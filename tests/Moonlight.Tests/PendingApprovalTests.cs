using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Notifications;

namespace Moonlight.Tests;

// Q2: with the manualReview switch on, a patient booking starts Pending and the clinic approves or rejects it.
// The switch is off by default (clinic.config.json), so bookings are Confirmed at once unless a test turns it on.
public class PendingApprovalTests(PostgresFixture db) : Phase2TestBase(db)
{
    public override async Task InitializeAsync()
    {
        await base.InitializeAsync();
        Enable(FeatureKeys.ManualReview);
    }

    private async Task<int> ExpireAsync()
    {
        await using var scope = Factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<PendingExpiryProcessor>().RunOnceAsync(CancellationToken.None);
    }

    [Fact]
    public async Task With_manual_review_off_a_patient_booking_is_confirmed_at_once()
    {
        Disable(FeatureKeys.ManualReview);
        var patient = await World.NewPatientAsync();
        var slot = await SlotAsync(TimeSpan.FromDays(1));

        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Confirmed", response["status"].GetString());
    }

    [Fact]
    public async Task An_unanswered_pending_booking_expires_when_its_visit_time_arrives()
    {
        var (id, slot, patient) = await BookPendingAsync(TimeSpan.FromHours(3));
        await RegisterDeviceAsync(patient);
        var before = Factory.Push.Sent.Count;

        Assert.Equal(0, await ExpireAsync());
        Assert.Equal(AppointmentStatus.Pending, (await AppointmentAsync(id)).Status);

        Factory.Clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(1, await ExpireAsync());

        var appointment = await AppointmentAsync(id);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(CancelledBy.Clinic, appointment.CancelledBy);
        Assert.Equal(before + 1, Factory.Push.Sent.Count);
        Assert.Equal(0, await ExpireAsync());
    }

    [Fact]
    public async Task A_confirmed_booking_is_never_expired()
    {
        var (id, _, _) = await BookPendingAsync(TimeSpan.FromHours(3));
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(id)}/approve")).StatusCode);

        Factory.Clock.Advance(TimeSpan.FromHours(4));

        Assert.Equal(0, await ExpireAsync());
        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(id)).Status);
    }

    private async Task<(Guid Id, Slot Slot, Session Patient)> BookPendingAsync(TimeSpan fromNow)
    {
        var patient = await World.NewPatientAsync();
        var slot = await SlotAsync(fromNow);
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id });
        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Pending", response["status"].GetString());
        return (response.Id, slot, patient);
    }

    [Fact]
    public async Task A_patient_booking_starts_pending_holds_the_slot_and_shows_in_upcoming()
    {
        var (id, slot, patient) = await BookPendingAsync(TimeSpan.FromDays(1));

        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
        Assert.Equal(AppointmentStatus.Pending, (await AppointmentAsync(id)).Status);

        var upcoming = await patient.GetAsync("/api/v1/appointments?scope=upcoming");
        Assert.Equal(200, upcoming.StatusCode);
        Assert.Equal(id, Assert.Single(upcoming["items"].EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Approving_a_pending_booking_confirms_it_and_notifies_the_patient()
    {
        var (id, _, patient) = await BookPendingAsync(TimeSpan.FromDays(1));
        await RegisterDeviceAsync(patient);

        var approved = await Staff.PostAsync($"{Dash(id)}/approve");

        Assert.Equal(200, approved.StatusCode);
        Assert.Equal("Confirmed", approved["status"].GetString());
        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(id)).Status);
        var (_, message) = Assert.Single(Factory.Push.Sent);
        Assert.Equal("appointment.confirmed", message.Data["type"]);
    }

    [Fact]
    public async Task Only_a_pending_booking_can_be_approved_or_rejected()
    {
        var (id, _, _) = await BookPendingAsync(TimeSpan.FromDays(1));
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(id)}/approve")).StatusCode);

        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/approve")).Code);
        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/reject", new { reason = "x" })).Code);
        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task Rejecting_a_pending_booking_cancels_it_frees_the_slot_and_notifies_the_patient()
    {
        var (id, slot, patient) = await BookPendingAsync(TimeSpan.FromDays(1));
        await RegisterDeviceAsync(patient);
        var before = Factory.Push.Sent.Count;

        var rejected = await Staff.PostAsync($"{Dash(id)}/reject", new { reason = "no capacity" });

        Assert.Equal(200, rejected.StatusCode);
        Assert.Equal("Cancelled", rejected["status"].GetString());
        var appointment = await AppointmentAsync(id);
        Assert.Equal(AppointmentStatus.Cancelled, appointment.Status);
        Assert.Equal(CancelledBy.Clinic, appointment.CancelledBy);
        Assert.Equal("no capacity", appointment.CancelReason);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
        Assert.Equal(before + 1, Factory.Push.Sent.Count);
        Assert.Equal("appointment.cancelled", Factory.Push.Sent[^1].Message.Data["type"]);

        // The freed slot is bookable right away.
        Assert.Equal(201, (await (await World.NewPatientAsync()).PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
    }

    [Fact]
    public async Task The_patient_cancels_a_pending_booking_without_the_cancel_window()
    {
        // 76 minutes out is beyond the lead time but well inside the 2-hour cancel window.
        var (id, slot, patient) = await BookPendingAsync(TimeSpan.FromMinutes(76));

        var cancelled = await patient.PostAsync($"{Patient(id)}/cancel", new { reason = "changed my mind" });

        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal("Cancelled", cancelled["status"].GetString());
        Assert.Equal(CancelledBy.Patient, (await AppointmentAsync(id)).CancelledBy);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task A_pending_booking_cannot_be_rescheduled_completed_cancelled_or_no_showed()
    {
        var (id, slot, patient) = await BookPendingAsync(TimeSpan.FromDays(1));
        var other = await SlotAsync(TimeSpan.FromDays(2));

        Assert.Equal("INVALID_TRANSITION", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = other.Id })).Code);
        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/cancel", new { reason = "x" })).Code);
        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/reschedule", new { slotId = other.Id })).Code);
        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/complete")).Code);
        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/no-show")).Code);
        Assert.Equal(AppointmentStatus.Pending, (await AppointmentAsync(id)).Status);
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task Pending_bookings_count_toward_the_booking_limits()
    {
        var patient = await World.NewPatientAsync();
        for (var i = 0; i < 3; i++)
        {
            var slot = await SlotAsync(TimeSpan.FromDays(1 + i), doctor: i == 0 ? Doctor : await World.NewDoctorAsync(Clinic, $"Dr {i}"));
            var booked = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id });
            Assert.Equal(201, booked.StatusCode);
        }

        var fourth = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(5), await World.NewDoctorAsync(Clinic, "Dr 4"))).Id });
        Assert.Equal("BOOKING_LIMIT", fourth.Code);
    }

    [Fact]
    public async Task The_dashboard_lists_pending_bookings_and_staff_bookings_stay_confirmed()
    {
        var (id, _, _) = await BookPendingAsync(TimeSpan.FromDays(1));
        var staffBooked = await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, patientPhone = "01012345678", patientName = "Walk In" });
        Assert.Equal("Confirmed", staffBooked["status"].GetString());

        var pending = await Staff.GetAsync("/api/v1/dashboard/appointments?status=Pending");
        Assert.Equal(200, pending.StatusCode);
        Assert.Equal(id, Assert.Single(pending["items"].EnumerateArray()).GetProperty("id").GetGuid());

        var confirmed = await Staff.GetAsync("/api/v1/dashboard/appointments?status=Confirmed");
        Assert.Equal(staffBooked.Id, Assert.Single(confirmed["items"].EnumerateArray()).GetProperty("id").GetGuid());
    }
}
