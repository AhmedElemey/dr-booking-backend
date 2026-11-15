using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Features.WaitingList;

namespace Moonlight.Tests;

public class WaitingListTests(PostgresFixture db) : Phase2TestBase(db)
{
    private Task<Resp> JoinAsync(Session patient, DateOnly date, object? extra = null) =>
        patient.PostAsync("/api/v1/waiting-list", new { doctorId = Doctor.Id, date = date.ToString("yyyy-MM-dd") });

    private async Task<Session> DevicePatientAsync()
    {
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);
        return patient;
    }

    private Task<List<OutboundMessage>> OutboxAsync(Guid userId, string kind) =>
        World.WithDbAsync(d => d.OutboundMessages.AsNoTracking().Where(m => m.UserId == userId && m.Kind == kind).ToListAsync());

    [Fact]
    public async Task Join_list_and_cancel_and_duplicate_conflict()
    {
        Enable(FeatureKeys.WaitingList);
        var patient = await World.NewPatientAsync();
        var date = Today(Now).AddDays(3);

        var joined = await JoinAsync(patient, date);
        Assert.Equal(201, joined.StatusCode);
        Assert.Equal("Waiting", joined["status"].GetString());

        var duplicate = await JoinAsync(patient, date);
        Assert.Equal(409, duplicate.StatusCode);
        Assert.Equal("ALREADY_WAITING", duplicate.Code);

        var mine = await patient.GetAsync("/api/v1/waiting-list");
        var entry = Assert.Single(mine.Body.EnumerateArray());
        Assert.Equal(Doctor.Id, entry.GetProperty("doctor").GetProperty("id").GetGuid());

        var dashboard = await Staff.GetAsync($"/api/v1/dashboard/waiting-list?doctorId={Doctor.Id}&status=Waiting");
        Assert.Single(dashboard.Body.EnumerateArray());

        Assert.Equal(204, (await patient.DeleteAsync($"/api/v1/waiting-list/{joined.Id}")).StatusCode);
        Assert.Equal(201, (await JoinAsync(patient, date)).StatusCode); // can rejoin once cancelled
    }

    [Fact]
    public async Task Cancelling_a_booking_notifies_waiting_entries_of_that_doctor_and_day_only()
    {
        Enable(FeatureKeys.WaitingList);
        var slot = await LocalSlotAsync(3, 10);
        var date = ClinicTime.LocalDate(slot.StartsAt, Cairo);
        var booker = await World.NewPatientAsync();
        var id = await World.BookAsync(booker, slot);
        var waiting1 = await DevicePatientAsync();
        var waiting2 = await DevicePatientAsync();
        var elsewhere = await DevicePatientAsync();
        var first = await JoinAsync(waiting1, date);
        await JoinAsync(waiting2, date);
        await JoinAsync(elsewhere, date.AddDays(1));

        Assert.Equal(200, (await booker.PostAsync($"{Patient(id)}/cancel")).StatusCode);

        Assert.Single(await OutboxAsync(waiting1.User!.Id, "waitlist.slot-opened"));
        Assert.Single(await OutboxAsync(waiting2.User!.Id, "waitlist.slot-opened"));
        Assert.Empty(await OutboxAsync(elsewhere.User!.Id, "waitlist.slot-opened"));
        var mine = await waiting1.GetAsync("/api/v1/waiting-list");
        Assert.Equal("Notified", Assert.Single(mine.Body.EnumerateArray()).GetProperty("status").GetString());
        Assert.Equal(first.Id, Assert.Single(mine.Body.EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Booking_that_doctor_on_that_day_marks_the_entry_booked()
    {
        Enable(FeatureKeys.WaitingList);
        var slot = await LocalSlotAsync(3, 10);
        var date = ClinicTime.LocalDate(slot.StartsAt, Cairo);
        var patient = await World.NewPatientAsync();
        await JoinAsync(patient, date);

        await World.BookAsync(patient, slot);

        var mine = await patient.GetAsync("/api/v1/waiting-list");
        Assert.Equal("Booked", Assert.Single(mine.Body.EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task New_slot_and_unblock_notify_and_the_notification_is_sent_once()
    {
        Enable(FeatureKeys.WaitingList);
        var date = Today(Now).AddDays(4);
        var patient = await DevicePatientAsync();
        await JoinAsync(patient, date);

        var created = await Staff.PostAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/slots", new { startsAt = Cairo10(date), endsAt = Cairo10(date).AddMinutes(30) });
        Assert.Equal(201, created.StatusCode);
        Assert.Single(await OutboxAsync(patient.User!.Id, "waitlist.slot-opened"));

        // A second slot does not re-notify an already Notified entry.
        await Staff.PostAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/slots", new { startsAt = Cairo10(date).AddMinutes(30), endsAt = Cairo10(date).AddMinutes(60) });
        Assert.Single(await OutboxAsync(patient.User!.Id, "waitlist.slot-opened"));
    }

    private static DateTime Cairo10(DateOnly date)
    {
        Assert.True(ClinicTime.TryLocalToUtc(date.ToDateTime(new TimeOnly(10, 0)), Cairo, out var utc));
        return utc;
    }

    [Fact]
    public async Task Manual_notify_and_nightly_expiry()
    {
        Enable(FeatureKeys.WaitingList);
        var patient = await DevicePatientAsync();
        var joined = await JoinAsync(patient, Today(Now).AddDays(1));

        var notified = await Staff.PostAsync($"/api/v1/dashboard/waiting-list/{joined.Id}/notify");
        Assert.Equal(200, notified.StatusCode);
        Assert.Single(await OutboxAsync(patient.User!.Id, "waitlist.slot-opened"));

        Factory.Clock.Advance(TimeSpan.FromDays(3));
        using (var scope = Factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WaitingListService>().ExpireAsync(CancellationToken.None);
        }

        var status = await World.WithDbAsync(d => d.WaitingListEntries.AsNoTracking().Where(w => w.Id == joined.Id).Select(w => w.Status).FirstAsync());
        Assert.Equal(WaitingListStatus.Expired, status);
    }

    [Fact]
    public async Task Patients_cannot_see_or_cancel_each_others_entries()
    {
        Enable(FeatureKeys.WaitingList);
        var owner = await World.NewPatientAsync();
        var other = await World.NewPatientAsync();
        var joined = await JoinAsync(owner, Today(Now).AddDays(2));

        Assert.Equal(404, (await other.DeleteAsync($"/api/v1/waiting-list/{joined.Id}")).StatusCode);
        Assert.Empty((await other.GetAsync("/api/v1/waiting-list")).Body.EnumerateArray());
    }
}

public class FamilyTests(PostgresFixture db) : Phase2TestBase(db)
{
    [Fact]
    public async Task Family_member_crud_and_limit_of_ten()
    {
        Enable(FeatureKeys.FamilyAccounts);
        var owner = await World.NewPatientAsync();

        var created = await owner.PostAsync("/api/v1/me/family", new { fullName = "Omar", relation = "Child", dateOfBirth = "2018-05-01", gender = "Male" });
        Assert.Equal(201, created.StatusCode);
        var updated = await owner.PutAsync($"/api/v1/me/family/{created.Id}", new { fullName = "Omar A", relation = "Child" });
        Assert.Equal("Omar A", updated["fullName"].GetString());
        Assert.Single((await owner.GetAsync("/api/v1/me/family")).Body.EnumerateArray());

        for (var i = 1; i < 10; i++)
        {
            Assert.Equal(201, (await owner.PostAsync("/api/v1/me/family", new { fullName = "M" + i, relation = "Other" })).StatusCode);
        }

        var eleventh = await owner.PostAsync("/api/v1/me/family", new { fullName = "Extra", relation = "Other" });
        Assert.Equal(422, eleventh.StatusCode);

        Assert.Equal(204, (await owner.DeleteAsync($"/api/v1/me/family/{created.Id}")).StatusCode);
        var invalid = await owner.PostAsync("/api/v1/me/family", new { fullName = "", relation = "Child" });
        Assert.Equal(400, invalid.StatusCode);
    }

    [Fact]
    public async Task Booking_for_a_member_stores_it_and_limits_count_per_person()
    {
        Enable(FeatureKeys.FamilyAccounts);
        var owner = await World.NewPatientAsync();
        var omar = await NewMemberAsync(owner);
        var sara = await NewMemberAsync(owner, "Sara");

        var self = await owner.PostAsync("/api/v1/appointments", new { slotId = (await LocalSlotAsync(2, 10)).Id });
        Assert.Equal(201, self.StatusCode);

        // Same doctor, same day: the owner is limited, a family member is not.
        var at15 = await LocalSlotAsync(2, 15);
        var sameDaySelf = await owner.PostAsync("/api/v1/appointments", new { slotId = at15.Id });
        Assert.Equal("BOOKING_LIMIT", sameDaySelf.Code);
        var forOmar = await owner.PostAsync("/api/v1/appointments", new { slotId = at15.Id, familyMemberId = omar.Id });
        Assert.Equal(201, forOmar.StatusCode);
        Assert.Equal("Omar", forOmar["familyMember"].GetProperty("fullName").GetString());
        Assert.Equal("Child", forOmar["familyMember"].GetProperty("relation").GetString());

        // ... but not twice for Omar.
        var at16 = await LocalSlotAsync(2, 16);
        var again = await owner.PostAsync("/api/v1/appointments", new { slotId = at16.Id, familyMemberId = omar.Id });
        Assert.Equal("BOOKING_LIMIT", again.Code);
        Assert.Equal(201, (await owner.PostAsync("/api/v1/appointments", new { slotId = at16.Id, familyMemberId = sara.Id })).StatusCode);

        var dash = await Staff.GetAsync(Dash(forOmar.Id));
        Assert.Equal("Omar", dash["familyMember"].GetProperty("fullName").GetString());
        Assert.Equal(owner.User!.Id, dash["patient"].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Another_users_family_member_cannot_be_booked_for()
    {
        Enable(FeatureKeys.FamilyAccounts);
        var owner = await World.NewPatientAsync();
        var stranger = await World.NewPatientAsync();
        var omar = await NewMemberAsync(owner);

        var response = await stranger.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, familyMemberId = omar.Id });

        Assert.True(response.StatusCode >= 400, response.StatusCode.ToString());
        Assert.Empty(await World.WithDbAsync(d => d.Appointments.Where(a => a.FamilyMemberId == omar.Id).ToListAsync()));
    }
}
