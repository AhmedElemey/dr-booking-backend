using Moonlight.Api.Domain;

namespace Moonlight.Tests;

public class DashboardAppointmentTests(PostgresFixture db) : AppointmentTestBase(db)
{
    private async Task<(Guid Id, Slot Slot, Session Patient)> BookedAsync(TimeSpan fromNow)
    {
        var slot = await SlotAsync(fromNow);
        var patient = await World.NewPatientAsync();
        return (await World.BookAsync(patient, slot), slot, patient);
    }

    [Fact]
    public async Task Staff_book_on_behalf_creating_the_patient_by_phone()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        var phone = TestWorld.NewPhone();

        var response = await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = slot.Id, serviceId = Service.Id, patientPhone = phone.Replace("+20", "0"), patientName = "Hoda Samir", patientNote = "phone call" });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("Confirmed", response["status"].GetString());
        Assert.Equal(phone, response["patient"].GetProperty("phone").GetString());
        Assert.Equal("Hoda Samir", response["patient"].GetProperty("fullName").GetString());
        Assert.Equal(Doctor.Id, response["doctor"].GetProperty("id").GetGuid());
        Assert.False(response.Body.TryGetProperty("clinic", out _));
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
        var patient = await World.WithDbAsync(d => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.SingleAsync(d.Users, u => u.Phone == phone));
        Assert.Equal(UserRole.Patient, patient.Role);

        // When the patient later logs in with the same phone they see the booking.
        var session = await World.LoginAsync(phone);
        Assert.Equal(1, (await session.GetAsync("/api/v1/appointments"))["totalCount"].GetInt32());
    }

    [Fact]
    public async Task On_behalf_booking_reuses_an_existing_patient_and_keeps_their_name()
    {
        var patient = await World.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { fullName = "Original Name" });

        var response = await DoctorSession.PostAsync("/api/v1/dashboard/appointments", new
        {
            slotId = (await SlotAsync(TimeSpan.FromDays(1))).Id,
            patientPhone = patient.User!.Phone,
            patientName = "Different Name",
        });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal(patient.User.Id, response["patient"].GetProperty("id").GetGuid());
        Assert.Equal("Original Name", response["patient"].GetProperty("fullName").GetString());
    }

    [Fact]
    public async Task On_behalf_booking_for_a_doctor_or_staff_phone_is_phone_in_use()
    {
        var staffPhone = (await World.WithDbAsync(d => Microsoft.EntityFrameworkCore.EntityFrameworkQueryableExtensions.FirstAsync(d.Users, u => u.Role == UserRole.ClinicStaff))).Phone;
        var doctorPhone = (await World.WithDbAsync(d => d.Users.FindAsync(Doctor.UserId).AsTask()))!.Phone;
        var slot = await SlotAsync(TimeSpan.FromDays(1));

        foreach (var phone in new[] { staffPhone, doctorPhone })
        {
            var response = await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = slot.Id, patientPhone = phone, patientName = "X" });
            Assert.Equal(409, response.StatusCode);
            Assert.Equal("PHONE_IN_USE", response.Code);
        }

        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task On_behalf_booking_validates_input_and_is_not_limited_like_patients()
    {
        Assert.Equal("VALIDATION_FAILED", (await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(1))).Id, patientPhone = "123", patientName = "X" })).Code);
        Assert.Equal("VALIDATION_FAILED", (await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(1) + TimeSpan.FromHours(3))).Id, patientPhone = TestWorld.NewPhone() })).Code);

        // Four bookings for one phone, two of them with the same doctor on one day, plus a slot inside the 60 minute lead time.
        var phone = TestWorld.NewPhone();
        foreach (var slot in new[] { await LocalSlotAsync(2, 10), await LocalSlotAsync(2, 12), await LocalSlotAsync(3, 10), await LocalSlotAsync(4, 10), await SlotAsync(TimeSpan.FromMinutes(20)) })
        {
            Assert.Equal(201, (await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = slot.Id, patientPhone = phone, patientName = "Regular" })).StatusCode);
        }
    }

    [Fact]
    public async Task On_behalf_booking_of_a_taken_slot_is_slot_taken_and_a_past_slot_unavailable()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        await World.BookAsync(await World.NewPatientAsync(), slot);
        var past = await SlotAsync(TimeSpan.FromHours(-2));

        Assert.Equal("SLOT_TAKEN", (await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = slot.Id, patientPhone = TestWorld.NewPhone(), patientName = "X" })).Code);
        Assert.Equal("SLOT_UNAVAILABLE", (await Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = past.Id, patientPhone = TestWorld.NewPhone(), patientName = "X" })).Code);
    }

    [Fact]
    public async Task Dashboard_list_and_detail_show_patient_data_within_scope_only()
    {
        var (id, slot, patient) = await BookedAsync(TimeSpan.FromDays(1));
        var otherClinic = await World.NewClinicAsync("Other");
        var otherDoctor = await World.NewDoctorAsync(otherClinic, "Elsewhere");
        var foreign = await World.BookAsync(await World.NewPatientAsync(), await SlotAsync(TimeSpan.FromDays(1), otherDoctor));
        var otherDoctorInClinic = await World.NewDoctorAsync(Clinic, "Colleague");
        var colleague = await World.BookAsync(await World.NewPatientAsync(), await SlotAsync(TimeSpan.FromDays(2), otherDoctorInClinic));

        var staffList = await Staff.GetAsync("/api/v1/dashboard/appointments");
        Assert.Equal([id, colleague], staffList["items"].EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));
        var doctorList = await DoctorSession.GetAsync("/api/v1/dashboard/appointments");
        Assert.Equal([id], doctorList["items"].EnumerateArray().Select(i => i.GetProperty("id").GetGuid()));

        var detail = await DoctorSession.GetAsync(Dash(id));
        Assert.Equal(patient.User!.Phone, detail["patient"].GetProperty("phone").GetString());
        Assert.Equal(slot.StartsAt, detail["startsAt"].GetDateTime().ToUniversalTime());

        Assert.Equal(404, (await Staff.GetAsync(Dash(foreign))).StatusCode);
        Assert.Equal(404, (await DoctorSession.GetAsync(Dash(colleague))).StatusCode);
        Assert.Equal(404, (await Staff.PostAsync($"{Dash(foreign)}/cancel")).StatusCode);
        Assert.Equal(404, (await DoctorSession.PostAsync($"{Dash(colleague)}/complete")).StatusCode);
        Assert.Equal(404, (await Staff.PostAsync($"{Dash(foreign)}/reschedule", new { slotId = Guid.NewGuid() })).StatusCode);
    }

    [Fact]
    public async Task Dashboard_list_filters_by_doctor_status_and_clinic_local_dates()
    {
        var colleague = await World.NewDoctorAsync(Clinic, "Colleague");
        var day2 = await World.BookAsync(await World.NewPatientAsync(), await LocalSlotAsync(2, 10));
        var day3 = await World.BookAsync(await World.NewPatientAsync(), await LocalSlotAsync(3, 10));
        var cancelled = await World.BookAsync(await World.NewPatientAsync(), await LocalSlotAsync(3, 12, colleague));
        await Staff.PostAsync($"{Dash(cancelled)}/cancel");
        var today = ClinicTime.LocalDate(Now, Cairo);
        string D(int n) => today.AddDays(n).ToString("yyyy-MM-dd");

        Ids(await Staff.GetAsync($"/api/v1/dashboard/appointments?from={D(3)}&to={D(3)}"), day3, cancelled);
        Ids(await Staff.GetAsync($"/api/v1/dashboard/appointments?from={D(2)}&to={D(2)}"), day2);
        Ids(await Staff.GetAsync($"/api/v1/dashboard/appointments?status=Cancelled"), cancelled);
        Ids(await Staff.GetAsync($"/api/v1/dashboard/appointments?doctorId={colleague.Id}"), cancelled);
        Ids(await Staff.GetAsync($"/api/v1/dashboard/appointments?from={D(4)}"));
        Assert.Equal(400, (await Staff.GetAsync("/api/v1/dashboard/appointments?status=Bogus")).StatusCode);
        Assert.Equal(404, (await Staff.GetAsync($"/api/v1/dashboard/appointments?doctorId={Guid.NewGuid()}")).StatusCode);

        static void Ids(Resp response, params Guid[] expected) =>
            Assert.Equal(expected.Order(), response["items"].EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Order());
    }

    [Fact]
    public async Task Clinic_cancel_marks_cancelled_by_clinic_and_frees_the_slot()
    {
        var (id, slot, _) = await BookedAsync(TimeSpan.FromHours(1));

        var response = await DoctorSession.PostAsync($"{Dash(id)}/cancel", new { reason = "emergency" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Cancelled", response["status"].GetString());
        Assert.Equal("Clinic", response["cancelledBy"].GetString());
        Assert.Equal("emergency", response["cancelReason"].GetString());
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task Clinic_cancel_inside_the_patient_window_is_allowed_but_not_after_the_start()
    {
        var inside = await BookedAsync(TimeSpan.FromMinutes(100));
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(inside.Id)}/cancel")).StatusCode);

        var started = await BookedAsync(TimeSpan.FromMinutes(180));
        Factory.Clock.Advance(TimeSpan.FromHours(4));
        var response = await Staff.PostAsync($"{Dash(started.Id)}/cancel");
        Assert.Equal(409, response.StatusCode);
        Assert.Equal("INVALID_TRANSITION", response.Code);
        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(started.Id)).Status);
    }

    [Theory]
    [InlineData("complete", "Completed")]
    [InlineData("no-show", "NoShow")]
    public async Task Complete_and_no_show_need_the_visit_to_have_started(string action, string expected)
    {
        var (id, slot, _) = await BookedAsync(TimeSpan.FromHours(2));

        var early = await Staff.PostAsync($"{Dash(id)}/{action}");
        Assert.Equal(409, early.StatusCode);
        Assert.Equal("INVALID_TRANSITION", early.Code);

        Factory.Clock.Advance(TimeSpan.FromHours(3));
        var done = await Staff.PostAsync($"{Dash(id)}/{action}");
        Assert.Equal(200, done.StatusCode);
        Assert.Equal(expected, done["status"].GetString());
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
    }

    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Completed")]
    [InlineData("NoShow")]
    public async Task Every_transition_out_of_a_terminal_status_is_invalid(string terminal)
    {
        var (id, _, patient) = await BookedAsync(TimeSpan.FromHours(2));
        var target = await SlotAsync(TimeSpan.FromDays(3));
        if (terminal == "Cancelled")
        {
            await Staff.PostAsync($"{Dash(id)}/cancel");
        }
        else
        {
            Factory.Clock.Advance(TimeSpan.FromHours(3));
            await Staff.PostAsync($"{Dash(id)}/{(terminal == "Completed" ? "complete" : "no-show")}");
        }

        Assert.Equal(Enum.Parse<AppointmentStatus>(terminal), (await AppointmentAsync(id)).Status);
        foreach (var path in new[] { "cancel", "complete", "no-show" })
        {
            var response = await Staff.PostAsync($"{Dash(id)}/{path}");
            Assert.Equal(409, response.StatusCode);
            Assert.Equal("INVALID_TRANSITION", response.Code);
        }

        Assert.Equal("INVALID_TRANSITION", (await Staff.PostAsync($"{Dash(id)}/reschedule", new { slotId = target.Id })).Code);
        Assert.Equal("INVALID_TRANSITION", (await patient.PostAsync($"{Patient(id)}/cancel")).Code);
        Assert.Equal("INVALID_TRANSITION", (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = target.Id })).Code);
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(target.Id));
    }

    [Fact]
    public async Task Staff_reschedule_moves_the_booking_without_patient_limits_and_resets_the_reminder()
    {
        var (id, oldSlot, _) = await BookedAsync(TimeSpan.FromMinutes(100));
        var newSlot = await SlotAsync(TimeSpan.FromMinutes(30));

        var response = await Staff.PostAsync($"{Dash(id)}/reschedule", new { slotId = newSlot.Id });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(newSlot.Id, response["slotId"].GetGuid());
        Assert.Equal(SlotStatus.Open, await World.SlotStatusAsync(oldSlot.Id));
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(newSlot.Id));
        Assert.Equal("SLOT_TAKEN", (await Staff.PostAsync($"{Dash(id)}/reschedule", new { slotId = newSlot.Id })).Code);
    }

    [Fact]
    public async Task Concurrent_clicks_on_one_appointment_yield_one_success_and_one_conflict()
    {
        var (id, _, _) = await BookedAsync(TimeSpan.FromHours(5));

        var results = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => Staff.PostAsync($"{Dash(id)}/cancel")));

        Assert.Equal(1, results.Count(r => r.StatusCode == 200));
        var loser = Assert.Single(results, r => r.StatusCode != 200);
        Assert.Equal("INVALID_TRANSITION", loser.Code);
    }

    [Fact]
    public async Task Patient_tokens_cannot_use_dashboard_endpoints_and_the_reverse()
    {
        var patient = await World.NewPatientAsync();

        Assert.Equal(403, (await patient.GetAsync("/api/v1/dashboard/appointments")).StatusCode);
        Assert.Equal(403, (await Staff.GetAsync("/api/v1/appointments")).StatusCode);
        Assert.Equal(401, (await World.Anonymous().GetAsync("/api/v1/dashboard/appointments")).StatusCode);
        var admin = await World.LoginAsync(await World.NewAdminAsync());
        Assert.Equal(403, (await admin.GetAsync("/api/v1/dashboard/appointments")).StatusCode);
    }
}
