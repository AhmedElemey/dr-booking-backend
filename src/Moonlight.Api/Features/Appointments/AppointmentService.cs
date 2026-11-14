using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Appointments;

// Booking, rescheduling and the appointment status machine (spec section 3).
// Double-booking is prevented by the conditional `UPDATE slots ... WHERE status = 'Open'` inside one transaction;
// the unique partial index on appointments(slot_id) is the backstop.
public sealed class AppointmentService(
    MoonlightDbContext db,
    CurrentUser current,
    DashboardScope scope,
    AppointmentMapper mapper,
    INotificationService notifications,
    TimeProvider clock,
    IOptions<BookingOptions> bookingOptions)
{
    private const string ActiveSlotIndex = "ux_appointments_active_slot";

    private BookingOptions Booking => bookingOptions.Value;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Patient ----------------------------------------------------------------------------------------------

    public async Task<AppointmentDto> PatientBookAsync(BookAppointmentDto request, CancellationToken ct)
    {
        var slotId = RequireSlotId(request.SlotId);
        ValidateNote(request.PatientNote);
        var slot = await FindBookableSlotAsync(slotId, patient: true, ct);
        await ValidateServiceAsync(request.ServiceId, slot.DoctorId, ct);

        var appointment = await CreateAsync(
            slot, current.Id, request.ServiceId, request.PatientNote, createdBy: current.Id, enforceLimits: true, ct);
        await notifications.AppointmentConfirmedAsync(appointment.Id, ct);
        return await GetPatientAsync(appointment.Id, ct);
    }

    public async Task<PagedResult<AppointmentDto>> PatientListAsync(string? scopeName, PageRequest page, CancellationToken ct)
    {
        var upcoming = scopeName is null || scopeName.Equals("upcoming", StringComparison.OrdinalIgnoreCase);
        if (!upcoming && !scopeName!.Equals("past", StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.Validation("scope", "scope must be 'upcoming' or 'past'.");
        }

        var userId = current.Id;
        var now = Now;
        var query = AppointmentMapper.WithDetails(db.Appointments.AsNoTracking()).Where(a => a.PatientUserId == userId);
        query = upcoming
            ? query.Where(a => a.Status == AppointmentStatus.Confirmed && a.Slot!.StartsAt >= now)
            : query.Where(a => a.Status != AppointmentStatus.Confirmed || a.Slot!.StartsAt < now);
        var ordered = upcoming
            ? query.OrderBy(a => a.Slot!.StartsAt).ThenBy(a => a.Id)
            : query.OrderByDescending(a => a.Slot!.StartsAt).ThenBy(a => a.Id);
        return await ordered.ToPagedAsync(page, rows => Task.FromResult<IReadOnlyList<AppointmentDto>>(rows.Select(mapper.ToPatientDto).ToList()), ct);
    }

    public async Task<AppointmentDto> GetPatientAsync(Guid id, CancellationToken ct)
    {
        var userId = current.Id;
        var appointment = await AppointmentMapper.WithDetails(db.Appointments.AsNoTracking())
            .FirstOrDefaultAsync(a => a.Id == id && a.PatientUserId == userId, ct) ?? throw ApiException.NotFound();
        return mapper.ToPatientDto(appointment);
    }

    public async Task<AppointmentDto> PatientCancelAsync(Guid id, string? reason, CancellationToken ct)
    {
        ValidateReason(reason);
        var userId = current.Id;
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id && a.PatientUserId == userId, ct)
            ?? throw ApiException.NotFound();
        EnsureConfirmed(appointment);
        var slot = await db.Slots.FirstAsync(s => s.Id == appointment.SlotId, ct);
        if (!AppointmentRules.IsInsidePatientWindow(Now, slot.StartsAt, TimeSpan.FromHours(Booking.PatientCancelWindowHours)))
        {
            throw new ApiException(ErrorCodes.CancelWindowPassed);
        }

        await CancelAsync(appointment, slot, CancelledBy.Patient, reason, ct);
        return await GetPatientAsync(id, ct);
    }

    public async Task<AppointmentDto> PatientRescheduleAsync(Guid id, RescheduleAppointmentDto request, CancellationToken ct)
    {
        var targetId = RequireSlotId(request.SlotId);
        var userId = current.Id;
        var appointment = await db.Appointments.FirstOrDefaultAsync(a => a.Id == id && a.PatientUserId == userId, ct)
            ?? throw ApiException.NotFound();
        EnsureConfirmed(appointment);
        var oldSlot = await db.Slots.FirstAsync(s => s.Id == appointment.SlotId, ct);
        if (!AppointmentRules.IsInsidePatientWindow(Now, oldSlot.StartsAt, TimeSpan.FromHours(Booking.PatientCancelWindowHours)))
        {
            throw new ApiException(ErrorCodes.CancelWindowPassed);
        }

        await RescheduleAsync(appointment, oldSlot, targetId, patient: true, ct);
        await notifications.AppointmentConfirmedAsync(id, ct);
        return await GetPatientAsync(id, ct);
    }

    // ---- Dashboard (Doctor / ClinicStaff) -------------------------------------------------------------------------

    public async Task<PagedResult<DashboardAppointmentDto>> DashboardListAsync(
        Guid? doctorId, AppointmentStatus? status, DateOnly? from, DateOnly? to, PageRequest page, CancellationToken ct)
    {
        var query = AppointmentMapper.WithDetails(scope.Appointments().AsNoTracking());
        if (doctorId is { } doctor)
        {
            if (!await scope.Doctors().AnyAsync(d => d.Id == doctor, ct))
            {
                throw ApiException.NotFound();
            }

            query = query.Where(a => a.Slot!.DoctorId == doctor);
        }

        if (status is { } s)
        {
            query = query.Where(a => a.Status == s);
        }

        if (from is not null || to is not null)
        {
            var zone = ClinicTime.FindZone((await scope.RequireClinicAsync(ct)).Timezone);
            if (from is { } fromDate)
            {
                var start = ClinicTime.StartOfDayUtc(fromDate, zone);
                query = query.Where(a => a.Slot!.StartsAt >= start);
            }

            if (to is { } toDate)
            {
                var end = ClinicTime.StartOfDayUtc(toDate.AddDays(1), zone);
                query = query.Where(a => a.Slot!.StartsAt < end);
            }
        }

        return await query.OrderBy(a => a.Slot!.StartsAt).ThenBy(a => a.Id).ToPagedAsync(
            page, rows => Task.FromResult<IReadOnlyList<DashboardAppointmentDto>>(rows.Select(mapper.ToDashboardDto).ToList()), ct);
    }

    public async Task<DashboardAppointmentDto> GetDashboardAsync(Guid id, CancellationToken ct)
    {
        var appointment = await AppointmentMapper.WithDetails(scope.Appointments().AsNoTracking())
            .FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound();
        return mapper.ToDashboardDto(appointment);
    }

    public async Task<DashboardAppointmentDto> DashboardBookAsync(DashboardBookAppointmentDto request, CancellationToken ct)
    {
        var slotId = RequireSlotId(request.SlotId);
        var phone = PhoneNumber.Normalize(request.PatientPhone, "patientPhone");
        new ValidationErrors().RequireText(request.PatientName, "patientName", 200).ThrowIfAny();
        ValidateNote(request.PatientNote);

        var slot = await FindBookableSlotAsync(slotId, patient: false, ct);
        await ValidateServiceAsync(request.ServiceId, slot.DoctorId, ct);
        var patient = await FindOrCreatePatientAsync(phone, request.PatientName!.Trim(), ct);

        var appointment = await CreateAsync(
            slot, patient.Id, request.ServiceId, request.PatientNote, createdBy: current.Id, enforceLimits: false, ct);
        await notifications.AppointmentConfirmedAsync(appointment.Id, ct);
        return await GetDashboardAsync(appointment.Id, ct);
    }

    public async Task<DashboardAppointmentDto> DashboardRescheduleAsync(Guid id, RescheduleAppointmentDto request, CancellationToken ct)
    {
        var targetId = RequireSlotId(request.SlotId);
        var appointment = await LoadScopedAsync(id, ct);
        EnsureConfirmed(appointment);
        var oldSlot = await db.Slots.FirstAsync(s => s.Id == appointment.SlotId, ct);
        await RescheduleAsync(appointment, oldSlot, targetId, patient: false, ct);
        await notifications.AppointmentConfirmedAsync(id, ct);
        return await GetDashboardAsync(id, ct);
    }

    public async Task<DashboardAppointmentDto> DashboardCancelAsync(Guid id, string? reason, CancellationToken ct)
    {
        ValidateReason(reason);
        var appointment = await LoadScopedAsync(id, ct);
        EnsureConfirmed(appointment);
        var slot = await db.Slots.FirstAsync(s => s.Id == appointment.SlotId, ct);
        if (!AppointmentRules.CanClinicCancel(Now, slot.StartsAt))
        {
            throw new ApiException(ErrorCodes.InvalidTransition, "The visit has already started; mark it completed or no-show instead.");
        }

        await CancelAsync(appointment, slot, CancelledBy.Clinic, reason, ct);
        await notifications.AppointmentCancelledByClinicAsync(id, ct);
        return await GetDashboardAsync(id, ct);
    }

    public Task<DashboardAppointmentDto> CompleteAsync(Guid id, CancellationToken ct) =>
        CloseOutAsync(id, AppointmentStatus.Completed, ct);

    public Task<DashboardAppointmentDto> NoShowAsync(Guid id, CancellationToken ct) =>
        CloseOutAsync(id, AppointmentStatus.NoShow, ct);

    // ---- Core ---------------------------------------------------------------------------------------------------

    private async Task<DashboardAppointmentDto> CloseOutAsync(Guid id, AppointmentStatus target, CancellationToken ct)
    {
        var appointment = await LoadScopedAsync(id, ct);
        EnsureConfirmed(appointment);
        var startsAt = await db.Slots.Where(s => s.Id == appointment.SlotId).Select(s => s.StartsAt).FirstAsync(ct);
        if (!AppointmentRules.CanCloseOut(Now, startsAt))
        {
            throw new ApiException(ErrorCodes.InvalidTransition, "The visit has not started yet.");
        }

        appointment.Status = target;
        appointment.UpdatedAt = Now;
        await SaveTransitionAsync(ct);
        return await GetDashboardAsync(id, ct);
    }

    private async Task<Appointment> CreateAsync(
        Slot slot, Guid patientId, Guid? serviceId, string? note, Guid createdBy, bool enforceLimits, CancellationToken ct)
    {
        var now = Now;
        var minStart = enforceLimits ? now.AddMinutes(Booking.MinLeadMinutes) : now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Serialize one patient's concurrent bookings so the limit checks below cannot be raced.
        await LockPatientAsync(patientId, ct);
        await ReserveSlotAsync(slot.Id, minStart, ct);
        if (enforceLimits)
        {
            await EnforceLimitsAsync(patientId, slot, excludeAppointmentId: null, checkTotal: true, ct);
        }

        var appointment = new Appointment
        {
            SlotId = slot.Id,
            PatientUserId = patientId,
            ServiceId = serviceId,
            PatientNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedByUserId = createdBy,
            Status = AppointmentStatus.Confirmed,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Appointments.Add(appointment);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(ActiveSlotIndex))
        {
            throw new ApiException(ErrorCodes.SlotTaken);
        }

        await tx.CommitAsync(ct);
        return appointment;
    }

    private async Task RescheduleAsync(Appointment appointment, Slot oldSlot, Guid targetId, bool patient, CancellationToken ct)
    {
        var target = await FindBookableSlotAsync(targetId, patient, ct);
        if (target.DoctorId != oldSlot.DoctorId)
        {
            throw ApiException.Validation("slotId", "The new slot must belong to the same doctor.");
        }

        var now = Now;
        var minStart = patient ? now.AddMinutes(Booking.MinLeadMinutes) : now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockPatientAsync(appointment.PatientUserId, ct);
        await ReserveSlotAsync(targetId, minStart, ct);
        if (patient)
        {
            await EnforceLimitsAsync(appointment.PatientUserId, target, appointment.Id, checkTotal: false, ct);
        }

        appointment.SlotId = targetId;
        appointment.ReminderSentAt = null;
        appointment.UpdatedAt = now;
        oldSlot.Status = SlotStatus.Open;
        await SaveTransitionAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task CancelAsync(Appointment appointment, Slot slot, CancelledBy by, string? reason, CancellationToken ct)
    {
        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledBy = by;
        appointment.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        appointment.UpdatedAt = Now;
        slot.Status = SlotStatus.Open;
        await SaveTransitionAsync(ct);
    }

    // Optimistic concurrency (xmin): of two simultaneous transitions on one appointment, the loser gets INVALID_TRANSITION.
    private async Task SaveTransitionAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ApiException(ErrorCodes.InvalidTransition, "The appointment was changed by someone else. Reload and try again.");
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(ActiveSlotIndex))
        {
            throw new ApiException(ErrorCodes.SlotTaken);
        }
    }

    private async Task ReserveSlotAsync(Guid slotId, DateTime minStart, CancellationToken ct)
    {
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE slots SET status = 'Reserved' WHERE id = {slotId} AND status = 'Open' AND starts_at >= {minStart}", ct);
        if (updated == 1)
        {
            return;
        }

        var status = await db.Slots.AsNoTracking().Where(s => s.Id == slotId).Select(s => (SlotStatus?)s.Status).FirstOrDefaultAsync(ct);
        throw status switch
        {
            null => ApiException.NotFound(),
            SlotStatus.Open => new ApiException(ErrorCodes.SlotUnavailable),
            _ => new ApiException(ErrorCodes.SlotTaken),
        };
    }

    private Task LockPatientAsync(Guid patientId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({patientId.ToString()}, 0))", ct);

    private async Task EnforceLimitsAsync(Guid patientId, Slot slot, Guid? excludeAppointmentId, bool checkTotal, CancellationToken ct)
    {
        var now = Now;
        var confirmed = db.Appointments.Where(a => a.PatientUserId == patientId && a.Status == AppointmentStatus.Confirmed);
        if (excludeAppointmentId is { } excluded)
        {
            confirmed = confirmed.Where(a => a.Id != excluded);
        }

        if (checkTotal)
        {
            var upcoming = await confirmed.CountAsync(a => a.Slot!.StartsAt > now, ct);
            if (upcoming >= Booking.MaxUpcomingPerPatient)
            {
                throw new ApiException(ErrorCodes.BookingLimit);
            }
        }

        var clinicTimezone = await db.Doctors.Where(d => d.Id == slot.DoctorId).Select(d => d.Clinic!.Timezone).FirstAsync(ct);
        var zone = ClinicTime.FindZone(clinicTimezone);
        var day = ClinicTime.LocalDate(slot.StartsAt, zone);
        var (start, end) = ClinicTime.DayRangeUtc(day, day, zone);
        var sameDay = await confirmed.AnyAsync(a => a.Slot!.DoctorId == slot.DoctorId && a.Slot.StartsAt >= start && a.Slot.StartsAt < end, ct);
        if (sameDay)
        {
            throw new ApiException(ErrorCodes.BookingLimit);
        }
    }

    // Loads the slot the caller wants to book; slots outside the caller's visibility do not exist (404).
    private async Task<Slot> FindBookableSlotAsync(Guid slotId, bool patient, CancellationToken ct)
    {
        IQueryable<Slot> query = patient
            ? db.Slots.Where(s => s.Doctor!.IsPublished && s.Doctor.Clinic!.IsActive && s.Doctor.User!.IsActive)
            : scope.Slots();
        return await query.AsNoTracking().FirstOrDefaultAsync(s => s.Id == slotId, ct) ?? throw ApiException.NotFound();
    }

    private async Task ValidateServiceAsync(Guid? serviceId, Guid doctorId, CancellationToken ct)
    {
        if (serviceId is not { } id)
        {
            return;
        }

        var valid = await db.Services.AnyAsync(s => s.Id == id && s.DoctorId == doctorId && s.IsActive, ct);
        if (!valid)
        {
            throw ApiException.Validation("serviceId", "The service does not exist for this doctor.");
        }
    }

    private async Task<User> FindOrCreatePatientAsync(string phone, string name, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == phone, ct);
            if (user is not null)
            {
                if (user.Role != UserRole.Patient)
                {
                    throw new ApiException(ErrorCodes.PhoneInUse);
                }

                if (string.IsNullOrWhiteSpace(user.FullName))
                {
                    user.FullName = name;
                    await db.SaveChangesAsync(ct);
                }

                return user;
            }

            user = new User { Phone = phone, FullName = name, Role = UserRole.Patient, CreatedAt = Now };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
                return user;
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                db.Entry(user).State = EntityState.Detached;
            }
        }

        throw new ApiException(ErrorCodes.PhoneInUse);
    }

    private async Task<Appointment> LoadScopedAsync(Guid id, CancellationToken ct) =>
        await scope.Appointments().FirstOrDefaultAsync(a => a.Id == id, ct) ?? throw ApiException.NotFound();

    private static void EnsureConfirmed(Appointment appointment)
    {
        if (!AppointmentRules.CanLeave(appointment.Status))
        {
            throw new ApiException(ErrorCodes.InvalidTransition, $"The appointment is already {appointment.Status}.");
        }
    }

    private static Guid RequireSlotId(Guid? slotId) =>
        slotId ?? throw ApiException.Validation("slotId", "slotId is required.");

    private static void ValidateNote(string? note) =>
        new ValidationErrors().RequireText(note, "patientNote", 1000, required: false).ThrowIfAny();

    private static void ValidateReason(string? reason) =>
        new ValidationErrors().RequireText(reason, "reason", 500, required: false).ThrowIfAny();
}
