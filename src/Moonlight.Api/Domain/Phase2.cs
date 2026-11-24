namespace Moonlight.Api.Domain;

// Phase 2 entities (docs/FEATURES-PHASE2.md). Enums are stored as strings like the rest of the schema.

public enum HolidaySource
{
    Manual,
    EgyptPublic,
}

public class ClinicHoliday
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClinicId { get; set; }

    public DateOnly Date { get; set; }

    public required string NameAr { get; set; }

    public required string NameEn { get; set; }

    public HolidaySource Source { get; set; }
}

/// <summary>One weekly window of a working-hours template (stored inside the template's jsonb column).</summary>
public class WorkingHoursRule
{
    public DayOfWeek DayOfWeek { get; set; }

    public string Start { get; set; } = "";

    public string End { get; set; } = "";
}

public class WorkingHoursTemplate
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClinicId { get; set; }

    public required string Name { get; set; }

    public int SlotMinutes { get; set; }

    public List<WorkingHoursRule> Rules { get; set; } = [];
}

public enum WaitingListStatus
{
    Waiting,
    Notified,
    Booked,
    Cancelled,
    Expired,
}

public class WaitingListEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PatientUserId { get; set; }

    public User? Patient { get; set; }

    public Guid? FamilyMemberId { get; set; }

    public Guid DoctorId { get; set; }

    public Doctor? Doctor { get; set; }

    public DateOnly Date { get; set; }

    public Guid? ServiceId { get; set; }

    public WaitingListStatus Status { get; set; } = WaitingListStatus.Waiting;

    public DateTime? NotifiedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum FamilyRelation
{
    Child,
    Spouse,
    Parent,
    Sibling,
    Other,
}

public enum Gender
{
    Male,
    Female,
}

public class FamilyMember
{
    public const int MaxPerOwner = 10;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid OwnerUserId { get; set; }

    public required string FullName { get; set; }

    public FamilyRelation Relation { get; set; }

    public DateOnly? DateOfBirth { get; set; }

    public Gender? Gender { get; set; }

    public DateTime CreatedAt { get; set; }

    // Soft delete: clinic records and appointments keep pointing at the member; hidden from the patient's lists and booking.
    public DateTime? DeletedAt { get; set; }
}

public enum MessageChannel
{
    Push,
    Sms,
    WhatsApp,
}

public enum MessageStatus
{
    Sent,
    Failed,
}

public class OutboundMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public MessageChannel Channel { get; set; }

    public required string To { get; set; }

    public Guid? UserId { get; set; }

    // Clinic the message is about (for staff scoping of the outbox); null for platform-level messages.
    public Guid? ClinicId { get; set; }

    public required string Kind { get; set; }

    public required string Body { get; set; }

    public MessageStatus Status { get; set; }

    public string? ProviderRef { get; set; }

    public string? Error { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum PaymentStatus
{
    Pending,
    Paid,
    Failed,
    Refunded,
    Cancelled,

    // A refund is in flight (claimed before the provider is called); counts as paid for the appointment.
    Refunding,
}

public enum PaymentMethod
{
    Online,
    Cash,
}

/// <summary>What a payment covers: the full visit price, or a booking deposit (the rest is settled at the clinic).</summary>
public enum PaymentKind
{
    Full,
    Deposit,
}

/// <summary>What the appointment DTOs report: Unpaid when there is no live payment.</summary>
public enum AppointmentPaymentStatus
{
    Unpaid,
    Pending,
    Paid,
    Refunded,
}

public class Payment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AppointmentId { get; set; }

    public Appointment? Appointment { get; set; }

    public decimal AmountEgp { get; set; }

    public PaymentStatus Status { get; set; } = PaymentStatus.Pending;

    public PaymentMethod Method { get; set; }

    // Rows that predate deposits are Full.
    public PaymentKind Kind { get; set; } = PaymentKind.Full;

    public required string Provider { get; set; }

    public string? ProviderRef { get; set; }

    // Provider-side transaction id (needed to refund); set by the webhook.
    public string? ProviderTransactionId { get; set; }

    public string? ReturnUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime? PaidAt { get; set; }

    public DateTime? RefundedAt { get; set; }
}

public enum InsuranceStatus
{
    Pending,
    Approved,
    Rejected,
}

public class InsuranceProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string NameAr { get; set; }

    public required string NameEn { get; set; }

    public bool IsActive { get; set; } = true;
}

public class DoctorInsuranceProvider
{
    public Guid DoctorId { get; set; }

    public Guid InsuranceProviderId { get; set; }

    public InsuranceProvider? InsuranceProvider { get; set; }
}

public class PatientMedicalInfo
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PatientUserId { get; set; }

    public Guid? FamilyMemberId { get; set; }

    public string? BloodType { get; set; }

    public string? Allergies { get; set; }

    public string? ChronicConditions { get; set; }

    public string? Medications { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public enum RecordKind
{
    Note,
    Diagnosis,
    Prescription,
    Attachment,
}

public class PatientRecordEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid PatientUserId { get; set; }

    public Guid? FamilyMemberId { get; set; }

    public Guid? AppointmentId { get; set; }

    public Guid? DoctorId { get; set; }

    // The clinic of the author: entries are only visible to the dashboard of that clinic.
    public Guid ClinicId { get; set; }

    public Guid AuthorUserId { get; set; }

    public RecordKind Kind { get; set; }

    public required string Title { get; set; }

    public string? Body { get; set; }

    // Key of the private file (see IRecordFileStorage); served only through the authorized attachment endpoints.
    public string? AttachmentFile { get; set; }

    public string? AttachmentContentType { get; set; }

    public bool VisibleToPatient { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public enum ReviewStatus
{
    Published,
    Hidden,
}

public class Review
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AppointmentId { get; set; }

    public Guid DoctorId { get; set; }

    public Doctor? Doctor { get; set; }

    public Guid PatientUserId { get; set; }

    public User? Patient { get; set; }

    public int Rating { get; set; }

    public string? Comment { get; set; }

    public ReviewStatus Status { get; set; } = ReviewStatus.Published;

    public string? Reply { get; set; }

    public DateTime? RepliedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum CouponDiscountType
{
    Percent,
    Fixed,
}

public class Coupon
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? ClinicId { get; set; }

    public required string Code { get; set; }

    public required string TitleAr { get; set; }

    public required string TitleEn { get; set; }

    public string? DescriptionAr { get; set; }

    public string? DescriptionEn { get; set; }

    public CouponDiscountType DiscountType { get; set; }

    public decimal Value { get; set; }

    public decimal? MaxDiscountEgp { get; set; }

    // Clinic-local dates, both inclusive.
    public DateOnly ValidFrom { get; set; }

    public DateOnly ValidTo { get; set; }

    public int? MaxRedemptions { get; set; }

    public int PerPatientLimit { get; set; } = 1;

    public Guid? DoctorId { get; set; }

    public Guid? ServiceId { get; set; }

    public bool IsPublic { get; set; }

    public bool IsActive { get; set; } = true;

    public int RedemptionsCount { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class CouponRedemption
{
    public Guid CouponId { get; set; }

    public Guid AppointmentId { get; set; }

    public Guid PatientUserId { get; set; }

    public decimal DiscountEgp { get; set; }

    public DateTime CreatedAt { get; set; }
}

public enum RecallChannel
{
    Auto,
    Sms,
    WhatsApp,
    Push,
}

public enum RecallMode
{
    OneOff,
    Recurring,
}

public enum RecallSendStatus
{
    Sent,
    Failed,
    Skipped,
}

public class RecallCampaign
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ClinicId { get; set; }

    public required string Name { get; set; }

    public required string MessageAr { get; set; }

    public required string MessageEn { get; set; }

    public RecallChannel Channel { get; set; }

    public int MonthsSinceLastVisit { get; set; }

    public Guid? DoctorId { get; set; }

    public Guid? ServiceId { get; set; }

    public RecallMode Mode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime? LastRunAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class RecallSend
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid CampaignId { get; set; }

    public Guid PatientUserId { get; set; }

    public RecallChannel Channel { get; set; }

    public RecallSendStatus Status { get; set; }

    public DateTime SentAt { get; set; }
}

public enum AppointmentMode
{
    InPerson,
    Video,
}
