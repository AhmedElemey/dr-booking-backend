using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Holidays;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Features.Payments;
using Moonlight.Api.Features.Promotions;
using Moonlight.Api.Features.WaitingList;
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
    FeatureService features,
    HolidayService holidays,
    WaitingListService waitingList,
    CouponService coupons,
    PaymentService payments,
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
        await EnsureClinicOpenAsync(slot, ct);
        var service = await ValidateServiceAsync(request.ServiceId, slot.DoctorId, ct);
        var extras = await BuildExtrasAsync(slot, service, current.Id, request, ct);

        var appointment = await CreateAsync(
            slot, current.Id, request.ServiceId, request.PatientNote, createdBy: current.Id, enforceLimits: true, extras, ct);
        await waitingList.MarkBookedAsync(current.Id, extras.FamilyMemberId, slot.DoctorId, slot.StartsAt, ct);
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
        await EnsureClinicOpenAsync(slot, ct);
        var service = await ValidateServiceAsync(request.ServiceId, slot.DoctorId, ct);
        var patient = await FindOrCreatePatientAsync(phone, request.PatientName!.Trim(), ct);
        var extras = await BuildExtrasAsync(slot, service, patient.Id, new BookAppointmentDto(slotId, request.ServiceId, request.PatientNote, null, request.Insurance, null, request.Mode), ct);

        var appointment = await CreateAsync(
            slot, patient.Id, request.ServiceId, request.PatientNote, createdBy: current.Id, enforceLimits: false, extras, ct);
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
        if (target == AppointmentStatus.Completed)
        {
            appointment.CompletedAt = Now;
        }

        await SaveTransitionAsync(ct);
        return await GetDashboardAsync(id, ct);
    }

    // Phase 2 booking details, already validated: ignored fields stay at their defaults.
    private sealed record BookingExtras(
        Guid? FamilyMemberId,
        AppointmentMode Mode,
        Guid? InsuranceProviderId,
        string? InsuranceMemberNumber,
        decimal? Price,
        CouponEvaluation? Coupon);

    private async Task<Appointment> CreateAsync(
        Slot slot, Guid patientId, Guid? serviceId, string? note, Guid createdBy, bool enforceLimits, BookingExtras extras, CancellationToken ct)
    {
        var now = Now;
        var minStart = enforceLimits ? now.AddMinutes(Booking.MinLeadMinutes) : now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Serialize one patient's concurrent bookings so the limit checks below cannot be raced.
        await LockPatientAsync(patientId, ct);
        await ReserveSlotAsync(slot.Id, minStart, ct);
        if (enforceLimits)
        {
            await EnforceLimitsAsync(patientId, extras.FamilyMemberId, slot, excludeAppointmentId: null, checkTotal: true, ct);
        }

        var appointment = new Appointment
        {
            SlotId = slot.Id,
            PatientUserId = patientId,
            FamilyMemberId = extras.FamilyMemberId,
            Mode = extras.Mode,
            PriceEgp = extras.Price,
            DiscountEgp = extras.Coupon?.DiscountEgp ?? 0m,
            CouponId = extras.Coupon?.Coupon?.Id,
            InsuranceProviderId = extras.InsuranceProviderId,
            InsuranceMemberNumber = extras.InsuranceMemberNumber,
            InsuranceStatus = extras.InsuranceProviderId is null ? null : InsuranceStatus.Pending,
            ServiceId = serviceId,
            PatientNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedByUserId = createdBy,
            Status = AppointmentStatus.Confirmed,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.Appointments.Add(appointment);
        if (extras.Coupon is { Coupon: { } coupon } evaluation)
        {
            await coupons.RedeemAsync(coupon, appointment.Id, patientId, evaluation.DiscountEgp, ct);
        }

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

        await EnsureClinicOpenAsync(target, ct);

        var now = Now;
        var minStart = patient ? now.AddMinutes(Booking.MinLeadMinutes) : now;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockPatientAsync(appointment.PatientUserId, ct);
        await ReserveSlotAsync(targetId, minStart, ct);
        if (patient)
        {
            await EnforceLimitsAsync(appointment.PatientUserId, appointment.FamilyMemberId, target, appointment.Id, checkTotal: false, ct);
        }

        appointment.SlotId = targetId;
        appointment.ReminderSentAt = null;
        appointment.UpdatedAt = now;
        oldSlot.Status = SlotStatus.Open;
        await SaveTransitionAsync(ct);
        await tx.CommitAsync(ct);
        await waitingList.NotifySlotsOpenedAsync(oldSlot.DoctorId, [oldSlot.StartsAt], ct);
    }

    private async Task CancelAsync(Appointment appointment, Slot slot, CancelledBy by, string? reason, CancellationToken ct)
    {
        appointment.Status = AppointmentStatus.Cancelled;
        appointment.CancelledBy = by;
        appointment.CancelReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
        appointment.UpdatedAt = Now;
        slot.Status = SlotStatus.Open;
        await SaveTransitionAsync(ct);
        await coupons.ReleaseAsync(appointment.Id, ct);
        await payments.RefundOnCancelAsync(appointment.Id, ct);
        await waitingList.NotifySlotsOpenedAsync(slot.DoctorId, [slot.StartsAt], ct);
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

    // Limits count per person: the account owner (familyMemberId null) and each family member have their own.
    private async Task EnforceLimitsAsync(Guid patientId, Guid? familyMemberId, Slot slot, Guid? excludeAppointmentId, bool checkTotal, CancellationToken ct)
    {
        var now = Now;
        var confirmed = db.Appointments.Where(a => a.PatientUserId == patientId && a.FamilyMemberId == familyMemberId && a.Status == AppointmentStatus.Confirmed);
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

    private async Task<Service?> ValidateServiceAsync(Guid? serviceId, Guid doctorId, CancellationToken ct)
    {
        if (serviceId is not { } id)
        {
            return null;
        }

        return await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id && s.DoctorId == doctorId && s.IsActive, ct)
            ?? throw ApiException.Validation("serviceId", "The service does not exist for this doctor.");
    }

    // Clinic holidays close the doctor's calendar (holidayCalendar feature): booking into one is CLINIC_CLOSED.
    private async Task EnsureClinicOpenAsync(Slot slot, CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.HolidayCalendar))
        {
            return;
        }

        var timezone = await db.Clinics.Where(c => c.Id == slot.ClinicId).Select(c => c.Timezone).FirstAsync(ct);
        var date = ClinicTime.LocalDate(slot.StartsAt, ClinicTime.FindZone(timezone));
        if (await holidays.FindAsync(slot.ClinicId, date, ct) is { } holiday)
        {
            throw new ApiException(
                ErrorCodes.ClinicClosed,
                extensions: new Dictionary<string, object?> { ["date"] = date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), ["holiday"] = holiday.NameEn });
        }
    }

    // Validates the feature-gated booking fields. Fields of a feature that is switched off are ignored, never rejected.
    private async Task<BookingExtras> BuildExtrasAsync(Slot slot, Service? service, Guid patientId, BookAppointmentDto request, CancellationToken ct)
    {
        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Clinic).FirstAsync(d => d.Id == slot.DoctorId, ct);

        Guid? familyMemberId = null;
        if (features.IsEnabled(FeatureKeys.FamilyAccounts) && request.FamilyMemberId is { } memberId)
        {
            if (!await db.FamilyMembers.AnyAsync(f => f.Id == memberId && f.OwnerUserId == patientId, ct))
            {
                throw ApiException.Validation("familyMemberId", "The family member does not exist.");
            }

            familyMemberId = memberId;
        }

        var mode = AppointmentMode.InPerson;
        if (features.IsEnabled(FeatureKeys.VideoConsultation) && request.Mode == AppointmentMode.Video)
        {
            if (!doctor.OffersVideo)
            {
                throw ApiException.Unprocessable("mode", "This doctor does not offer video consultations.");
            }

            mode = AppointmentMode.Video;
        }

        Guid? insuranceProviderId = null;
        string? memberNumber = null;
        if (features.IsEnabled(FeatureKeys.Insurance) && request.Insurance is { } insurance)
        {
            new ValidationErrors()
                .Require(insurance.ProviderId is not null, "insurance.providerId", "providerId is required.")
                .RequireText(insurance.MemberNumber, "insurance.memberNumber", 100)
                .ThrowIfAny();
            var accepted = await db.DoctorInsuranceProviders.AnyAsync(
                d => d.DoctorId == doctor.Id && d.InsuranceProviderId == insurance.ProviderId && d.InsuranceProvider!.IsActive, ct);
            if (!accepted)
            {
                throw ApiException.Unprocessable("insurance.providerId", "The doctor does not accept this insurance provider.");
            }

            insuranceProviderId = insurance.ProviderId;
            memberNumber = insurance.MemberNumber!.Trim();
        }

        CouponEvaluation? evaluation = null;
        if (features.IsEnabled(FeatureKeys.Promotions) && !string.IsNullOrWhiteSpace(request.CouponCode))
        {
            evaluation = await coupons.EvaluateAsync(request.CouponCode, doctor, service, patientId, ct);
            if (!evaluation.Valid)
            {
                throw CouponService.Invalid(evaluation.Reason!);
            }
        }

        return new BookingExtras(familyMemberId, mode, insuranceProviderId, memberNumber, CouponService.PriceOf(doctor, service), evaluation);
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
