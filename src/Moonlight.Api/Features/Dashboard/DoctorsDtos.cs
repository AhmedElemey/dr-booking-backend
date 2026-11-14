namespace Moonlight.Api.Features.Dashboard;

public sealed record ManagedDoctorDto(
    Guid Id,
    Guid ClinicId,
    Guid SpecialtyId,
    string Slug,
    string NameAr,
    string NameEn,
    string? BioAr,
    string? BioEn,
    string? PhotoUrl,
    int? YearsExperience,
    decimal? ConsultationFeeEgp,
    bool IsPublished);

public sealed record UpdateDoctorDto(
    string? NameAr,
    string? NameEn,
    string? BioAr,
    string? BioEn,
    int? YearsExperience,
    decimal? ConsultationFeeEgp,
    Guid? SpecialtyId,
    bool? IsPublished);

public sealed record PhotoUrlDto(string PhotoUrl);

public sealed record ManagedServiceDto(Guid Id, string NameAr, string NameEn, int DurationMinutes, decimal? PriceEgp, bool IsActive);

public sealed record ServiceInputDto(string? NameAr, string? NameEn, int? DurationMinutes, decimal? PriceEgp, bool? IsActive);

public sealed record ClinicDto(
    Guid Id,
    string NameAr,
    string NameEn,
    string AddressAr,
    string AddressEn,
    string City,
    double? Latitude,
    double? Longitude,
    string Phone,
    string Timezone,
    bool IsActive);

public sealed record UpdateClinicDto(
    string? NameAr,
    string? NameEn,
    string? AddressAr,
    string? AddressEn,
    string? City,
    string? Phone,
    double? Latitude,
    double? Longitude);

public sealed record ManagedSlotDto(
    Guid Id,
    Guid DoctorId,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTimeOffset StartsAtLocal,
    Domain.SlotStatus Status,
    Guid? AppointmentId,
    string? PatientName,
    string? PatientPhone);

public sealed record CreateSlotDto(DateTimeOffset? StartsAt, DateTimeOffset? EndsAt);

public sealed record BulkSlotsDto(
    DateOnly? FromDate,
    DateOnly? ToDate,
    List<DayOfWeek>? DaysOfWeek,
    TimeOnly? StartTime,
    TimeOnly? EndTime,
    int? SlotMinutes);

public sealed record SkippedSlotDto(DateTime? StartsAt, DateTime? EndsAt, string Local, string Reason);

public sealed record BulkSlotsResultDto(int Created, int Skipped, IReadOnlyList<SkippedSlotDto> SkippedSlots);

public sealed record DeleteSlotsResultDto(int Deleted, int SkippedReserved);
