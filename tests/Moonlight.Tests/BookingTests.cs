using Moonlight.Api.Domain;

namespace Moonlight.Tests;

public class BookingTests(PostgresFixture db) : AppointmentTestBase(db)
{
    [Fact]
    public async Task Patient_books_an_open_slot_and_it_becomes_reserved()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        var patient = await World.NewPatientAsync();

        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id, serviceId = Service.Id, patientNote = "  tooth pain " });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Pending", response["status"].GetString());
        Assert.Equal(slot.Id, response["slotId"].GetGuid());
        Assert.Equal("tooth pain", response["patientNote"].GetString());
        Assert.Equal(slot.StartsAt, response["startsAt"].GetDateTime().ToUniversalTime());
        Assert.Equal("Africa/Cairo", response["timezone"].GetString());
        Assert.Equal(Doctor.Slug, response["doctor"].GetProperty("slug").GetString());
        Assert.Equal(Clinic.Phone, response["clinic"].GetProperty("phone").GetString());
        Assert.Equal(Service.Id, response["service"].GetProperty("id").GetGuid());
        var local = response["startsAtLocal"].GetDateTimeOffset();
        Assert.Equal(slot.StartsAt, local.UtcDateTime);
        Assert.Equal(Cairo.GetUtcOffset(slot.StartsAt), local.Offset);
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task Booking_requires_login_and_the_patient_role()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));

        Assert.Equal(401, (await World.Anonymous().PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
        Assert.Equal(403, (await Staff.PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task A_taken_slot_is_slot_taken()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        await World.BookAsync(await World.NewPatientAsync(), slot);

        var response = await (await World.NewPatientAsync()).PostAsync("/api/v1/appointments", new { slotId = slot.Id });

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("SLOT_TAKEN", response.Code);
    }

    [Fact]
    public async Task A_blocked_slot_is_slot_taken()
    {
        var slot = await World.NewSlotAsync(Doctor, TimeSpan.FromDays(1), status: SlotStatus.Blocked);

        Assert.Equal("SLOT_TAKEN", (await (await World.NewPatientAsync()).PostAsync("/api/v1/appointments", new { slotId = slot.Id })).Code);
    }

    [Theory]
    [InlineData(-120)]
    [InlineData(30)]
    public async Task Past_slots_and_slots_inside_the_lead_time_are_slot_unavailable(int minutesFromNow)
    {
        var slot = await SlotAsync(TimeSpan.FromMinutes(minutesFromNow));

        var response = await (await World.NewPatientAsync()).PostAsync("/api/v1/appointments", new { slotId = slot.Id });

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("SLOT_UNAVAILABLE", response.Code);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task A_slot_just_beyond_the_lead_time_can_be_booked()
    {
        var slot = await SlotAsync(TimeSpan.FromMinutes(76));

        Assert.Equal(201, (await (await World.NewPatientAsync()).PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
    }

    [Fact]
    public async Task Slots_of_unpublished_doctors_or_unknown_slots_are_not_found()
    {
        var hidden = await World.NewDoctorAsync(Clinic, "Hidden", published: false);
        var slot = await SlotAsync(TimeSpan.FromDays(1), hidden);
        var patient = await World.NewPatientAsync();

        Assert.Equal(404, (await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
        Assert.Equal(404, (await patient.PostAsync("/api/v1/appointments", new { slotId = Guid.NewGuid() })).StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await patient.PostAsync("/api/v1/appointments", new { })).Code);
    }

    [Fact]
    public async Task The_service_must_belong_to_the_doctor()
    {
        var other = await World.NewDoctorAsync(Clinic, "Other");
        var foreign = await World.NewServiceAsync(other);
        var inactive = await World.NewServiceAsync(Doctor, active: false);
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        var patient = await World.NewPatientAsync();

        Assert.Equal("VALIDATION_FAILED", (await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id, serviceId = foreign.Id })).Code);
        Assert.Equal("VALIDATION_FAILED", (await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id, serviceId = inactive.Id })).Code);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task Patients_list_their_own_upcoming_and_past_appointments_with_paging()
    {
        var patient = await World.NewPatientAsync();
        var other = await World.NewPatientAsync();
        var s1 = await LocalSlotAsync(2, 10);
        var s2 = await LocalSlotAsync(3, 10);
        var s3 = await LocalSlotAsync(4, 10);
        var a1 = await World.BookAsync(patient, s1);
        var a2 = await World.BookAsync(patient, s2);
        var a3 = await World.BookAsync(patient, s3);
        await World.BookAsync(other, await LocalSlotAsync(5, 10));
        await patient.PostAsync($"{Patient(a3)}/cancel");

        var upcoming = await patient.GetAsync("/api/v1/appointments?scope=upcoming");
        Assert.Equal([a1, a2], upcoming["items"].EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal(2, upcoming["totalCount"].GetInt32());

        var past = await patient.GetAsync("/api/v1/appointments?scope=past");
        Assert.Equal([a3], past["items"].EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        var paged = await patient.GetAsync("/api/v1/appointments?pageSize=1&page=2");
        Assert.Equal([a2], paged["items"].EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        Assert.Equal("VALIDATION_FAILED", (await patient.GetAsync("/api/v1/appointments?scope=nope")).Code);

        // A visit that already started is "past" even if still Confirmed.
        Factory.Clock.Advance(TimeSpan.FromDays(3) + TimeSpan.FromHours(12));
        Assert.Equal(3, (await patient.GetAsync("/api/v1/appointments?scope=past"))["totalCount"].GetInt32());
    }

    [Fact]
    public async Task Another_patients_appointment_is_not_found()
    {
        var id = await World.BookAsync(await World.NewPatientAsync(), await SlotAsync(TimeSpan.FromDays(1)));
        var stranger = await World.NewPatientAsync();

        Assert.Equal(404, (await stranger.GetAsync(Patient(id))).StatusCode);
        Assert.Equal(404, (await stranger.PostAsync($"{Patient(id)}/cancel")).StatusCode);
        Assert.Equal(404, (await stranger.PostAsync($"{Patient(id)}/reschedule", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id })).StatusCode);
        Assert.Equal(404, (await stranger.GetAsync(Patient(Guid.NewGuid()))).StatusCode);
    }

    [Fact]
    public async Task Fourth_upcoming_booking_is_a_booking_limit_error()
    {
        var patient = await World.NewPatientAsync();
        for (var day = 2; day <= 4; day++)
        {
            await World.BookAsync(patient, await LocalSlotAsync(day, 10));
        }

        var fourth = await LocalSlotAsync(5, 10);
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = fourth.Id });

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("BOOKING_LIMIT", response.Code);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(fourth.Id));

        // Cancelling one frees a place.
        var first = (await patient.GetAsync("/api/v1/appointments"))["items"][0].GetProperty("id").GetGuid();
        await patient.PostAsync($"{Patient(first)}/cancel");
        Assert.Equal(201, (await patient.PostAsync("/api/v1/appointments", new { slotId = fourth.Id })).StatusCode);
    }

    [Fact]
    public async Task Second_booking_with_the_same_doctor_on_the_same_day_is_a_booking_limit_error()
    {
        var patient = await World.NewPatientAsync();
        await World.BookAsync(patient, await LocalSlotAsync(2, 10));

        var sameDay = await patient.PostAsync("/api/v1/appointments", new { slotId = (await LocalSlotAsync(2, 15)).Id });
        Assert.Equal("BOOKING_LIMIT", sameDay.Code);

        // Same day with another doctor, or the same doctor on another day, is fine.
        var other = await World.NewDoctorAsync(Clinic, "Second");
        Assert.Equal(201, (await patient.PostAsync("/api/v1/appointments", new { slotId = (await LocalSlotAsync(2, 15, other)).Id })).StatusCode);
        Assert.Equal(201, (await patient.PostAsync("/api/v1/appointments", new { slotId = (await LocalSlotAsync(3, 10)).Id })).StatusCode);
    }

    [Fact]
    public async Task Booking_limits_are_configurable()
    {
        await using var factory = new MoonlightApiFactory(ConnectionString, settings: new() { ["Booking:MaxUpcomingPerPatient"] = "1" });
        var world = new TestWorld(factory);
        var clinic = await world.NewClinicAsync();
        var doctor = await world.NewDoctorAsync(clinic);
        var patient = await world.NewPatientAsync();

        await world.BookAsync(patient, await world.NewSlotAsync(doctor, TimeSpan.FromDays(2)));
        var second = await patient.PostAsync("/api/v1/appointments", new { slotId = (await world.NewSlotAsync(doctor, TimeSpan.FromDays(4))).Id });

        Assert.Equal("BOOKING_LIMIT", second.Code);
    }

    [Fact]
    public async Task Patient_cancels_inside_the_window_and_the_slot_is_bookable_again_immediately()
    {
        var slot = await SlotAsync(TimeSpan.FromHours(5));
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, slot);

        var cancelled = await patient.PostAsync($"{Patient(id)}/cancel", new { reason = "changed my mind" });

        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal("Cancelled", cancelled["status"].GetString());
        Assert.Equal("Patient", cancelled["cancelledBy"].GetString());
        Assert.Equal("changed my mind", cancelled["cancelReason"].GetString());
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));

        var next = await World.NewPatientAsync();
        Assert.Equal(201, (await next.PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
        Assert.Equal(2, (await World.AppointmentsOfSlotAsync(slot.Id)).Count);
        Assert.Equal(1, (await World.AppointmentsOfSlotAsync(slot.Id)).Count(a => a.Status != AppointmentStatus.Cancelled));
    }

    [Fact]
    public async Task Patient_cannot_cancel_or_reschedule_inside_the_cancel_window()
    {
        var slot = await SlotAsync(TimeSpan.FromMinutes(100));
        var target = await SlotAsync(TimeSpan.FromDays(2));
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, slot);

        var cancel = await patient.PostAsync($"{Patient(id)}/cancel");
        Assert.Equal(409, cancel.StatusCode);
        Assert.Equal("CANCEL_WINDOW_PASSED", cancel.Code);
        Assert.Equal("CANCEL_WINDOW_PASSED", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = target.Id })).Code);

        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(id)).Status);
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(target.Id));
    }

    [Fact]
    public async Task Cancel_window_follows_configuration()
    {
        await using var factory = new MoonlightApiFactory(ConnectionString, settings: new() { ["Booking:PatientCancelWindowHours"] = "24" });
        var world = new TestWorld(factory);
        var doctor = await world.NewDoctorAsync(await world.NewClinicAsync());
        var patient = await world.NewPatientAsync();
        var id = await world.BookAsync(patient, await world.NewSlotAsync(doctor, TimeSpan.FromHours(10)));

        Assert.Equal("CANCEL_WINDOW_PASSED", (await patient.PostAsync($"/api/v1/appointments/{id}/cancel")).Code);
    }

    [Fact]
    public async Task Patient_reschedules_to_another_open_slot_of_the_same_doctor()
    {
        var oldSlot = await SlotAsync(TimeSpan.FromDays(1));
        var newSlot = await SlotAsync(TimeSpan.FromDays(3));
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, oldSlot);
        await World.WithDbAsync(async d =>
        {
            (await d.Appointments.FindAsync(id))!.ReminderSentAt = Now;
            await d.SaveChangesAsync();
            return 0;
        });

        var moved = await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = newSlot.Id });

        Assert.Equal(200, moved.StatusCode);
        Assert.Equal("Confirmed", moved["status"].GetString());
        Assert.Equal(newSlot.Id, moved["slotId"].GetGuid());
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(oldSlot.Id));
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(newSlot.Id));
        Assert.Null((await AppointmentAsync(id)).ReminderSentAt);

        // The freed slot is bookable right away.
        Assert.Equal(201, (await (await World.NewPatientAsync()).PostAsync("/api/v1/appointments", new { slotId = oldSlot.Id })).StatusCode);
    }

    [Fact]
    public async Task Reschedule_rejects_taken_slots_other_doctors_and_the_current_slot()
    {
        var current = await SlotAsync(TimeSpan.FromDays(1));
        var taken = await SlotAsync(TimeSpan.FromDays(2));
        var other = await World.NewDoctorAsync(Clinic, "Other");
        var otherSlot = await SlotAsync(TimeSpan.FromDays(3), other);
        var tooSoon = await SlotAsync(TimeSpan.FromMinutes(20));
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, current);
        await World.BookAsync(await World.NewPatientAsync(), taken);

        Assert.Equal("SLOT_TAKEN", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = taken.Id })).Code);
        Assert.Equal("SLOT_TAKEN", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = current.Id })).Code);
        Assert.Equal("VALIDATION_FAILED", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = otherSlot.Id })).Code);
        Assert.Equal("SLOT_UNAVAILABLE", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = tooSoon.Id })).Code);
        Assert.Equal(404, (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = Guid.NewGuid() })).StatusCode);

        Assert.Equal(current.Id, (await AppointmentAsync(id)).SlotId);
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(current.Id));
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(otherSlot.Id));
    }

    [Fact]
    public async Task Reschedule_respects_the_one_per_doctor_per_day_limit()
    {
        var patient = await World.NewPatientAsync();
        await World.BookAsync(patient, await LocalSlotAsync(2, 10));
        var id = await World.BookAsync(patient, await LocalSlotAsync(3, 10));

        var response = await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = (await LocalSlotAsync(2, 15)).Id });
        Assert.Equal("BOOKING_LIMIT", response.Code);

        // Moving within the same day is fine: it does not clash with itself.
        var sameDay = await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = (await LocalSlotAsync(3, 15)).Id });
        Assert.Equal(200, sameDay.StatusCode);
    }

    [Fact]
    public async Task Cancelled_appointments_cannot_be_cancelled_or_rescheduled_again()
    {
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(1)));
        await patient.PostAsync($"{Patient(id)}/cancel");

        var again = await patient.PostAsync($"{Patient(id)}/cancel");
        Assert.Equal(409, again.StatusCode);
        Assert.Equal("INVALID_TRANSITION", again.Code);
        Assert.Equal("INVALID_TRANSITION", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id })).Code);
    }
}
