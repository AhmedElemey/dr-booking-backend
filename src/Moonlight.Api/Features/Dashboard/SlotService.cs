using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Holidays;
using Moonlight.Api.Features.Templates;
using Moonlight.Api.Features.WaitingList;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Dashboard;

// Slot authoring for doctors / staff (spec section 3, "Slots: lifecycle and authoring").
public sealed class SlotService(MoonlightDbContext db, DashboardScope scope, TimeProvider clock, HolidayService holidays, WaitingListService waitingList)
{
    public const int MaxCalendarDays = 31;
    public const int MaxDeleteRangeDays = SlotGenerator.MaxDays;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    public async Task<List<ManagedSlotDto>> ListAsync(Guid doctorId, DateOnly? from, DateOnly? to, SlotStatus? status, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(doctorId, ct);
        var zone = ClinicTime.FindZone(doctor.Clinic!.Timezone);
        var fromDate = from ?? ClinicTime.LocalDate(Now, zone);
        var toDate = to ?? fromDate.AddDays(6);
        RequireRange(fromDate, toDate, MaxCalendarDays);

        var (start, end) = ClinicTime.DayRangeUtc(fromDate, toDate, zone);
        var query = db.Slots.AsNoTracking().Where(s => s.DoctorId == doctorId && s.StartsAt >= start && s.StartsAt < end);
        if (status is { } s)
        {
            query = query.Where(x => x.Status == s);
        }

        var slots = await query.OrderBy(x => x.StartsAt).ThenBy(x => x.Id).ToListAsync(ct);
        var slotIds = slots.Where(x => x.Status == SlotStatus.Reserved).Select(x => x.Id).ToList();
        var appointments = await db.Appointments.AsNoTracking().Include(a => a.Patient)
            .Where(a => slotIds.Contains(a.SlotId) && a.Status != AppointmentStatus.Cancelled)
            .ToDictionaryAsync(a => a.SlotId, ct);

        return slots.Select(x =>
        {
            appointments.TryGetValue(x.Id, out var appointment);
            return ToDto(x, zone, appointment);
        }).ToList();
    }

    public async Task<ManagedSlotDto> CreateAsync(Guid doctorId, CreateSlotDto request, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(doctorId, ct);
        var errors = new ValidationErrors()
            .Require(request.StartsAt is not null, "startsAt", "startsAt is required.")
            .Require(request.EndsAt is not null, "endsAt", "endsAt is required.");
        errors.ThrowIfAny();

        var startsAt = request.StartsAt!.Value.UtcDateTime;
        var endsAt = request.EndsAt!.Value.UtcDateTime;
        if (startsAt < Now)
        {
            throw ApiException.Validation("startsAt", "Slots can only be created in the future.");
        }

        if (!Slot.IsValidRange(startsAt, endsAt))
        {
            throw ApiException.Validation("endsAt", $"A slot is {Slot.MinLengthMinutes}-{Slot.MaxLengthMinutes} minutes long and starts and ends on a {Slot.AlignmentMinutes}-minute boundary.");
        }

        var slot = NewSlot(doctor, startsAt, endsAt);
        db.Slots.Add(slot);
        await SaveSlotsAsync(ct);
        await waitingList.NotifySlotsOpenedAsync(doctor.Id, [slot.StartsAt], ct);
        return ToDto(slot, ClinicTime.FindZone(doctor.Clinic!.Timezone), null);
    }

    public async Task<BulkSlotsResultDto> BulkCreateAsync(Guid doctorId, BulkSlotsDto request, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(doctorId, ct);
        new ValidationErrors()
            .Require(request.FromDate is not null, "fromDate", "fromDate is required.")
            .Require(request.ToDate is not null, "toDate", "toDate is required.")
            .Require(request.DaysOfWeek is { Count: > 0 }, "daysOfWeek", "Select at least one day of the week.")
            .Require(request.StartTime is not null, "startTime", "startTime is required (HH:mm).")
            .Require(request.EndTime is not null, "endTime", "endTime is required (HH:mm).")
            .Require(request.SlotMinutes is not null, "slotMinutes", "slotMinutes is required.")
            .ThrowIfAny();

        var spec = new BulkSlotSpec(
            request.FromDate!.Value,
            request.ToDate!.Value,
            request.DaysOfWeek!.Distinct().ToList(),
            request.StartTime!.Value,
            request.EndTime!.Value,
            request.SlotMinutes!.Value);
        return await GenerateAsync(doctor, [spec], ct);
    }

    // Template: every rule of the template expands like a bulk request over the same date range.
    public async Task<BulkSlotsResultDto> ApplyTemplateAsync(Guid doctorId, ApplyTemplateDto request, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(doctorId, ct);
        new ValidationErrors()
            .Require(request.TemplateId is not null, "templateId", "templateId is required.")
            .Require(request.FromDate is not null, "fromDate", "fromDate is required.")
            .Require(request.ToDate is not null, "toDate", "toDate is required.")
            .ThrowIfAny();
        var clinicId = scope.ClinicId;
        var template = await db.WorkingHoursTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == request.TemplateId && t.ClinicId == clinicId, ct)
            ?? throw ApiException.NotFound();

        var specs = template.Rules.Select(r => new BulkSlotSpec(
            request.FromDate!.Value,
            request.ToDate!.Value,
            [r.DayOfWeek],
            TimeOnly.ParseExact(r.Start, "HH:mm", CultureInfo.InvariantCulture),
            TimeOnly.ParseExact(r.End, "HH:mm", CultureInfo.InvariantCulture),
            template.SlotMinutes)).ToList();
        return await GenerateAsync(doctor, specs, ct);
    }

    // Shared by bulk creation and templates: past, overlapping and holiday slots are skipped and reported.
    private async Task<BulkSlotsResultDto> GenerateAsync(Doctor doctor, IReadOnlyList<BulkSlotSpec> specs, CancellationToken ct)
    {
        var zone = ClinicTime.FindZone(doctor.Clinic!.Timezone);
        var from = specs.Min(s => s.FromDate);
        var to = specs.Max(s => s.ToDate);

        // Existing slots around the requested window, to detect overlaps (one day of slack for DST / timezone edges).
        var (windowStart, windowEnd) = ClinicTime.DayRangeUtc(from.AddDays(-1), to.AddDays(1), zone);
        var existing = await db.Slots.AsNoTracking()
            .Where(s => s.DoctorId == doctor.Id && s.StartsAt < windowEnd && s.EndsAt > windowStart)
            .Select(s => new TimeRange(s.StartsAt, s.EndsAt))
            .ToListAsync(ct);
        var closed = (await holidays.ClosedAsync(doctor.ClinicId, from, to, ct)).Keys.ToHashSet();

        var created = new List<TimeRange>();
        var skipped = new List<SkippedSlot>();
        try
        {
            foreach (var spec in specs)
            {
                var result = SlotGenerator.Generate(spec, zone, Now, [.. existing, .. created], closed);
                created.AddRange(result.Created);
                skipped.AddRange(result.Skipped);
            }
        }
        catch (SlotGenerationException ex)
        {
            throw ApiException.Validation(ex.Field, ex.Message);
        }

        var now = Now;
        db.Slots.AddRange(created.Select(r => NewSlot(doctor, r.StartsAt, r.EndsAt, now)));
        await SaveSlotsAsync(ct);
        await waitingList.NotifySlotsOpenedAsync(doctor.Id, created.Select(r => r.StartsAt).ToList(), ct);

        var dtos = skipped.Select(s => new SkippedSlotDto(
            s.StartsAt,
            s.EndsAt,
            s.LocalStart.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            s.Reason == SkipReason.Holiday ? "holiday" : s.Reason.ToString())).ToList();
        return new BulkSlotsResultDto(created.Count, dtos.Count, dtos);
    }

    public async Task<ManagedSlotDto> BlockAsync(Guid doctorId, Guid slotId, CancellationToken ct)
    {
        var (doctor, slot) = await LoadAsync(doctorId, slotId, ct);
        if (slot.Status == SlotStatus.Reserved)
        {
            throw new ApiException(ErrorCodes.SlotReserved);
        }

        EnsureNotStarted(slot);

        slot.Status = SlotStatus.Blocked;
        await db.SaveChangesAsync(ct);
        return ToDto(slot, ClinicTime.FindZone(doctor.Clinic!.Timezone), null);
    }

    public async Task<ManagedSlotDto> UnblockAsync(Guid doctorId, Guid slotId, CancellationToken ct)
    {
        var (doctor, slot) = await LoadAsync(doctorId, slotId, ct);
        if (slot.Status == SlotStatus.Reserved)
        {
            throw new ApiException(ErrorCodes.SlotReserved);
        }

        EnsureNotStarted(slot);

        slot.Status = SlotStatus.Open;
        await db.SaveChangesAsync(ct);
        await waitingList.NotifySlotsOpenedAsync(doctor.Id, [slot.StartsAt], ct);
        return ToDto(slot, ClinicTime.FindZone(doctor.Clinic!.Timezone), null);
    }

    public async Task DeleteAsync(Guid doctorId, Guid slotId, CancellationToken ct)
    {
        var (_, slot) = await LoadAsync(doctorId, slotId, ct);
        if (slot.Status == SlotStatus.Reserved)
        {
            throw new ApiException(ErrorCodes.SlotReserved);
        }

        EnsureNotStarted(slot);

        // Conditional delete: a booking that lands between the read and here keeps the slot.
        var deleted = await db.Slots.Where(s => s.Id == slotId && s.Status != SlotStatus.Reserved).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw new ApiException(ErrorCodes.SlotReserved);
        }
    }

    public async Task<DeleteSlotsResultDto> DeleteRangeAsync(Guid doctorId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(doctorId, ct);
        new ValidationErrors()
            .Require(from is not null, "from", "from is required.")
            .Require(to is not null, "to", "to is required.")
            .ThrowIfAny();
        RequireRange(from!.Value, to!.Value, MaxDeleteRangeDays);

        var zone = ClinicTime.FindZone(doctor.Clinic!.Timezone);
        var (start, end) = ClinicTime.DayRangeUtc(from.Value, to.Value, zone);
        var lower = start > Now ? start : Now;
        var inRange = db.Slots.Where(s => s.DoctorId == doctorId && s.StartsAt >= lower && s.StartsAt < end);

        var skippedReserved = await inRange.CountAsync(s => s.Status == SlotStatus.Reserved, ct);
        var deleted = await inRange.Where(s => s.Status != SlotStatus.Reserved).ExecuteDeleteAsync(ct);
        return new DeleteSlotsResultDto(deleted, skippedReserved);
    }

    private async Task<(Doctor Doctor, Slot Slot)> LoadAsync(Guid doctorId, Guid slotId, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(doctorId, ct);
        var slot = await db.Slots.FirstOrDefaultAsync(s => s.Id == slotId && s.DoctorId == doctorId, ct) ?? throw ApiException.NotFound();
        return (doctor, slot);
    }

    private void EnsureNotStarted(Slot slot)
    {
        if (slot.StartsAt < Now)
        {
            throw new ApiException(ErrorCodes.SlotUnavailable, "Slots that have started are read-only.");
        }
    }

    private async Task SaveSlotsAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsExclusionViolation())
        {
            // The no_overlapping_slots exclusion constraint is the authority.
            throw new ApiException(ErrorCodes.SlotOverlap);
        }
    }

    private Slot NewSlot(Doctor doctor, DateTime startsAt, DateTime endsAt, DateTime? now = null) => new()
    {
        DoctorId = doctor.Id,
        ClinicId = doctor.ClinicId,
        StartsAt = startsAt,
        EndsAt = endsAt,
        Status = SlotStatus.Open,
        CreatedAt = now ?? Now,
    };

    private static void RequireRange(DateOnly from, DateOnly to, int maxDays)
    {
        if (to < from || to.DayNumber - from.DayNumber + 1 > maxDays)
        {
            throw ApiException.Validation("to", $"The date range must be between 1 and {maxDays} days.");
        }
    }

    private static ManagedSlotDto ToDto(Slot slot, TimeZoneInfo zone, Appointment? appointment)
    {
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(slot.StartsAt, DateTimeKind.Utc));
        return new ManagedSlotDto(
            slot.Id,
            slot.DoctorId,
            slot.StartsAt,
            slot.EndsAt,
            new DateTimeOffset(ClinicTime.ToLocal(slot.StartsAt, zone), offset),
            slot.Status,
            appointment?.Id,
            appointment?.Patient?.FullName,
            appointment?.Patient?.Phone);
    }
}
