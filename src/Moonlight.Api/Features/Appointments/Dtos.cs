using Moonlight.Api.Domain;

namespace Moonlight.Api.Features.Appointments;

public sealed record BookAppointmentDto(Guid? SlotId, Guid? ServiceId, string? PatientNote);

public sealed record CancelAppointmentDto(string? Reason);

public sealed record RescheduleAppointmentDto(Guid? SlotId);

public sealed record DoctorRefDto(Guid Id, string? Slug, string Name, string? PhotoUrl);

public sealed record ClinicRefDto(Guid Id, string Name, string Address, string Phone, double? Latitude, double? Longitude);

public sealed record ServiceRefDto(Guid Id, string Name, int DurationMinutes, decimal? PriceEgp);

public sealed record PatientRefDto(Guid Id, string? FullName, string Phone);

public sealed record DashboardDoctorRefDto(Guid Id, string Name);

/// <summary>Patient-facing appointment. Doctor, clinic and times come from the slot.</summary>
public sealed record AppointmentDto(
    Guid Id,
    AppointmentStatus Status,
    Guid SlotId,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTimeOffset StartsAtLocal,
    string Timezone,
    DoctorRefDto Doctor,
    ClinicRefDto Clinic,
    ServiceRefDto? Service,
    string? PatientNote,
    CancelledBy? CancelledBy,
    string? CancelReason);

/// <summary>Dashboard appointment: the clinic block is replaced by the patient.</summary>
public sealed record DashboardAppointmentDto(
    Guid Id,
    AppointmentStatus Status,
    Guid SlotId,
    DateTime StartsAt,
    DateTime EndsAt,
    DateTimeOffset StartsAtLocal,
    string Timezone,
    DashboardDoctorRefDto Doctor,
    PatientRefDto Patient,
    ServiceRefDto? Service,
    string? PatientNote,
    CancelledBy? CancelledBy,
    string? CancelReason);

public sealed record DashboardBookAppointmentDto(
    Guid? SlotId,
    Guid? ServiceId,
    string? PatientPhone,
    string? PatientName,
    string? PatientNote);
