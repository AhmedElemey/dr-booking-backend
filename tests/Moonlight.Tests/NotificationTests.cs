using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Notifications;

namespace Moonlight.Tests;

public class NotificationTests(PostgresFixture db) : AppointmentTestBase(db)
{
    private async Task<(Guid Id, Slot Slot, Session Patient)> BookWithDeviceAsync(TimeSpan fromNow, string language = "ar")
    {
        var patient = await World.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { preferredLanguage = language });
        await RegisterDeviceAsync(patient);
        var slot = await SlotAsync(fromNow);
        return (await World.BookAsync(patient, slot), slot, patient);
    }

    [Fact]
    public async Task Booking_sends_a_localized_confirmation_push_to_every_device()
    {
        var patient = await World.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { preferredLanguage = "en" });
        await RegisterDeviceAsync(patient, "device-1");
        await RegisterDeviceAsync(patient, "device-2");

        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(1)));

        var (tokens, message) = Assert.Single(Factory.Push.Sent);
        Assert.Equal(["device-1", "device-2"], tokens.Order());
        Assert.Equal("Appointment confirmed", message.Title);
        Assert.Contains("Dr Test", message.Body);
        Assert.Equal("appointment.confirmed", message.Data["type"]);
        Assert.Equal(id.ToString(), message.Data["appointmentId"]);
    }

    [Fact]
    public async Task Arabic_is_the_default_notification_language()
    {
        await BookWithDeviceAsync(TimeSpan.FromDays(1));

        Assert.Equal("تم تأكيد حجزك", Assert.Single(Factory.Push.Sent).Message.Title);
    }

    [Fact]
    public async Task Staff_booking_and_reschedule_send_confirmations()
    {
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);
        var first = await SlotAsync(TimeSpan.FromDays(1));

        var booked = await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = first.Id, patientPhone = patient.User!.Phone, patientName = "P" });
        Assert.Equal(1, Factory.Push.Sent.Count);

        await Staff.PostAsync($"{Dash(booked.Id)}/reschedule", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id });
        Assert.Equal(2, Factory.Push.Sent.Count);
    }

    [Fact]
    public async Task Clinic_cancellation_notifies_the_patient_with_the_reason_but_patient_cancellation_does_not()
    {
        var (id, _, patient) = await BookWithDeviceAsync(TimeSpan.FromDays(1), "en");
        var (second, _, _) = await BookWithDeviceAsync(TimeSpan.FromDays(2), "en");
        var before = Factory.Push.Sent.Count;

        await patient.PostAsync($"{Patient(id)}/cancel", new { reason = "mine" });
        Assert.Equal(before, Factory.Push.Sent.Count);

        await Staff.PostAsync($"{Dash(second)}/cancel", new { reason = "doctor is ill" });
        var (_, message) = Factory.Push.Sent[^1];
        Assert.Equal("appointment.cancelled", message.Data["type"]);
        Assert.Contains("doctor is ill", message.Body);
        Assert.Equal(before + 1, Factory.Push.Sent.Count);
    }

    [Fact]
    public async Task A_failing_push_provider_never_fails_the_request()
    {
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);
        Factory.Push.ThrowOnSend = true;

        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(1))).Id });

        Assert.Equal(201, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_device_tokens_are_deleted_after_a_send()
    {
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient, "stale-token");
        await RegisterDeviceAsync(patient, "good-token");
        Factory.Push.InvalidTokens.Add("stale-token");

        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(1)));

        var remaining = await World.WithDbAsync(d => d.DeviceTokens.Where(t => t.UserId == patient.User!.Id).Select(t => t.FcmToken).ToListAsync());
        Assert.Equal(["good-token"], remaining);
    }

    [Fact]
    public async Task Patients_without_devices_get_no_push_and_no_sms_while_the_sms_flag_is_off()
    {
        var patient = await World.NewPatientAsync();
        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(1)));

        Assert.Empty(Factory.Push.Sent);
        Assert.Empty(Factory.Sms.Sent);
    }

    [Fact]
    public async Task With_the_sms_flag_on_patients_without_a_device_token_get_an_sms()
    {
        await using var factory = new MoonlightApiFactory(ConnectionString, settings: new() { ["Sms:NotificationsEnabled"] = "true" });
        var world = new TestWorld(factory);
        var doctor = await world.NewDoctorAsync(await world.NewClinicAsync());
        var withoutDevice = await world.NewPatientAsync();
        var withDevice = await world.NewPatientAsync();
        await withDevice.PutAsync("/api/v1/me/device-tokens", new { token = "abc", platform = "ios" });

        await world.BookAsync(withoutDevice, await world.NewSlotAsync(doctor, TimeSpan.FromDays(1)));
        await world.BookAsync(withDevice, await world.NewSlotAsync(doctor, TimeSpan.FromDays(2)));

        Assert.Equal(withoutDevice.User!.Phone, Assert.Single(factory.Sms.Sent).Phone);
        Assert.Single(factory.Push.Sent);
    }
}

public class ReminderTests(PostgresFixture db) : AppointmentTestBase(db)
{
    protected override bool IsolatedDatabase => true;

    private Task<int> RunRemindersAsync() => RunRemindersAsync(Factory);

    private static async Task<int> RunRemindersAsync(MoonlightApiFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<ReminderProcessor>().RunOnceAsync(CancellationToken.None);
    }

    private async Task<(Guid Id, Slot Slot, Session Patient)> BookWithDeviceAsync(TimeSpan fromNow, string language = "ar")
    {
        var patient = await World.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { preferredLanguage = language });
        await RegisterDeviceAsync(patient);
        var slot = await SlotAsync(fromNow);
        return (await World.BookAsync(patient, slot), slot, patient);
    }

    [Fact]
    public async Task Reminder_is_sent_once_when_the_visit_is_within_24_hours()
    {
        var (id, _, _) = await BookWithDeviceAsync(TimeSpan.FromHours(30), "en");
        var confirmations = Factory.Push.Sent.Count;

        Assert.Equal(0, await RunRemindersAsync());

        Factory.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(1, await RunRemindersAsync());
        Assert.Equal(0, await RunRemindersAsync());

        var (_, reminder) = Factory.Push.Sent[^1];
        Assert.Equal(confirmations + 1, Factory.Push.Sent.Count);
        Assert.Equal("appointment.reminder", reminder.Data["type"]);
        Assert.Equal("Appointment reminder", reminder.Title);
        Assert.Contains("Address", reminder.Body);
        Assert.NotNull((await AppointmentAsync(id)).ReminderSentAt);
    }

    [Fact]
    public async Task Reminder_skips_cancelled_bookings_made_inside_the_window_and_visits_within_two_hours()
    {
        var (cancelledId, _, patient) = await BookWithDeviceAsync(TimeSpan.FromHours(30));
        await patient.PostAsync($"{Patient(cancelledId)}/cancel");
        var (lateBooking, _, _) = await BookWithDeviceAsync(TimeSpan.FromHours(10));

        Factory.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(0, await RunRemindersAsync());
        Assert.Null((await AppointmentAsync(cancelledId)).ReminderSentAt);
        Assert.Null((await AppointmentAsync(lateBooking)).ReminderSentAt);
    }

    [Fact]
    public async Task Reminder_is_not_sent_less_than_two_hours_before_the_visit()
    {
        var (id, _, _) = await BookWithDeviceAsync(TimeSpan.FromHours(30));

        Factory.Clock.Advance(TimeSpan.FromHours(29));

        Assert.Equal(0, await RunRemindersAsync());
        Assert.Null((await AppointmentAsync(id)).ReminderSentAt);
    }

    [Fact]
    public async Task Reminder_is_reset_by_a_reschedule_and_sent_again_for_the_new_time()
    {
        var (id, _, patient) = await BookWithDeviceAsync(TimeSpan.FromHours(30));
        Factory.Clock.Advance(TimeSpan.FromHours(7));
        Assert.Equal(1, await RunRemindersAsync());
        var newSlot = await SlotAsync(TimeSpan.FromHours(48));

        Assert.Equal(200, (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = newSlot.Id })).StatusCode);
        Assert.Null((await AppointmentAsync(id)).ReminderSentAt);
        Assert.Equal(0, await RunRemindersAsync());

        Factory.Clock.Advance(TimeSpan.FromHours(26));
        Assert.Equal(1, await RunRemindersAsync());
        Assert.Equal(0, await RunRemindersAsync());
    }

    [Fact]
    public async Task Two_processors_running_at_once_send_each_reminder_only_once()
    {
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++)
        {
            ids.Add((await BookWithDeviceAsync(TimeSpan.FromHours(30) + TimeSpan.FromMinutes(30 * i))).Id);
        }

        Factory.Clock.Advance(TimeSpan.FromHours(9));
        var before = Factory.Push.Sent.Count;
        var counts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(RunRemindersAsync)));

        Assert.Equal(5, counts.Sum());
        Assert.Equal(before + 5, Factory.Push.Sent.Count);
    }
}

public class ReminderHostedServiceTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task The_hosted_service_is_only_registered_when_enabled()
    {
        await using var off = new MoonlightApiFactory(db.ConnectionString);
        await using var on = new MoonlightApiFactory(db.ConnectionString, settings: new() { ["Reminders:Enabled"] = "true" });

        Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), s => s is ReminderBackgroundService);
        Assert.Contains(on.Services.GetServices<IHostedService>(), s => s is ReminderBackgroundService);
    }

    [Fact]
    public async Task The_background_service_sends_due_reminders_when_it_starts()
    {
        // Seed with the service disabled, then start a second host on the same database with it enabled.
        await using var seed = new MoonlightApiFactory(db.ConnectionString);
        var world = new TestWorld(seed);
        var doctor = await world.NewDoctorAsync(await world.NewClinicAsync());
        var patient = await world.NewPatientAsync();
        await patient.PutAsync("/api/v1/me/device-tokens", new { token = "device", platform = "android" });
        await world.BookAsync(patient, await world.NewSlotAsync(doctor, TimeSpan.FromHours(30)));

        await using var running = new MoonlightApiFactory(db.ConnectionString, settings: new() { ["Reminders:Enabled"] = "true" });
        running.Clock.Advance(TimeSpan.FromHours(7));
        _ = running.Server;

        Assert.True(await running.Push.WaitForSendsAsync(1, TimeSpan.FromSeconds(30)));
        Assert.Equal("appointment.reminder", Assert.Single(running.Push.Sent).Message.Data["type"]);
    }
}
