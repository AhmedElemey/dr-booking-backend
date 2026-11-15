using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Holidays;

public sealed record PublicHolidayDto(DateOnly Date, string Name);

public sealed record HolidayDto(Guid Id, Guid ClinicId, DateOnly Date, string NameAr, string NameEn, HolidaySource Source);

public sealed record CreateHolidayDto(Guid? ClinicId, DateOnly? Date, string? NameAr, string? NameEn);

public sealed record ImportHolidaysDto(Guid? ClinicId, int? Year);

public sealed record ImportHolidaysResultDto(int Created, int Skipped);

public sealed record ClosedDateDto(DateOnly Date, string Name);

// Closed days of a clinic. Every read answers "nothing is closed" while the holidayCalendar feature is off, so callers
// (slot listing, booking, bulk creation) need no feature checks of their own.
public sealed class HolidayService(
    MoonlightDbContext db,
    DashboardScope scope,
    AppointmentMapper mapper,
    Language lang,
    FeatureService features)
{
    public const int MaxRangeDays = 366;

    public async Task<Dictionary<DateOnly, ClinicHoliday>> ClosedAsync(Guid clinicId, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.HolidayCalendar))
        {
            return [];
        }

        return await db.ClinicHolidays.AsNoTracking()
            .Where(h => h.ClinicId == clinicId && h.Date >= from && h.Date <= to)
            .ToDictionaryAsync(h => h.Date, ct);
    }

    public async Task<ClinicHoliday?> FindAsync(Guid clinicId, DateOnly date, CancellationToken ct) =>
        (await ClosedAsync(clinicId, date, date, ct)).GetValueOrDefault(date);

    public async Task<List<PublicHolidayDto>> PublicListAsync(Guid clinicId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (!await db.Clinics.AnyAsync(c => c.Id == clinicId && c.IsActive, ct))
        {
            throw ApiException.NotFound();
        }

        var fromDate = from ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var toDate = to ?? fromDate.AddDays(MaxRangeDays - 1);
        RequireRange(fromDate, toDate);
        var holidays = await ClosedAsync(clinicId, fromDate, toDate, ct);
        return holidays.Values.OrderBy(h => h.Date).Select(h => new PublicHolidayDto(h.Date, lang.Pick(h.NameAr, h.NameEn))).ToList();
    }

    public async Task<List<HolidayDto>> ListAsync(Guid? clinicId, int? year, CancellationToken ct)
    {
        var id = ResolveClinic(clinicId);
        var y = year ?? DateTime.UtcNow.Year;
        var rows = await db.ClinicHolidays.AsNoTracking()
            .Where(h => h.ClinicId == id && h.Date >= new DateOnly(y, 1, 1) && h.Date <= new DateOnly(y, 12, 31))
            .OrderBy(h => h.Date).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<HolidayDto> CreateAsync(CreateHolidayDto request, CancellationToken ct)
    {
        var errors = new ValidationErrors()
            .Require(request.Date is not null, "date", "date is required.")
            .RequireText(request.NameAr, "nameAr", 200)
            .RequireText(request.NameEn, "nameEn", 200);
        errors.ThrowIfAny();
        var clinicId = ResolveClinic(request.ClinicId);

        var holiday = new ClinicHoliday
        {
            ClinicId = clinicId,
            Date = request.Date!.Value,
            NameAr = request.NameAr!.Trim(),
            NameEn = request.NameEn!.Trim(),
            Source = HolidaySource.Manual,
        };
        db.ClinicHolidays.Add(holiday);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Validation("date", "This date is already a holiday.");
        }

        return ToDto(holiday);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var deleted = await db.ClinicHolidays.Where(h => h.Id == id && h.ClinicId == clinicId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw ApiException.NotFound();
        }
    }

    public async Task<ImportHolidaysResultDto> ImportEgyptAsync(ImportHolidaysDto request, CancellationToken ct)
    {
        var errors = new ValidationErrors().Require(request.Year is >= 2000 and <= 2100, "year", "year must be between 2000 and 2100.");
        errors.ThrowIfAny();
        var clinicId = ResolveClinic(request.ClinicId);
        var year = request.Year!.Value;

        var existing = (await db.ClinicHolidays.Where(h => h.ClinicId == clinicId && h.Date >= new DateOnly(year, 1, 1) && h.Date <= new DateOnly(year, 12, 31)).Select(h => h.Date).ToListAsync(ct)).ToHashSet();
        var created = 0;
        var skipped = 0;
        foreach (var template in EgyptHolidays.ForYear(year))
        {
            if (!existing.Add(template.Date))
            {
                skipped++;
                continue;
            }

            db.ClinicHolidays.Add(new ClinicHoliday
            {
                ClinicId = clinicId,
                Date = template.Date,
                NameAr = template.NameAr,
                NameEn = template.NameEn,
                Source = HolidaySource.EgyptPublic,
            });
            created++;
        }

        await db.SaveChangesAsync(ct);
        return new ImportHolidaysResultDto(created, skipped);
    }

    // Confirmed appointments (in the caller's scope) that already sit on the holiday: they are left untouched.
    public async Task<List<DashboardAppointmentDto>> ConflictsAsync(Guid id, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var holiday = await db.ClinicHolidays.AsNoTracking().FirstOrDefaultAsync(h => h.Id == id && h.ClinicId == clinicId, ct)
            ?? throw ApiException.NotFound();
        var clinic = await scope.RequireClinicAsync(ct);
        var (start, end) = ClinicTime.DayRangeUtc(holiday.Date, holiday.Date, ClinicTime.FindZone(clinic.Timezone));
        var rows = await AppointmentMapper.WithDetails(scope.Appointments().AsNoTracking())
            .Where(a => a.Status == AppointmentStatus.Confirmed && a.Slot!.StartsAt >= start && a.Slot.StartsAt < end)
            .OrderBy(a => a.Slot!.StartsAt).ThenBy(a => a.Id)
            .ToListAsync(ct);
        return rows.Select(mapper.ToDashboardDto).ToList();
    }

    private Guid ResolveClinic(Guid? requested)
    {
        var clinicId = scope.ClinicId;
        if (requested is { } id && id != clinicId)
        {
            throw ApiException.NotFound();
        }

        return clinicId;
    }

    private static void RequireRange(DateOnly from, DateOnly to)
    {
        if (to < from || to.DayNumber - from.DayNumber + 1 > MaxRangeDays)
        {
            throw ApiException.Validation("to", $"The date range must be between 1 and {MaxRangeDays} days.");
        }
    }

    private static HolidayDto ToDto(ClinicHoliday h) => new(h.Id, h.ClinicId, h.Date, h.NameAr, h.NameEn, h.Source);
}
