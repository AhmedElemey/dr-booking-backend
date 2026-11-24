using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Holidays;
using Moonlight.Api.Features.Insurance;
using Moonlight.Api.Features.Reviews;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Catalog;

public sealed class CatalogService(
    MoonlightDbContext db,
    Language lang,
    TimeProvider clock,
    IOptions<BookingOptions> booking,
    FeatureService features,
    HolidayService holidays,
    InsuranceService insurance,
    ReviewService reviews)
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
        Guid? specialtyId, string? city, string? q, Guid? insuranceProviderId, PageRequest page, CancellationToken ct)
    {
        var query = Visible(db).AsNoTracking();
        if (insuranceProviderId is { } provider && features.IsEnabled(FeatureKeys.Insurance))
        {
            query = query.Where(d => db.DoctorInsuranceProviders.Any(i => i.DoctorId == d.Id && i.InsuranceProviderId == provider && i.InsuranceProvider!.IsActive));
        }

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
            async rows =>
            {
                var ids = rows.Select(d => d.Id).ToList();
                var accepted = features.IsEnabled(FeatureKeys.Insurance) ? await insurance.AcceptedByDoctorAsync(ids, ct) : [];
                var ratings = await reviews.RatingsAsync(ids, ct);
                return (IReadOnlyList<DoctorSummaryDto>)rows.Select(d => ToSummary(d, accepted.GetValueOrDefault(d.Id) ?? [], ratings.GetValueOrDefault(d.Id))).ToList();
            },
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
        var accepted = features.IsEnabled(FeatureKeys.Insurance) ? await insurance.AcceptedByDoctorAsync([doctor.Id], ct) : [];
        var ratings = await reviews.RatingsAsync([doctor.Id], ct);
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
            PricesVisible(clinic) ? doctor.ConsultationFeeEgp : null,
            services.Select(s => new PublicServiceDto(s.Id, lang.Pick(s.NameAr, s.NameEn), s.DurationMinutes, PricesVisible(clinic) ? s.PriceEgp : null)).ToList(),
            OffersVideoPublic(doctor),
            accepted.GetValueOrDefault(doctor.Id) ?? [],
            ratings.GetValueOrDefault(doctor.Id));
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

        // Holiday dates (holidayCalendar) are not bookable: their slots are left out and the dates are listed separately.
        var closed = await holidays.ClosedAsync(doctor.ClinicId, fromDate, toDate, ct);
        if (closed.Count > 0)
        {
            slots = slots.Where(s => !closed.ContainsKey(ClinicTime.LocalDate(s.StartsAt, zone))).ToList();
        }

        var days = slots
            .GroupBy(s => ClinicTime.LocalDate(s.StartsAt, zone))
            .OrderBy(g => g.Key)
            .Select(g => new SlotDayDto(
                g.Key,
                g.Select(s => new PublicSlotDto(s.Id, s.StartsAt, s.EndsAt, ClinicTime.ToLocal(s.StartsAt, zone).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture))).ToList()))
            .ToList();
        var closedDates = closed.Values.OrderBy(h => h.Date).Select(h => new ClosedDateDto(h.Date, lang.Pick(h.NameAr, h.NameEn))).ToList();
        return new DoctorSlotsDto(doctor.Id, doctor.Clinic.Timezone, days, closedDates);
    }

    private async Task<Doctor> FindVisibleAsync(string idOrSlug, CancellationToken ct)
    {
        var query = Visible(db).AsNoTracking().Include(d => d.Clinic).Include(d => d.Specialty);
        var doctor = Guid.TryParse(idOrSlug, out var id)
            ? await query.FirstOrDefaultAsync(d => d.Id == id, ct)
            : await query.FirstOrDefaultAsync(d => d.Slug == idOrSlug, ct);
        return doctor ?? throw ApiException.NotFound();
    }

    private bool OffersVideoPublic(Doctor d) => d.OffersVideo && features.IsEnabled(FeatureKeys.VideoConsultation, d.ClinicId);

    // Prices show only when the clinic keeps its own switch on and the platform admin's publicPrices feature is on for it.
    private bool PricesVisible(Clinic clinic) => clinic.ShowPrices && features.IsEnabled(FeatureKeys.PublicPrices, clinic.Id);

    private DoctorSummaryDto ToSummary(Doctor d, IReadOnlyList<InsuranceProviderRefDto> accepted, RatingDto? rating) => new(
        d.Id,
        d.Slug,
        lang.Pick(d.NameAr, d.NameEn),
        d.PhotoUrl,
        new SpecialtyDto(d.Specialty!.Id, lang.Pick(d.Specialty.NameAr, d.Specialty.NameEn)),
        new ClinicSummaryDto(d.Clinic!.Id, lang.Pick(d.Clinic.NameAr, d.Clinic.NameEn), d.Clinic.City),
        d.YearsExperience,
        PricesVisible(d.Clinic!) ? d.ConsultationFeeEgp : null,
        OffersVideoPublic(d),
        accepted,
        rating);
}
