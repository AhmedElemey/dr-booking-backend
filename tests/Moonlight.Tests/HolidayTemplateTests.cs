using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;

using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public class HolidayTemplateTests(PostgresFixture db) : Phase2TestBase(db)
{
    private async Task<Resp> AddHolidayAsync(DateOnly date, string name = "Test Holiday") =>
        await Staff.PostAsync("/api/v1/dashboard/holidays", new { clinicId = Clinic.Id, date = date.ToString("yyyy-MM-dd"), nameAr = name + " ar", nameEn = name });

    [Fact]
    public async Task Holiday_crud_and_duplicate_date_is_rejected()
    {
        Enable(FeatureKeys.HolidayCalendar);
        var date = Today(Now).AddDays(10);

        var created = await AddHolidayAsync(date);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("Manual", created["source"].GetString());

        Assert.Equal(400, (await AddHolidayAsync(date)).StatusCode);

        var list = await Staff.GetAsync($"/api/v1/dashboard/holidays?clinicId={Clinic.Id}&year={date.Year}");
        Assert.Equal(200, list.StatusCode);
        Assert.Single(list.Body.EnumerateArray());

        var publicList = await World.Anonymous().GetAsync($"/api/v1/clinics/{Clinic.Id}/holidays?from={date:yyyy-MM-dd}&to={date:yyyy-MM-dd}", "en");
        Assert.Equal("Test Holiday", Assert.Single(publicList.Body.EnumerateArray()).GetProperty("name").GetString());

        Assert.Equal(204, (await Staff.DeleteAsync($"/api/v1/dashboard/holidays/{created.Id}")).StatusCode);
        Assert.Empty((await Staff.GetAsync($"/api/v1/dashboard/holidays?clinicId={Clinic.Id}&year={date.Year}")).Body.EnumerateArray());
    }

    [Fact]
    public async Task Staff_of_another_clinic_cannot_manage_holidays()
    {
        Enable(FeatureKeys.HolidayCalendar);
        var other = await World.NewClinicAsync("Other");
        var response = await Staff.PostAsync("/api/v1/dashboard/holidays", new { clinicId = other.Id, date = "2027-03-01", nameAr = "a", nameEn = "b" });
        Assert.True(response.StatusCode is 403 or 404);
    }

    [Fact]
    public async Task Import_egypt_creates_the_table_once_then_skips()
    {
        Enable(FeatureKeys.HolidayCalendar);

        var first = await Staff.PostAsync("/api/v1/dashboard/holidays/import-egypt", new { clinicId = Clinic.Id, year = 2027 });
        var second = await Staff.PostAsync("/api/v1/dashboard/holidays/import-egypt", new { clinicId = Clinic.Id, year = 2027 });

        Assert.Equal(200, first.StatusCode);
        Assert.True(first["created"].GetInt32() >= 12);
        Assert.Equal(0, first["skipped"].GetInt32());
        Assert.Equal(0, second["created"].GetInt32());
        Assert.Equal(first["created"].GetInt32(), second["skipped"].GetInt32());
    }

    [Fact]
    public async Task Holiday_blocks_booking_and_rescheduling_and_hides_slots()
    {
        var patient = await World.NewPatientAsync();
        var slot = await LocalSlotAsync(3, 10);
        var other = await LocalSlotAsync(5, 10);
        var booked = await World.BookAsync(patient, await LocalSlotAsync(6, 10));
        var date = ClinicTime.LocalDate(slot.StartsAt, Cairo);

        // Off: the holiday table is ignored even when rows exist.
        Enable(FeatureKeys.HolidayCalendar);
        Assert.Equal(201, (await AddHolidayAsync(date)).StatusCode);
        Disable(FeatureKeys.HolidayCalendar);
        var free = await World.NewPatientAsync();
        Assert.Equal(201, (await free.PostAsync("/api/v1/appointments", new { slotId = slot.Id })).StatusCode);
        Assert.Equal(200, (await free.PostAsync($"{Patient((await World.WithDbAsync(d => d.Appointments.AsNoTracking().Where(a => a.SlotId == slot.Id).Select(a => a.Id).FirstAsync())))}/cancel")).StatusCode);

        Enable(FeatureKeys.HolidayCalendar);
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id });
        Assert.Equal(409, response.StatusCode);
        Assert.Equal("CLINIC_CLOSED", response.Code);

        var move = await patient.PostAsync($"{Patient(booked)}/reschedule", new { slotId = slot.Id });
        Assert.Equal("CLINIC_CLOSED", move.Code);

        var slots = await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}/slots");
        var days = slots["days"].EnumerateArray().Select(d => d.GetProperty("date").GetString()).ToList();
        Assert.DoesNotContain(date.ToString("yyyy-MM-dd"), days);
        Assert.Contains(ClinicTime.LocalDate(other.StartsAt, Cairo).ToString("yyyy-MM-dd"), days);
        Assert.Equal(date.ToString("yyyy-MM-dd"), Assert.Single(slots["closedDates"].EnumerateArray()).GetProperty("date").GetString());
    }

    [Fact]
    public async Task Conflicts_lists_existing_appointments_on_the_date()
    {
        Enable(FeatureKeys.HolidayCalendar);
        var slot = await LocalSlotAsync(4, 11);
        var id = await World.BookAsync(await World.NewPatientAsync(), slot);
        var holiday = await AddHolidayAsync(ClinicTime.LocalDate(slot.StartsAt, Cairo));

        var conflicts = await Staff.GetAsync($"/api/v1/dashboard/holidays/{holiday.Id}/conflicts");

        Assert.Equal(200, conflicts.StatusCode);
        Assert.Equal(id, Assert.Single(conflicts.Body.EnumerateArray()).GetProperty("id").GetGuid());
        // existing appointments stay untouched
        Assert.Equal("Confirmed", (await AppointmentAsync(id)).Status.ToString());
    }

    [Fact]
    public async Task Bulk_creation_skips_holiday_dates_and_template_apply_reuses_the_generator()
    {
        Enable(FeatureKeys.HolidayCalendar, FeatureKeys.WorkingHoursTemplates);
        var start = Today(Now).AddDays(7);
        while (start.DayOfWeek != DayOfWeek.Monday)
        {
            start = start.AddDays(1);
        }

        await AddHolidayAsync(start);
        var template = await Staff.PostAsync("/api/v1/dashboard/working-hours-templates", new
        {
            name = "Mornings",
            slotMinutes = 30,
            rules = new[] { new { dayOfWeek = "Monday", start = "09:00", end = "11:00" }, new { dayOfWeek = "Tuesday", start = "09:00", end = "10:00" } },
        });
        Assert.Equal(201, template.StatusCode);
        Assert.Equal(2, template["rules"].GetArrayLength());

        var applied = await Staff.PostAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/slots/apply-template", new { templateId = template.Id, fromDate = start.ToString("yyyy-MM-dd"), toDate = start.AddDays(1).ToString("yyyy-MM-dd") });

        Assert.Equal(200, applied.StatusCode);
        Assert.Equal(2, applied["created"].GetInt32()); // Tuesday only
        Assert.Equal(4, applied["skipped"].GetInt32()); // Monday: 4 slots on the holiday
        Assert.All(applied["skippedSlots"].EnumerateArray(), s => Assert.Equal("holiday", s.GetProperty("reason").GetString()));

        var again = await Staff.PostAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/slots/apply-template", new { templateId = template.Id, fromDate = start.ToString("yyyy-MM-dd"), toDate = start.AddDays(1).ToString("yyyy-MM-dd") });
        Assert.Equal(0, again["created"].GetInt32());
        Assert.Contains(again["skippedSlots"].EnumerateArray(), s => s.GetProperty("reason").GetString() != "holiday");

        var bulk = await Staff.PostAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/slots/bulk", new { fromDate = start.ToString("yyyy-MM-dd"), toDate = start.ToString("yyyy-MM-dd"), daysOfWeek = new[] { "Monday" }, startTime = "13:00", endTime = "14:00", slotMinutes = 30 });
        Assert.Equal(0, bulk["created"].GetInt32());
        Assert.All(bulk["skippedSlots"].EnumerateArray(), s => Assert.Equal("holiday", s.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task Template_validation_and_crud()
    {
        Enable(FeatureKeys.WorkingHoursTemplates);

        var bad = await Staff.PostAsync("/api/v1/dashboard/working-hours-templates", new { name = "x", slotMinutes = 30, rules = new[] { new { dayOfWeek = "Monday", start = "12:00", end = "09:00" } } });
        Assert.Equal(400, bad.StatusCode);

        var created = await Staff.PostAsync("/api/v1/dashboard/working-hours-templates", new { name = "A", slotMinutes = 30, rules = new[] { new { dayOfWeek = "Monday", start = "09:00", end = "12:00" } } });
        var updated = await Staff.PutAsync($"/api/v1/dashboard/working-hours-templates/{created.Id}", new { name = "B", slotMinutes = 45, rules = new[] { new { dayOfWeek = "Sunday", start = "09:00", end = "12:00" } } });
        Assert.Equal("B", updated["name"].GetString());
        Assert.Single((await Staff.GetAsync("/api/v1/dashboard/working-hours-templates")).Body.EnumerateArray());
        Assert.Equal(204, (await Staff.DeleteAsync($"/api/v1/dashboard/working-hours-templates/{created.Id}")).StatusCode);
        Assert.Empty((await Staff.GetAsync("/api/v1/dashboard/working-hours-templates")).Body.EnumerateArray());
    }
}
