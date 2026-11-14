using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Catalog;

public sealed class CatalogService(MoonlightDbContext db, Language lang, TimeProvider clock, IOptions<BookingOptions> booking)
{
    public const int MaxSlotRangeDays = 14;

    // Published doctors at active clinics (and with an active login) are the only ones the public can see.
    public static IQueryable<Doctor> Visible(MoonlightDbContext db) =>
        db.Doctors.Where(d => d.IsPublished && d.Clinic!.IsActive && d.User!.IsActive);

    public async Task<List<SpecialtyDto>> SpecialtiesAsync(CancellationToken ct)
    {
        var rows = await db.Specialties.AsNoTracking().ToListAsync(ct);
        return rows.Select(s => new SpecialtyDto(s.Id, lang.Pick(s.NameAr, s.NameEn))).OrderBy(s => s.Name, StringComparer.Ordinal).ThenBy(s => s.Id).ToList();
    }

    public Task<List<string>> CitiesAsync(CancellationToken ct) =>
        Visible(db).Select(d => d.Clinic!.City).Distinct().OrderBy(c => c).ToListAsync(ct);

    public async Task<PagedResult<DoctorSummaryDto>> SearchDoctorsAsync(
        Guid? specialtyId, string? city, string? q, PageRequest page, CancellationToken ct)
    {
        var query = Visible(db).AsNoTracking();
        if (specialtyId is { } specialty)
        {
            query = query.Where(d => d.SpecialtyId == specialty);
        }

        if (!string.IsNullOrWhiteSpace(city))
        {
            var cityName = city.Trim();
            query = query.Where(d => EF.Functions.ILike(d.Clinic!.City, cityName));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = "%" + q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(d => EF.Functions.ILike(d.NameEn, pattern) || EF.Functions.ILike(d.NameAr, pattern));
        }

        var ordered = lang.IsEnglish
            ? query.OrderBy(d => d.NameEn).ThenBy(d => d.Id)
            : query.OrderBy(d => d.NameAr).ThenBy(d => d.Id);

        return await ordered.Include(d => d.Specialty).Include(d => d.Clinic).ToPagedAsync(
            page,
            rows => Task.FromResult<IReadOnlyList<DoctorSummaryDto>>(rows.Select(ToSummary).ToList()),
            ct);
    }

    public async Task<DoctorProfileDto> GetDoctorAsync(string idOrSlug, CancellationToken ct)
    {
        var doctor = await FindVisibleAsync(idOrSlug, ct);
        var services = await db.Services.AsNoTracking()
            .Where(s => s.DoctorId == doctor.Id && s.IsActive)
            .OrderBy(s => s.DurationMinutes).ThenBy(s => s.Id)
            .ToListAsync(ct);
        var clinic = doctor.Clinic!;
        return new DoctorProfileDto(
            doctor.Id,
            doctor.Slug,
            lang.Pick(doctor.NameAr, doctor.NameEn),
            lang.PickOptional(doctor.BioAr, doctor.BioEn),
            doctor.PhotoUrl,
            new SpecialtyDto(doctor.Specialty!.Id, lang.Pick(doctor.Specialty.NameAr, doctor.Specialty.NameEn)),
            new ClinicDetailDto(
                clinic.Id,
                lang.Pick(clinic.NameAr, clinic.NameEn),
                lang.Pick(clinic.AddressAr, clinic.AddressEn),
                clinic.City,
                clinic.Phone,
                clinic.Latitude,
                clinic.Longitude,
                clinic.Timezone),
            doctor.YearsExperience,
            doctor.ConsultationFeeEgp,
            services.Select(s => new PublicServiceDto(s.Id, lang.Pick(s.NameAr, s.NameEn), s.DurationMinutes, s.PriceEgp)).ToList());
    }

    public async Task<DoctorSlotsDto> GetOpenSlotsAsync(string idOrSlug, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var doctor = await FindVisibleAsync(idOrSlug, ct);
        var zone = ClinicTime.FindZone(doctor.Clinic!.Timezone);
        var now = clock.GetUtcNow().UtcDateTime;

        var fromDate = from ?? ClinicTime.LocalDate(now, zone);
        var toDate = to ?? fromDate.AddDays(MaxSlotRangeDays - 1);
        if (toDate < fromDate || toDate.DayNumber - fromDate.DayNumber + 1 > MaxSlotRangeDays)
        {
            throw ApiException.Validation("to", $"The range must be between 1 and {MaxSlotRangeDays} days.");
        }

        var (start, end) = ClinicTime.DayRangeUtc(fromDate, toDate, zone);
        var earliest = now.AddMinutes(booking.Value.MinLeadMinutes);
        var slots = await db.Slots.AsNoTracking()
            .Where(s => s.DoctorId == doctor.Id && s.Status == SlotStatus.Open && s.StartsAt >= start && s.StartsAt < end && s.StartsAt >= earliest)
            .OrderBy(s => s.StartsAt)
            .ToListAsync(ct);

        var days = slots
            .GroupBy(s => ClinicTime.LocalDate(s.StartsAt, zone))
            .OrderBy(g => g.Key)
            .Select(g => new SlotDayDto(
                g.Key,
                g.Select(s => new PublicSlotDto(s.Id, s.StartsAt, s.EndsAt, ClinicTime.ToLocal(s.StartsAt, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))).ToList()))
            .ToList();
        return new DoctorSlotsDto(doctor.Id, doctor.Clinic.Timezone, days);
    }

    private async Task<Doctor> FindVisibleAsync(string idOrSlug, CancellationToken ct)
    {
        var query = Visible(db).AsNoTracking().Include(d => d.Clinic).Include(d => d.Specialty);
        var doctor = Guid.TryParse(idOrSlug, out var id)
            ? await query.FirstOrDefaultAsync(d => d.Id == id, ct)
            : await query.FirstOrDefaultAsync(d => d.Slug == idOrSlug, ct);
        return doctor ?? throw ApiException.NotFound();
    }

    private DoctorSummaryDto ToSummary(Doctor d) => new(
        d.Id,
        d.Slug,
        lang.Pick(d.NameAr, d.NameEn),
        d.PhotoUrl,
        new SpecialtyDto(d.Specialty!.Id, lang.Pick(d.Specialty.NameAr, d.Specialty.NameEn)),
        new ClinicSummaryDto(d.Clinic!.Id, lang.Pick(d.Clinic.NameAr, d.Clinic.NameEn), d.Clinic.City),
        d.YearsExperience,
        d.ConsultationFeeEgp);
}
