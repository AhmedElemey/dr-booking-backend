namespace Moonlight.Api.Domain;

public class Slot
{
    public const int MinLengthMinutes = 15;
    public const int MaxLengthMinutes = 240;
    public const int AlignmentMinutes = 15;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DoctorId { get; set; }

    public Doctor? Doctor { get; set; }

    public Guid ClinicId { get; set; }

    public DateTime StartsAt { get; set; }

    public DateTime EndsAt { get; set; }

    public SlotStatus Status { get; set; } = SlotStatus.Open;

    public DateTime CreatedAt { get; set; }

    // True when both ends sit on a 15-minute boundary and the length is 15-240 minutes.
    public static bool IsValidRange(DateTime startsAt, DateTime endsAt)
    {
        var length = endsAt - startsAt;
        return IsAligned(startsAt)
            && IsAligned(endsAt)
            && length >= TimeSpan.FromMinutes(MinLengthMinutes)
            && length <= TimeSpan.FromMinutes(MaxLengthMinutes);
    }

    private static bool IsAligned(DateTime value) =>
        value.Second == 0 && value.Millisecond == 0 && value.Ticks % TimeSpan.TicksPerMinute == 0
        && value.Minute % AlignmentMinutes == 0;
}
