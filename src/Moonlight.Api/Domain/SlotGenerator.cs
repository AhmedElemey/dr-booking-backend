namespace Moonlight.Api.Domain;

public sealed record BulkSlotSpec(
    DateOnly FromDate,
    DateOnly ToDate,
    IReadOnlyCollection<DayOfWeek> DaysOfWeek,
    TimeOnly StartTime,
    TimeOnly EndTime,
    int SlotMinutes);

public sealed record TimeRange(DateTime StartsAt, DateTime EndsAt);

public enum SkipReason
{
    Past,
    Overlap,
    NonexistentLocalTime,
    Holiday,
}

public sealed record SkippedSlot(DateTime LocalStart, DateTime? StartsAt, DateTime? EndsAt, SkipReason Reason);

public sealed record SlotGenerationResult(IReadOnlyList<TimeRange> Created, IReadOnlyList<SkippedSlot> Skipped);

public sealed class SlotGenerationException(string field, string message) : Exception(message)
{
    public string Field { get; } = field;
}

// Pure expansion of the bulk "repeat" helper into concrete slot rows (spec section 3). No database, no clock.
public static class SlotGenerator
{
    public const int MaxDays = 92;
    public const int MaxSlots = 2000;

    public static SlotGenerationResult Generate(
        BulkSlotSpec spec,
        TimeZoneInfo zone,
        DateTime nowUtc,
        IReadOnlyCollection<TimeRange> existing,
        IReadOnlySet<DateOnly>? closedDates = null)
    {
        Validate(spec);

        var days = new List<DateOnly>();
        for (var date = spec.FromDate; date <= spec.ToDate; date = date.AddDays(1))
        {
            if (spec.DaysOfWeek.Contains(date.DayOfWeek))
            {
                days.Add(date);
            }
        }

        var stepsPerDay = ((spec.EndTime.ToTimeSpan() - spec.StartTime.ToTimeSpan()).TotalMinutes / spec.SlotMinutes);
        var candidates = days.Count * (int)Math.Floor(stepsPerDay);
        if (candidates > MaxSlots)
        {
            throw new SlotGenerationException("toDate", $"A single call may create at most {MaxSlots} slots (this request expands to {candidates}).");
        }

        var created = new List<TimeRange>();
        var skipped = new List<SkippedSlot>();
        var slotLength = TimeSpan.FromMinutes(spec.SlotMinutes);

        foreach (var date in days)
        {
            var windowEnd = date.ToDateTime(spec.EndTime);
            for (var localStart = date.ToDateTime(spec.StartTime); localStart + slotLength <= windowEnd; localStart += slotLength)
            {
                if (!ClinicTime.TryLocalToUtc(localStart, zone, out var startsAt))
                {
                    skipped.Add(new SkippedSlot(localStart, null, null, SkipReason.NonexistentLocalTime));
                    continue;
                }

                // Length is elapsed time, so a slot is always exactly SlotMinutes long even across a DST change.
                var endsAt = startsAt + slotLength;
                if (startsAt < nowUtc)
                {
                    skipped.Add(new SkippedSlot(localStart, startsAt, endsAt, SkipReason.Past));
                }
                else if (closedDates is not null && closedDates.Contains(date))
                {
                    skipped.Add(new SkippedSlot(localStart, startsAt, endsAt, SkipReason.Holiday));
                }
                else if (Overlaps(startsAt, endsAt, existing) || Overlaps(startsAt, endsAt, created))
                {
                    skipped.Add(new SkippedSlot(localStart, startsAt, endsAt, SkipReason.Overlap));
                }
                else
                {
                    created.Add(new TimeRange(startsAt, endsAt));
                }
            }
        }

        return new SlotGenerationResult(created, skipped);
    }

    private static bool Overlaps(DateTime startsAt, DateTime endsAt, IEnumerable<TimeRange> ranges) =>
        ranges.Any(r => r.StartsAt < endsAt && startsAt < r.EndsAt);

    private static void Validate(BulkSlotSpec spec)
    {
        if (spec.ToDate < spec.FromDate)
        {
            throw new SlotGenerationException("toDate", "toDate must not be before fromDate.");
        }

        if (spec.ToDate.DayNumber - spec.FromDate.DayNumber + 1 > MaxDays)
        {
            throw new SlotGenerationException("toDate", $"The date range may span at most {MaxDays} days.");
        }

        if (spec.DaysOfWeek.Count == 0)
        {
            throw new SlotGenerationException("daysOfWeek", "Select at least one day of the week.");
        }

        if (spec.SlotMinutes is < Slot.MinLengthMinutes or > Slot.MaxLengthMinutes || spec.SlotMinutes % Slot.AlignmentMinutes != 0)
        {
            throw new SlotGenerationException("slotMinutes", $"slotMinutes must be a multiple of {Slot.AlignmentMinutes} between {Slot.MinLengthMinutes} and {Slot.MaxLengthMinutes}.");
        }

        if (spec.StartTime.Minute % Slot.AlignmentMinutes != 0 || spec.StartTime.Second != 0)
        {
            throw new SlotGenerationException("startTime", $"startTime must be aligned to {Slot.AlignmentMinutes} minutes.");
        }

        if (spec.EndTime <= spec.StartTime)
        {
            throw new SlotGenerationException("endTime", "endTime must be after startTime.");
        }
    }
}
