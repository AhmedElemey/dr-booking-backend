namespace Moonlight.Api.Domain;

// Conversions between UTC instants and clinic wall-clock time. Offsets always come from tzdata, never hardcoded.
public static class ClinicTime
{
    public static TimeZoneInfo FindZone(string ianaId) => TimeZoneInfo.FindSystemTimeZoneById(ianaId);

    public static bool IsValidZone(string ianaId)
    {
        try
        {
            FindZone(ianaId);
            return true;
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            return false;
        }
    }

    // A wall-clock time inside a DST gap does not exist and yields false; an ambiguous one resolves to the first occurrence.
    public static bool TryLocalToUtc(DateTime local, TimeZoneInfo zone, out DateTime utc)
    {
        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            utc = default;
            return false;
        }

        var offset = zone.IsAmbiguousTime(local)
            ? zone.GetAmbiguousTimeOffsets(local).Max()
            : zone.GetUtcOffset(local);
        utc = DateTime.SpecifyKind(local - offset, DateTimeKind.Utc);
        return true;
    }

    // First instant of a clinic-local date (when midnight falls in a DST gap, the first valid instant of that day).
    public static DateTime StartOfDayUtc(DateOnly date, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(TimeOnly.MinValue);
        while (!TryLocalToUtc(local, zone, out _))
        {
            local = local.AddMinutes(15);
        }

        TryLocalToUtc(local, zone, out var utc);
        return utc;
    }

    // Half-open UTC range [start, end) covering clinic-local dates from..to inclusive.
    public static (DateTime Start, DateTime End) DayRangeUtc(DateOnly from, DateOnly to, TimeZoneInfo zone) =>
        (StartOfDayUtc(from, zone), StartOfDayUtc(to.AddDays(1), zone));

    public static DateTime ToLocal(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone);

    public static DateOnly LocalDate(DateTime utc, TimeZoneInfo zone) => DateOnly.FromDateTime(ToLocal(utc, zone));
}
