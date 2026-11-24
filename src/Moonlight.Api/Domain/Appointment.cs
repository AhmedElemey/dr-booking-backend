namespace Moonlight.Api.Domain;

public class Appointment
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SlotId { get; set; }

    public Slot? Slot { get; set; }

    public Guid PatientUserId { get; set; }

    public User? Patient { get; set; }

    public Guid? ServiceId { get; set; }

    public Service? Service { get; set; }

    public AppointmentStatus Status { get; set; } = AppointmentStatus.Confirmed;

    public CancelledBy? CancelledBy { get; set; }

    public string? CancelReason { get; set; }

    public string? PatientNote { get; set; }

    public Guid CreatedByUserId { get; set; }

    public DateTime? ReminderSentAt { get; set; }

    // ---- Phase 2 (feature-gated; defaults keep the MVP behaviour) ----

    public Guid? FamilyMemberId { get; set; }

    public FamilyMember? FamilyMember { get; set; }

    public AppointmentMode Mode { get; set; } = AppointmentMode.InPerson;

    // Price snapshot taken at booking (service price, else doctor fee); null for rows that predate it.
    public decimal? PriceEgp { get; set; }

    public decimal DiscountEgp { get; set; }

    public Guid? CouponId { get; set; }

    public Guid? InsuranceProviderId { get; set; }

    public InsuranceProvider? InsuranceProvider { get; set; }

    public string? InsuranceMemberNumber { get; set; }

    public InsuranceStatus? InsuranceStatus { get; set; }

    public string? InsuranceNote { get; set; }

    public string? VideoRoomName { get; set; }

    public string? VideoPatientUrl { get; set; }

    public string? VideoHostUrl { get; set; }

    public DateTime? CheckedInAt { get; set; }

    public DateTime? StartedAt { get; set; }

    public DateTime? CompletedAt { get; set; }

    public DateTime? ReviewPromptSentAt { get; set; }

    public List<Payment> Payments { get; set; } = [];

    public Review? Review { get; set; }

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Maps to the Postgres xmin system column: optimistic concurrency token.
    public uint RowVersion { get; set; }
}
