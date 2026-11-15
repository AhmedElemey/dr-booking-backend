using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Family;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.WaitingList;

public sealed record JoinWaitingListDto(Guid? DoctorId, DateOnly? Date, Guid? ServiceId, Guid? FamilyMemberId);

public sealed record WaitingListEntryDto(Guid Id, DoctorRefDto Doctor, DateOnly Date, WaitingListStatus Status, DateTime? NotifiedAt, FamilyMemberRefDto? FamilyMember);

public sealed record JoinedWaitingListDto(
    Guid Id,
    DoctorRefDto Doctor,
    DateOnly Date,
    Guid? ServiceId,
    Guid? FamilyMemberId,
    WaitingListStatus Status,
    DateTime? NotifiedAt,
    DateTime CreatedAt);

public sealed record DashboardWaitingListEntryDto(
    Guid Id,
    DashboardDoctorRefDto Doctor,
    PatientRefDto Patient,
    FamilyMemberRefDto? FamilyMember,
    DateOnly Date,
    Guid? ServiceId,
    WaitingListStatus Status,
    DateTime? NotifiedAt,
    DateTime CreatedAt);

// The waiting list: patients ask to be told when a day of a fully booked doctor frees up.
public sealed class WaitingListService(
    MoonlightDbContext db,
    CurrentUser current,
    DashboardScope scope,
    Language lang,
    FeatureService features,
    INotificationService notifications,
    TimeProvider clock,
    ILogger<WaitingListService> logger)
{
    private const string ActiveIndex = "ux_waiting_list_active";

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Patient ------------------------------------------------------------------------------------------------

    public async Task<JoinedWaitingListDto> JoinAsync(JoinWaitingListDto request, CancellationToken ct)
    {
        new ValidationErrors()
            .Require(request.DoctorId is not null, "doctorId", "doctorId is required.")
            .Require(request.Date is not null, "date", "date is required.")
            .ThrowIfAny();

        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Clinic)
            .Where(d => d.IsPublished && d.Clinic!.IsActive && d.User!.IsActive)
            .FirstOrDefaultAsync(d => d.Id == request.DoctorId, ct) ?? throw ApiException.NotFound();

        var zone = ClinicTime.FindZone(doctor.Clinic!.Timezone);
        if (request.Date < ClinicTime.LocalDate(Now, zone))
        {
            throw ApiException.Validation("date", "The date is in the past.");
        }

        if (request.ServiceId is { } serviceId && !await db.Services.AnyAsync(s => s.Id == serviceId && s.DoctorId == doctor.Id && s.IsActive, ct))
        {
            throw ApiException.Validation("serviceId", "The service does not exist for this doctor.");
        }

        var userId = current.Id;
        var familyMemberId = features.IsEnabled(FeatureKeys.FamilyAccounts) ? request.FamilyMemberId : null;
        if (familyMemberId is { } memberId && !await db.FamilyMembers.AnyAsync(f => f.Id == memberId && f.OwnerUserId == userId && f.DeletedAt == null, ct))
        {
            throw ApiException.Validation("familyMemberId", "The family member does not exist.");
        }

        var duplicate = await db.WaitingListEntries.AnyAsync(w =>
            w.PatientUserId == userId && w.DoctorId == doctor.Id && w.Date == request.Date
            && w.FamilyMemberId == familyMemberId && (w.Status == WaitingListStatus.Waiting || w.Status == WaitingListStatus.Notified), ct);
        if (duplicate)
        {
            throw new ApiException(ErrorCodes.AlreadyWaiting);
        }

        var entry = new WaitingListEntry
        {
            PatientUserId = userId,
            FamilyMemberId = familyMemberId,
            DoctorId = doctor.Id,
            Date = request.Date!.Value,
            ServiceId = request.ServiceId,
            CreatedAt = Now,
        };
        db.WaitingListEntries.Add(entry);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation(ActiveIndex))
        {
            throw new ApiException(ErrorCodes.AlreadyWaiting);
        }

        return new JoinedWaitingListDto(entry.Id, DoctorRef(doctor), entry.Date, entry.ServiceId, entry.FamilyMemberId, entry.Status, entry.NotifiedAt, entry.CreatedAt);
    }

    // Active entries and those that ended in the last 30 days.
    public async Task<List<WaitingListEntryDto>> MineAsync(CancellationToken ct)
    {
        var userId = current.Id;
        var since = ClinicTime.LocalDate(Now.AddDays(-30), ClinicTime.FindZone(Clinic.DefaultTimezone));
        var rows = await db.WaitingListEntries.AsNoTracking().Include(w => w.Doctor)
            .Where(w => w.PatientUserId == userId && (w.Status == WaitingListStatus.Waiting || w.Status == WaitingListStatus.Notified || w.Date >= since))
            .OrderByDescending(w => w.Date).ThenBy(w => w.CreatedAt).ThenBy(w => w.Id)
            .ToListAsync(ct);
        var memberIds = rows.Where(w => w.FamilyMemberId != null).Select(w => w.FamilyMemberId!.Value).Distinct().ToList();
        var members = await db.FamilyMembers.AsNoTracking().Where(f => memberIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        return rows.Select(w => new WaitingListEntryDto(
            w.Id,
            DoctorRef(w.Doctor!),
            w.Date,
            w.Status,
            w.NotifiedAt,
            w.FamilyMemberId is { } m && members.TryGetValue(m, out var member) ? new FamilyMemberRefDto(member.Id, member.FullName, member.Relation) : null)).ToList();
    }

    public async Task CancelAsync(Guid id, CancellationToken ct)
    {
        var userId = current.Id;
        var entry = await db.WaitingListEntries.FirstOrDefaultAsync(w => w.Id == id && w.PatientUserId == userId, ct) ?? throw ApiException.NotFound();
        if (entry.Status is WaitingListStatus.Waiting or WaitingListStatus.Notified)
        {
            entry.Status = WaitingListStatus.Cancelled;
            await db.SaveChangesAsync(ct);
        }
    }

    // ---- Dashboard ----------------------------------------------------------------------------------------------

    public async Task<List<DashboardWaitingListEntryDto>> DashboardListAsync(Guid? doctorId, DateOnly? date, WaitingListStatus? status, CancellationToken ct)
    {
        var doctorIds = scope.Doctors().Select(d => d.Id);
        var query = db.WaitingListEntries.AsNoTracking().Include(w => w.Doctor).Include(w => w.Patient)
            .Where(w => doctorIds.Contains(w.DoctorId));
        if (doctorId is { } d)
        {
            query = query.Where(w => w.DoctorId == d);
        }

        if (date is { } day)
        {
            query = query.Where(w => w.Date == day);
        }

        if (status is { } s)
        {
            query = query.Where(w => w.Status == s);
        }

        var rows = await query.OrderBy(w => w.Date).ThenBy(w => w.CreatedAt).ThenBy(w => w.Id).ToListAsync(ct);
        var memberIds = rows.Where(w => w.FamilyMemberId != null).Select(w => w.FamilyMemberId!.Value).Distinct().ToList();
        var members = await db.FamilyMembers.AsNoTracking().Where(f => memberIds.Contains(f.Id)).ToDictionaryAsync(f => f.Id, ct);
        return rows.Select(w => new DashboardWaitingListEntryDto(
            w.Id,
            new DashboardDoctorRefDto(w.DoctorId, lang.Pick(w.Doctor!.NameAr, w.Doctor.NameEn)),
            new PatientRefDto(w.PatientUserId, w.Patient!.FullName, w.Patient.Phone),
            w.FamilyMemberId is { } m && members.TryGetValue(m, out var member) ? new FamilyMemberRefDto(member.Id, member.FullName, member.Relation) : null,
            w.Date,
            w.ServiceId,
            w.Status,
            w.NotifiedAt,
            w.CreatedAt)).ToList();
    }

    // Manual notify: allowed for entries that are still waiting (or were notified before).
    public async Task NotifyAsync(Guid id, CancellationToken ct)
    {
        var doctorIds = scope.Doctors().Select(d => d.Id);
        var entry = await db.WaitingListEntries.FirstOrDefaultAsync(w => w.Id == id && doctorIds.Contains(w.DoctorId), ct) ?? throw ApiException.NotFound();
        if (entry.Status is not (WaitingListStatus.Waiting or WaitingListStatus.Notified))
        {
            throw new ApiException(ErrorCodes.InvalidTransition, $"The entry is already {entry.Status}.");
        }

        entry.Status = WaitingListStatus.Notified;
        entry.NotifiedAt = Now;
        await db.SaveChangesAsync(ct);
        await notifications.WaitingListSlotOpenedAsync(entry.Id, ct);
    }

    // ---- Hooks used by booking, slot management and the background job -----------------------------------------------

    /// <summary>Slots of the doctor became Open (cancel, unblock, new slot): tell every Waiting entry of those days, first come first served.</summary>
    public async Task NotifySlotsOpenedAsync(Guid doctorId, IReadOnlyCollection<DateTime> startsAtUtc, CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.WaitingList) || startsAtUtc.Count == 0)
        {
            return;
        }

        try
        {
            var timezone = await db.Doctors.Where(d => d.Id == doctorId).Select(d => d.Clinic!.Timezone).FirstOrDefaultAsync(ct);
            if (timezone is null)
            {
                return;
            }

            var zone = ClinicTime.FindZone(timezone);
            var today = ClinicTime.LocalDate(Now, zone);
            var dates = startsAtUtc.Select(s => ClinicTime.LocalDate(s, zone)).Where(d => d >= today).Distinct().ToList();
            if (dates.Count == 0)
            {
                return;
            }

            var waiting = await db.WaitingListEntries.AsNoTracking()
                .Where(w => w.DoctorId == doctorId && w.Status == WaitingListStatus.Waiting && dates.Contains(w.Date))
                .OrderBy(w => w.CreatedAt).ThenBy(w => w.Id)
                .Select(w => w.Id)
                .ToListAsync(ct);
            foreach (var id in waiting)
            {
                // The status guard makes the claim atomic: concurrent triggers notify each entry once.
                var now = Now;
                var claimed = await db.WaitingListEntries.Where(w => w.Id == id && w.Status == WaitingListStatus.Waiting)
                    .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WaitingListStatus.Notified).SetProperty(w => w.NotifiedAt, now), ct);
                if (claimed == 1)
                {
                    await notifications.WaitingListSlotOpenedAsync(id, ct);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Waiting-list notification for doctor {DoctorId} failed.", doctorId);
        }
    }

    /// <summary>The patient booked the doctor that day: their waiting entries are fulfilled.</summary>
    public async Task MarkBookedAsync(Guid patientUserId, Guid? familyMemberId, Guid doctorId, DateTime startsAtUtc, CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.WaitingList))
        {
            return;
        }

        var timezone = await db.Doctors.Where(d => d.Id == doctorId).Select(d => d.Clinic!.Timezone).FirstAsync(ct);
        var date = ClinicTime.LocalDate(startsAtUtc, ClinicTime.FindZone(timezone));
        await db.WaitingListEntries
            .Where(w => w.PatientUserId == patientUserId && w.FamilyMemberId == familyMemberId && w.DoctorId == doctorId && w.Date == date
                && (w.Status == WaitingListStatus.Waiting || w.Status == WaitingListStatus.Notified))
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WaitingListStatus.Booked), ct);
    }

    /// <summary>Entries whose day has passed (clinic-local) become Expired.</summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var now = Now;
        var candidates = await db.WaitingListEntries
            .Where(w => (w.Status == WaitingListStatus.Waiting || w.Status == WaitingListStatus.Notified) && w.Date <= DateOnly.FromDateTime(now.AddDays(1)))
            .Select(w => new { w.Id, w.Date, w.Doctor!.Clinic!.Timezone })
            .ToListAsync(ct);
        var expired = candidates
            .Where(c => c.Date < ClinicTime.LocalDate(now, ClinicTime.FindZone(c.Timezone)))
            .Select(c => c.Id)
            .ToList();
        if (expired.Count == 0)
        {
            return 0;
        }

        return await db.WaitingListEntries.Where(w => expired.Contains(w.Id))
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WaitingListStatus.Expired), ct);
    }

    private DoctorRefDto DoctorRef(Doctor d) => new(d.Id, d.Slug, lang.Pick(d.NameAr, d.NameEn), d.PhotoUrl);
}
