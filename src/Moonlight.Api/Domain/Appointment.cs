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

    public DateTime CreatedAt { get; set; }

    public DateTime UpdatedAt { get; set; }

    // Maps to the Postgres xmin system column: optimistic concurrency token.
    public uint RowVersion { get; set; }
}
