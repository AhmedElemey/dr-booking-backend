using Moonlight.Api.Domain;

namespace Moonlight.Tests;

public class SlotGeneratorTests
{
    private static readonly TimeZoneInfo Cairo = ClinicTime.FindZone("Africa/Cairo");
    private static readonly DateTime LongAgo = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly IReadOnlyCollection<TimeRange> NoSlots = [];

    private static readonly DayOfWeek[] EveryDay = Enum.GetValues<DayOfWeek>();

    private static BulkSlotSpec Spec(string from, string to, DayOfWeek[] days, string start, string end, int minutes) =>
        new(DateOnly.Parse(from), DateOnly.Parse(to), days, TimeOnly.Parse(start), TimeOnly.Parse(end), minutes);

    private static DateTime Utc(int y, int m, int d, int h, int min = 0) => new(y, m, d, h, min, 0, DateTimeKind.Utc);

    [Fact]
    public void Only_selected_days_of_week_are_expanded()
    {
        // 2026-11-01 is a Sunday.
        var result = SlotGenerator.Generate(
            Spec("2026-11-01", "2026-11-07", [DayOfWeek.Monday, DayOfWeek.Wednesday], "09:00", "10:00", 30), Cairo, LongAgo, NoSlots);

        Assert.Equal(
            [Utc(2026, 11, 2, 7), Utc(2026, 11, 2, 7, 30), Utc(2026, 11, 4, 7), Utc(2026, 11, 4, 7, 30)],
            result.Created.Select(r => r.StartsAt));
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Slots_step_from_start_time_and_the_last_slot_must_fit_inside_the_window()
    {
        var result = SlotGenerator.Generate(
            Spec("2026-11-02", "2026-11-02", EveryDay, "09:00", "10:20", 30), Cairo, LongAgo, NoSlots);

        // 09:00 and 09:30 fit; 10:00-10:30 would end after 10:20.
        Assert.Equal([Utc(2026, 11, 2, 7), Utc(2026, 11, 2, 7, 30)], result.Created.Select(r => r.StartsAt));
        Assert.All(result.Created, r => Assert.Equal(TimeSpan.FromMinutes(30), r.EndsAt - r.StartsAt));
    }

    [Fact]
    public void A_window_exactly_filled_by_slots_includes_the_last_one()
    {
        var result = SlotGenerator.Generate(Spec("2026-11-02", "2026-11-02", EveryDay, "09:00", "10:00", 30), Cairo, LongAgo, NoSlots);

        Assert.Equal(2, result.Created.Count);
        Assert.Equal(Utc(2026, 11, 2, 8), result.Created[^1].EndsAt);
    }

    [Fact]
    public void A_range_crossing_the_egypt_dst_change_keeps_local_wall_clock_times()
    {
        // Egypt moves to summer time on Friday 2026-04-24 at 00:00 (UTC+2 -> UTC+3).
        var result = SlotGenerator.Generate(Spec("2026-04-23", "2026-04-25", EveryDay, "09:00", "10:00", 60), Cairo, LongAgo, NoSlots);

        Assert.Equal(
            [Utc(2026, 4, 23, 7), Utc(2026, 4, 24, 6), Utc(2026, 4, 25, 6)],
            result.Created.Select(r => r.StartsAt));
    }

    [Fact]
    public void Local_times_inside_the_dst_gap_are_skipped_and_reported()
    {
        var result = SlotGenerator.Generate(Spec("2026-04-24", "2026-04-24", EveryDay, "00:00", "03:00", 60), Cairo, LongAgo, NoSlots);

        // 00:00 does not exist on that day; 01:00 and 02:00 do.
        var skipped = Assert.Single(result.Skipped);
        Assert.Equal(SkipReason.NonexistentLocalTime, skipped.Reason);
        Assert.Equal(new DateTime(2026, 4, 24, 0, 0, 0), skipped.LocalStart);
        Assert.Equal([Utc(2026, 4, 23, 22), Utc(2026, 4, 23, 23)], result.Created.Select(r => r.StartsAt));
    }

    [Fact]
    public void An_ambiguous_local_time_resolves_to_the_first_occurrence()
    {
        // Summer time ends 2026-10-29 at 24:00, so 23:00-23:59 happens twice.
        var result = SlotGenerator.Generate(Spec("2026-10-29", "2026-10-29", EveryDay, "22:00", "23:59", 30), Cairo, LongAgo, NoSlots);

        Assert.Equal(
            [Utc(2026, 10, 29, 19), Utc(2026, 10, 29, 19, 30), Utc(2026, 10, 29, 20)],
            result.Created.Select(r => r.StartsAt));
    }

    [Fact]
    public void Past_and_overlapping_slots_are_skipped_with_a_reason()
    {
        var now = Utc(2026, 11, 2, 6, 45);
        TimeRange[] existing = [new(Utc(2026, 11, 2, 7), Utc(2026, 11, 2, 7, 30))];

        // 08:00-10:00 Cairo = 06:00Z-08:00Z in four 30 minute slots.
        var result = SlotGenerator.Generate(Spec("2026-11-02", "2026-11-02", EveryDay, "08:00", "10:00", 30), Cairo, now, existing);

        Assert.Equal([Utc(2026, 11, 2, 7, 30)], result.Created.Select(r => r.StartsAt));
        Assert.Equal(
            [SkipReason.Past, SkipReason.Past, SkipReason.Overlap],
            result.Skipped.Select(s => s.Reason));
    }

    [Fact]
    public void A_slot_that_only_touches_an_existing_one_is_not_an_overlap()
    {
        TimeRange[] existing = [new(Utc(2026, 11, 2, 6, 30), Utc(2026, 11, 2, 7))];

        var result = SlotGenerator.Generate(Spec("2026-11-02", "2026-11-02", EveryDay, "09:00", "09:30", 30), Cairo, LongAgo, existing);

        Assert.Single(result.Created);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Exactly_two_thousand_slots_are_allowed_and_one_more_is_rejected()
    {
        // 00:00-20:00 in 15 minute steps is 80 slots a day.
        var ok = SlotGenerator.Generate(Spec("2026-11-01", "2026-11-25", EveryDay, "00:00", "20:00", 15), Cairo, LongAgo, NoSlots);
        Assert.Equal(SlotGenerator.MaxSlots, ok.Created.Count);

        var ex = Assert.Throws<SlotGenerationException>(() =>
            SlotGenerator.Generate(Spec("2026-11-01", "2026-11-26", EveryDay, "00:00", "20:00", 15), Cairo, LongAgo, NoSlots));
        Assert.Equal("toDate", ex.Field);
    }

    [Fact]
    public void The_date_range_is_limited_to_92_days()
    {
        var ok = SlotGenerator.Generate(Spec("2026-11-01", "2027-01-31", EveryDay, "09:00", "09:30", 30), Cairo, LongAgo, NoSlots);
        Assert.Equal(92, ok.Created.Count);

        Assert.Throws<SlotGenerationException>(() =>
            SlotGenerator.Generate(Spec("2026-11-01", "2027-02-01", EveryDay, "09:00", "09:30", 30), Cairo, LongAgo, NoSlots));
    }

    [Theory]
    [InlineData("2026-11-05", "2026-11-01", "09:00", "10:00", 30)]
    [InlineData("2026-11-01", "2026-11-02", "09:00", "10:00", 20)]
    [InlineData("2026-11-01", "2026-11-02", "09:00", "10:00", 10)]
    [InlineData("2026-11-01", "2026-11-02", "09:00", "10:00", 255)]
    [InlineData("2026-11-01", "2026-11-02", "10:00", "10:00", 30)]
    [InlineData("2026-11-01", "2026-11-02", "09:10", "10:00", 30)]
    public void Invalid_specs_are_rejected(string from, string to, string start, string end, int minutes)
    {
        Assert.Throws<SlotGenerationException>(() => SlotGenerator.Generate(Spec(from, to, EveryDay, start, end, minutes), Cairo, LongAgo, NoSlots));
    }

    [Fact]
    public void An_empty_day_selection_is_rejected()
    {
        Assert.Throws<SlotGenerationException>(() => SlotGenerator.Generate(Spec("2026-11-01", "2026-11-02", [], "09:00", "10:00", 30), Cairo, LongAgo, NoSlots));
    }
}

public class AppointmentRulesTests
{
    private static readonly DateTime Start = new(2026, 11, 2, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Window = TimeSpan.FromHours(2);

    [Theory]
    [InlineData(-180, true)]
    [InlineData(-120, true)]
    [InlineData(-119, false)]
    [InlineData(10, false)]
    public void Patient_window_is_inclusive_at_exactly_the_cut_off(int minutesFromStart, bool allowed) =>
        Assert.Equal(allowed, AppointmentRules.IsInsidePatientWindow(Start.AddMinutes(minutesFromStart), Start, Window));

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    [InlineData(5, false)]
    public void Clinic_can_cancel_only_before_start(int minutesFromStart, bool allowed) =>
        Assert.Equal(allowed, AppointmentRules.CanClinicCancel(Start.AddMinutes(minutesFromStart), Start));

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    public void Complete_and_no_show_need_the_visit_to_have_started(int minutesFromStart, bool allowed) =>
        Assert.Equal(allowed, AppointmentRules.CanCloseOut(Start.AddMinutes(minutesFromStart), Start));

    [Theory]
    [InlineData(AppointmentStatus.Confirmed, true)]
    [InlineData(AppointmentStatus.Cancelled, false)]
    [InlineData(AppointmentStatus.Completed, false)]
    [InlineData(AppointmentStatus.NoShow, false)]
    public void Only_confirmed_appointments_can_change(AppointmentStatus status, bool allowed) =>
        Assert.Equal(allowed, AppointmentRules.CanLeave(status));
}

public class PhoneNumberTests
{
    [Theory]
    [InlineData("01012345678", "+201012345678")]
    [InlineData("+201012345678", "+201012345678")]
    [InlineData("201112345678", "+201112345678")]
    [InlineData("00201212345678", "+201212345678")]
    [InlineData("010 1234 5678", "+201012345678")]
    [InlineData("1512345678", "+201512345678")]
    public void Egyptian_mobiles_normalize_to_e164(string input, string expected)
    {
        Assert.True(Moonlight.Api.Common.PhoneNumber.TryNormalize(input, out var e164));
        Assert.Equal(expected, e164);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0101234567")]
    [InlineData("01312345678")]
    [InlineData("+14155552671")]
    [InlineData("abc")]
    public void Anything_else_is_rejected(string input) =>
        Assert.False(Moonlight.Api.Common.PhoneNumber.TryNormalize(input, out _));
}
