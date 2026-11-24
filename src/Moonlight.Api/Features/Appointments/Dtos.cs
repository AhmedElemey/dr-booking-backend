using Moonlight.Api.Domain;
using Moonlight.Api.Features.Family;
using Moonlight.Api.Features.Insurance;
using Moonlight.Api.Features.Reviews;

namespace Moonlight.Api.Features.Appointments;

/// <summary>
/// Book a slot. The optional fields belong to Phase 2 features and are ignored while the feature is off:
/// <c>familyMemberId</c> (familyAccounts), <c>insurance</c> (insurance), <c>couponCode</c> (promotions), <c>mode</c> (videoConsultation).
/// </summary>
public sealed record BookAppointmentDto(
    Guid? SlotId,
    Guid? ServiceId,
    string? PatientNote,
    Guid? FamilyMemberId = null,
    InsuranceBookingDto? Insurance = null,
    string? CouponCode = null,
    AppointmentMode? Mode = null);

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
    string? CancelReason,
    AppointmentMode Mode,
    decimal PriceEgp,
    decimal DiscountEgp,
    decimal TotalEgp,
    AppointmentPaymentStatus PaymentStatus,
    decimal DepositRequiredEgp,
    decimal DepositPaidEgp,
    FamilyMemberRefDto? FamilyMember,
    InsuranceRefDto? Insurance,
    bool CanReview,
    ReviewRefDto? Review);

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
    string? CancelReason,
    AppointmentMode Mode,
    decimal PriceEgp,
    decimal DiscountEgp,
    decimal TotalEgp,
    AppointmentPaymentStatus PaymentStatus,
    decimal DepositRequiredEgp,
    decimal DepositPaidEgp,
    FamilyMemberRefDto? FamilyMember,
    InsuranceRefDto? Insurance,
    ReviewRefDto? Review);

public sealed record DashboardBookAppointmentDto(
    Guid? SlotId,
    Guid? ServiceId,
    string? PatientPhone,
    string? PatientName,
    string? PatientNote,
    AppointmentMode? Mode = null,
    InsuranceBookingDto? Insurance = null);
