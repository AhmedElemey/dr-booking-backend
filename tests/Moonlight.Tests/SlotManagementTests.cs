using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

using Npgsql;

namespace Moonlight.Tests;

public class SlotManagementTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly MoonlightApiFactory _factory = new(db.ConnectionString);
    private TestWorld _world = null!;
    private Clinic _clinic = null!;
    private Doctor _doctor = null!;
    private Session _staff = null!;
    private readonly TimeZoneInfo _zone = ClinicTime.FindZone("Africa/Cairo");

    public async Task InitializeAsync()
    {
        _world = new TestWorld(_factory);
        _clinic = await _world.NewClinicAsync();
        _doctor = await _world.NewDoctorAsync(_clinic);
        _staff = await _world.LoginAsync(await _world.NewStaffAsync(_clinic));
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private string Slots => $"/api/v1/dashboard/doctors/{_doctor.Id}/slots";

    private DateOnly LocalToday => ClinicTime.LocalDate(_factory.Clock.UtcNow, _zone);

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss'Z'");

    private object Range(DateTime start, int minutes = 30) => new { startsAt = Iso(start), endsAt = Iso(start.AddMinutes(minutes)) };

    [Fact]
    public async Task A_single_slot_can_be_created_and_shows_in_the_calendar()
    {
        var start = _factory.Clock.SlotStart(TimeSpan.FromDays(2));

        var created = await _staff.PostAsync(Slots, Range(start, 45));

        Assert.Equal(201, created.StatusCode);
        Assert.Equal("Open", created["status"].GetString());
        Assert.Equal(_doctor.Id, created["doctorId"].GetGuid());
        var day = ClinicTime.LocalDate(start, _zone);
        var calendar = await _staff.GetAsync($"{Slots}?from={day:yyyy-MM-dd}&to={day:yyyy-MM-dd}");
        Assert.Equal(created.Id, Assert.Single(calendar.Body.EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(_clinic.Id, await _world.WithDbAsync(async d => (await d.Slots.FindAsync(created.Id))!.ClinicId));
    }

    [Fact]
    public async Task Offsets_in_the_request_are_normalized_to_utc()
    {
        var start = _factory.Clock.SlotStart(TimeSpan.FromDays(2));
        var local = ClinicTime.ToLocal(start, _zone);
        var offset = _zone.GetUtcOffset(start);

        var created = await _staff.PostAsync(Slots, new
        {
            startsAt = new DateTimeOffset(local, offset).ToString("o"),
            endsAt = new DateTimeOffset(local.AddMinutes(30), offset).ToString("o"),
        });

        Assert.Equal(201, created.StatusCode);
        Assert.Equal(start, created["startsAt"].GetDateTime().ToUniversalTime());
    }

    [Fact]
    public async Task Overlapping_slots_are_rejected_but_back_to_back_slots_and_other_doctors_are_fine()
    {
        var start = _factory.Clock.SlotStart(TimeSpan.FromDays(2));
        Assert.Equal(201, (await _staff.PostAsync(Slots, Range(start))).StatusCode);

        var overlap = await _staff.PostAsync(Slots, Range(start.AddMinutes(15)));
        Assert.Equal(409, overlap.StatusCode);
        Assert.Equal("SLOT_OVERLAP", overlap.Code);
        Assert.Equal("SLOT_OVERLAP", (await _staff.PostAsync(Slots, Range(start, 60))).Code);
        Assert.Equal("SLOT_OVERLAP", (await _staff.PostAsync(Slots, Range(start.AddMinutes(-15), 60))).Code);

        Assert.Equal(201, (await _staff.PostAsync(Slots, Range(start.AddMinutes(30)))).StatusCode);
        Assert.Equal(201, (await _staff.PostAsync(Slots, Range(start.AddMinutes(-30)))).StatusCode);

        var other = await _world.NewDoctorAsync(_clinic, "Other");
        Assert.Equal(201, (await _staff.PostAsync($"/api/v1/dashboard/doctors/{other.Id}/slots", Range(start))).StatusCode);
    }

    [Fact]
    public async Task The_database_itself_rejects_overlapping_slots()
    {
        var start = _factory.Clock.SlotStart(TimeSpan.FromDays(3));
        await _world.NewSlotAtAsync(_doctor, start);

        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => _world.NewSlotAtAsync(_doctor, start.AddMinutes(15)));

        Assert.True(ex.IsExclusionViolation());
        Assert.Equal("no_overlapping_slots", ((PostgresException)ex.InnerException!).ConstraintName);
        await _world.NewSlotAtAsync(_doctor, start.AddMinutes(30));
    }

    [Fact]
    public async Task The_database_allows_only_one_active_appointment_per_slot()
    {
        var slot = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(1));
        var patient = await _world.NewPatientAsync();

        Task Insert(AppointmentStatus status) => _world.WithDbAsync(async d =>
        {
            d.Appointments.Add(new Appointment
            {
                SlotId = slot.Id,
                PatientUserId = patient.User!.Id,
                CreatedByUserId = patient.User.Id,
                Status = status,
                CreatedAt = _factory.Clock.UtcNow,
                UpdatedAt = _factory.Clock.UtcNow,
            });
            await d.SaveChangesAsync();
            return 0;
        });

        await Insert(AppointmentStatus.Confirmed);
        var ex = await Assert.ThrowsAsync<DbUpdateException>(() => Insert(AppointmentStatus.Confirmed));
        Assert.True(ex.IsUniqueViolation("ux_appointments_active_slot"));
        await Insert(AppointmentStatus.Cancelled);
        await Insert(AppointmentStatus.Cancelled);
    }

    [Theory]
    [InlineData(-60, 30, "startsAt")]
    [InlineData(5, 30, "endsAt")]
    [InlineData(1440, 10, "endsAt")]
    [InlineData(1440, 300, "endsAt")]
    public async Task Invalid_slots_fail_validation(int startMinutesFromNow, int length, string field)
    {
        var start = _factory.Clock.SlotStart(TimeSpan.FromMinutes(startMinutesFromNow));
        var end = field == "endsAt" && startMinutesFromNow == 5 ? start.AddMinutes(30).AddMinutes(7) : start.AddMinutes(length);

        var response = await _staff.PostAsync(Slots, new { startsAt = Iso(start), endsAt = Iso(end) });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", response.Code);
        Assert.True(response["errors"].TryGetProperty(field, out _));
    }

    [Fact]
    public async Task Bulk_creates_the_expected_rows_and_reports_skipped_ones()
    {
        var firstDay = LocalToday.AddDays(3);
        Assert.True(ClinicTime.TryLocalToUtc(firstDay.ToDateTime(new TimeOnly(10, 0)), _zone, out var tenLocal));
        await _world.NewSlotAtAsync(_doctor, tenLocal.AddMinutes(15), 15);

        var result = await _staff.PostAsync($"{Slots}/bulk", new
        {
            fromDate = firstDay.ToString("yyyy-MM-dd"),
            toDate = firstDay.AddDays(1).ToString("yyyy-MM-dd"),
            daysOfWeek = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" },
            startTime = "10:00",
            endTime = "11:00",
            slotMinutes = 30,
        });

        Assert.Equal(200, result.StatusCode);
        Assert.Equal(3, result["created"].GetInt32());
        Assert.Equal(1, result["skipped"].GetInt32());
        Assert.Equal("Overlap", result["skippedSlots"][0].GetProperty("reason").GetString());
        var rows = await _world.WithDbAsync(d => d.Slots.Where(s => s.DoctorId == _doctor.Id).OrderBy(s => s.StartsAt).ToListAsync());
        Assert.Equal(4, rows.Count);
        Assert.All(rows, r => Assert.Equal(SlotStatus.Open, r.Status));
        Assert.All(rows, r => Assert.Equal(_clinic.Id, r.ClinicId));
    }

    [Fact]
    public async Task Bulk_skips_slots_in_the_past()
    {
        var yesterday = LocalToday.AddDays(-1);

        var result = await _staff.PostAsync($"{Slots}/bulk", new
        {
            fromDate = yesterday.ToString("yyyy-MM-dd"),
            toDate = yesterday.ToString("yyyy-MM-dd"),
            daysOfWeek = Enum.GetNames<DayOfWeek>(),
            startTime = "09:00",
            endTime = "10:00",
            slotMinutes = 30,
        });

        Assert.Equal(0, result["created"].GetInt32());
        Assert.Equal(2, result["skipped"].GetInt32());
        Assert.All(result["skippedSlots"].EnumerateArray(), s => Assert.Equal("Past", s.GetProperty("reason").GetString()));
    }

    [Fact]
    public async Task Bulk_rejects_too_many_slots_bad_times_and_missing_fields()
    {
        var from = LocalToday.AddDays(5);
        object Body(string toDate, string end, int minutes) => new
        {
            fromDate = from.ToString("yyyy-MM-dd"),
            toDate,
            daysOfWeek = Enum.GetNames<DayOfWeek>(),
            startTime = "00:00",
            endTime = end,
            slotMinutes = minutes,
        };

        var tooMany = await _staff.PostAsync($"{Slots}/bulk", Body(from.AddDays(25).ToString("yyyy-MM-dd"), "20:00", 15));
        Assert.Equal("VALIDATION_FAILED", tooMany.Code);
        Assert.Equal("VALIDATION_FAILED", (await _staff.PostAsync($"{Slots}/bulk", Body(from.AddDays(1).ToString("yyyy-MM-dd"), "20:00", 20))).Code);
        Assert.Equal("VALIDATION_FAILED", (await _staff.PostAsync($"{Slots}/bulk", Body(from.AddDays(120).ToString("yyyy-MM-dd"), "00:30", 30))).Code);
        Assert.Equal("VALIDATION_FAILED", (await _staff.PostAsync($"{Slots}/bulk", Body(from.ToString("yyyy-MM-dd"), "9am", 30))).Code);
        Assert.Equal("VALIDATION_FAILED", (await _staff.PostAsync($"{Slots}/bulk", new { })).Code);
        Assert.Equal(0, await _world.WithDbAsync(d => d.Slots.CountAsync(s => s.DoctorId == _doctor.Id)));
    }

    [Fact]
    public async Task A_reserved_slot_cannot_be_blocked_or_removed_until_its_appointment_is_cancelled()
    {
        var slot = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(1));
        var patient = await _world.NewPatientAsync();
        var appointmentId = await _world.BookAsync(patient, slot);

        var block = await _staff.PostAsync($"{Slots}/{slot.Id}/block");
        Assert.Equal(409, block.StatusCode);
        Assert.Equal("SLOT_RESERVED", block.Code);
        Assert.Equal("SLOT_RESERVED", (await _staff.DeleteAsync($"{Slots}/{slot.Id}")).Code);

        var day = ClinicTime.LocalDate(slot.StartsAt, _zone).ToString("yyyy-MM-dd");
        var range = await _staff.DeleteAsync($"{Slots}?from={day}&to={day}");
        Assert.Equal(0, range["deleted"].GetInt32());
        Assert.Equal(1, range["skippedReserved"].GetInt32());
        Assert.Equal(SlotStatus.Reserved, await _world.SlotStatusAsync(slot.Id));

        await _staff.PostAsync($"/api/v1/dashboard/appointments/{appointmentId}/cancel", new { reason = "doctor unavailable" });
        Assert.Equal(200, (await _staff.PostAsync($"{Slots}/{slot.Id}/block")).StatusCode);
    }

    [Fact]
    public async Task Blocked_slots_are_hidden_from_patients_until_unblocked()
    {
        await _world.WithDbAsync(async d =>
        {
            (await d.Doctors.FindAsync(_doctor.Id))!.IsPublished = true;
            await d.SaveChangesAsync();
            return 0;
        });
        var slot = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(1));
        var day = ClinicTime.LocalDate(slot.StartsAt, _zone).ToString("yyyy-MM-dd");
        var anon = _world.Anonymous();

        Assert.Equal("Blocked", (await _staff.PostAsync($"{Slots}/{slot.Id}/block"))["status"].GetString());
        Assert.Equal(0, (await anon.GetAsync($"/api/v1/doctors/{_doctor.Id}/slots?from={day}&to={day}"))["days"].GetArrayLength());
        var patient = await _world.NewPatientAsync();
        Assert.Equal("SLOT_TAKEN", (await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id })).Code);

        Assert.Equal("Open", (await _staff.PostAsync($"{Slots}/{slot.Id}/unblock"))["status"].GetString());
        Assert.Equal(1, (await anon.GetAsync($"/api/v1/doctors/{_doctor.Id}/slots?from={day}&to={day}"))["days"].GetArrayLength());
    }

    [Fact]
    public async Task Started_slots_are_read_only()
    {
        var past = await _world.NewSlotAsync(_doctor, TimeSpan.FromHours(-3));

        var response = await _staff.PostAsync($"{Slots}/{past.Id}/block");
        Assert.Equal("SLOT_UNAVAILABLE", response.Code);
        Assert.Equal("SLOT_UNAVAILABLE", (await _staff.DeleteAsync($"{Slots}/{past.Id}")).Code);
        Assert.Equal(404, (await _staff.PostAsync($"{Slots}/{Guid.NewGuid()}/block")).StatusCode);
    }

    [Fact]
    public async Task Single_and_range_deletes_remove_open_and_blocked_slots_only()
    {
        var a = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(4));
        var b = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(4) + TimeSpan.FromHours(1), status: SlotStatus.Blocked);
        var c = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(4) + TimeSpan.FromHours(2));
        var keep = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(9));

        Assert.Equal(204, (await _staff.DeleteAsync($"{Slots}/{a.Id}")).StatusCode);
        var day = ClinicTime.LocalDate(b.StartsAt, _zone);
        var range = await _staff.DeleteAsync($"{Slots}?from={day:yyyy-MM-dd}&to={day.AddDays(1):yyyy-MM-dd}");

        Assert.Equal(2, range["deleted"].GetInt32());
        Assert.Equal(0, range["skippedReserved"].GetInt32());
        var remaining = await _world.WithDbAsync(d => d.Slots.Where(s => s.DoctorId == _doctor.Id).Select(s => s.Id).ToListAsync());
        Assert.Equal([keep.Id], remaining);
        Assert.Equal("VALIDATION_FAILED", (await _staff.DeleteAsync($"{Slots}?from=2026-01-01")).Code);
    }

    [Fact]
    public async Task Calendar_embeds_the_appointment_and_patient_on_reserved_slots_and_filters_by_status()
    {
        var open = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(1));
        var reserved = await _world.NewSlotAsync(_doctor, TimeSpan.FromDays(1) + TimeSpan.FromHours(1));
        var patient = await _world.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { fullName = "Hoda Samir" });
        var appointmentId = await _world.BookAsync(patient, reserved);
        var day = ClinicTime.LocalDate(open.StartsAt, _zone);
        var query = $"from={day:yyyy-MM-dd}&to={day.AddDays(1):yyyy-MM-dd}";

        var calendar = await _staff.GetAsync($"{Slots}?{query}");
        var row = calendar.Body.EnumerateArray().Single(s => s.GetProperty("id").GetGuid() == reserved.Id);
        Assert.Equal("Reserved", row.GetProperty("status").GetString());
        Assert.Equal(appointmentId, row.GetProperty("appointmentId").GetGuid());
        Assert.Equal("Hoda Samir", row.GetProperty("patientName").GetString());
        Assert.Equal(patient.User!.Phone, row.GetProperty("patientPhone").GetString());

        var onlyOpen = await _staff.GetAsync($"{Slots}?{query}&status=Open");
        Assert.Equal([open.Id], onlyOpen.Body.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()));
        Assert.Equal("VALIDATION_FAILED", (await _staff.GetAsync($"{Slots}?from=2026-11-01&to=2026-12-31")).Code);
    }
}
