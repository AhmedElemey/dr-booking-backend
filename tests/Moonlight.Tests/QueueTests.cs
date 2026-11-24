using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

// Live queue: check-in on the day, one patient with the doctor at a time, the queue ordered for staff,
// and the patient sees their position. Behind the liveQueue feature.
public class QueueTests(PostgresFixture db) : Phase2TestBase(db)
{
    private const string QueueUrl = "/api/v1/dashboard/appointments/queue";

    private async Task<(Guid Id, Session Patient)> BookedTodayAsync(TimeSpan fromNow)
    {
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await SlotAsync(fromNow));
        return (id, patient);
    }

    [Fact]
    public async Task Check_in_puts_the_visit_in_the_queue_and_the_patient_sees_the_position()
    {
        Enable(FeatureKeys.LiveQueue);
        var (id, patient) = await BookedTodayAsync(TimeSpan.FromHours(2));

        var checkedIn = await Staff.PostAsync($"{Dash(id)}/check-in");
        Assert.Equal(200, checkedIn.StatusCode);
        Assert.Equal("CheckedIn", checkedIn["status"].GetString());
        Assert.NotEqual(default, checkedIn["checkedInAt"].GetDateTime());

        var queue = await Staff.GetAsync(QueueUrl);
        Assert.Equal(200, queue.StatusCode);
        var rows = queue.Body.EnumerateArray().ToList();
        Assert.Single(rows);
        Assert.Equal(1, rows[0].GetProperty("position").GetInt32());
        Assert.Equal(id, rows[0].GetProperty("appointment").GetProperty("id").GetGuid());

        var view = await patient.GetAsync(Patient(id));
        Assert.Equal("CheckedIn", view["status"].GetString());
        Assert.Equal(1, view["queuePosition"].GetInt32());
    }

    [Fact]
    public async Task Check_in_before_the_visit_day_is_rejected()
    {
        Enable(FeatureKeys.LiveQueue);
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await LocalSlotAsync(2, 10));

        var response = await Staff.PostAsync($"{Dash(id)}/check-in");
        Assert.Equal(409, response.StatusCode);
    }

    [Fact]
    public async Task Start_requires_check_in_and_the_doctor_sees_one_patient_at_a_time()
    {
        Enable(FeatureKeys.LiveQueue);
        var (first, _) = await BookedTodayAsync(TimeSpan.FromHours(2));
        var (second, secondPatient) = await BookedTodayAsync(TimeSpan.FromHours(3));

        // Start before check-in is rejected.
        Assert.Equal(409, (await Staff.PostAsync($"{Dash(first)}/start")).StatusCode);

        Assert.Equal(200, (await Staff.PostAsync($"{Dash(first)}/check-in")).StatusCode);
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(second)}/check-in")).StatusCode);

        var started = await Staff.PostAsync($"{Dash(first)}/start");
        Assert.Equal(200, started.StatusCode);
        Assert.Equal("InProgress", started["status"].GetString());
        Assert.NotEqual(default, started["startedAt"].GetDateTime());

        // The doctor is busy: the second visit cannot start, and the patient sees position 2.
        Assert.Equal(409, (await Staff.PostAsync($"{Dash(second)}/start")).StatusCode);
        Assert.Equal(2, (await secondPatient.GetAsync(Patient(second)))["queuePosition"].GetInt32());

        // Completing the first visit frees the doctor.
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(first)}/complete")).StatusCode);
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(second)}/start")).StatusCode);

        // The queue orders the in-progress visit first.
        var rows = (await Staff.GetAsync(QueueUrl)).Body.EnumerateArray().ToList();
        Assert.Equal(second, rows[0].GetProperty("appointment").GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task The_queue_and_its_transitions_answer_403_while_the_feature_is_off()
    {
        var (id, _) = await BookedTodayAsync(TimeSpan.FromHours(2));

        Assert.Equal(403, (await Staff.GetAsync(QueueUrl)).StatusCode);
        Assert.Equal(403, (await Staff.PostAsync($"{Dash(id)}/check-in")).StatusCode);
        Assert.Equal(403, (await Staff.PostAsync($"{Dash(id)}/start")).StatusCode);
    }
}
