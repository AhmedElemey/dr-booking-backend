namespace Moonlight.Api.Features.Catalog;

public sealed record SpecialtyDto(Guid Id, string Name);

public sealed record ClinicSummaryDto(Guid Id, string Name, string City);

public sealed record DoctorSummaryDto(
    Guid Id,
    string Slug,
    string Name,
    string? PhotoUrl,
    SpecialtyDto Specialty,
    ClinicSummaryDto Clinic,
    int? YearsExperience,
    decimal? ConsultationFeeEgp);

public sealed record ClinicDetailDto(
    Guid Id,
    string Name,
    string Address,
    string City,
    string Phone,
    double? Latitude,
    double? Longitude,
    string Timezone);

public sealed record PublicServiceDto(Guid Id, string Name, int DurationMinutes, decimal? PriceEgp);

public sealed record DoctorProfileDto(
    Guid Id,
    string Slug,
    string Name,
    string? Bio,
    string? PhotoUrl,
    SpecialtyDto Specialty,
    ClinicDetailDto Clinic,
    int? YearsExperience,
    decimal? ConsultationFeeEgp,
    IReadOnlyList<PublicServiceDto> Services);

public sealed record PublicSlotDto(Guid Id, DateTime StartsAt, DateTime EndsAt, string Local);

public sealed record SlotDayDto(DateOnly Date, IReadOnlyList<PublicSlotDto> Slots);

public sealed record DoctorSlotsDto(Guid DoctorId, string Timezone, IReadOnlyList<SlotDayDto> Days);
