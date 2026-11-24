namespace Moonlight.Api.Domain;

// Audit trail for dashboard status corrections (Completed <-> NoShow). One row per correction:
// who changed it, from what to what, why and when.
public class AppointmentStatusCorrection
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid AppointmentId { get; set; }

    public Appointment? Appointment { get; set; }

    public Guid ChangedByUserId { get; set; }

    public User? ChangedBy { get; set; }

    public AppointmentStatus FromStatus { get; set; }

    public AppointmentStatus ToStatus { get; set; }

    public string? Reason { get; set; }

    public DateTime CreatedAt { get; set; }
}
