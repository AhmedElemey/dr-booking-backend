using Moonlight.Api.Domain;

namespace Moonlight.Tests;

public class ConcurrencyTests(PostgresFixture db) : AppointmentTestBase(db)
{
    [Fact]
    public async Task Twenty_parallel_bookings_of_one_slot_yield_exactly_one_success()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        var patients = new List<Session>();
        for (var i = 0; i < 20; i++)
        {
            patients.Add(await World.NewPatientAsync());
        }

        var results = await Task.WhenAll(patients.Select(p => Task.Run(() => p.PostAsync("/api/v1/appointments", new { slotId = slot.Id }))));

        Assert.Equal(1, results.Count(r => r.StatusCode == 201));
        var losers = results.Where(r => r.StatusCode != 201).ToList();
        Assert.Equal(19, losers.Count);
        Assert.All(losers, r =>
        {
            Assert.Equal(409, r.StatusCode);
            Assert.Equal("SLOT_TAKEN", r.Code);
        });
        var rows = await World.AppointmentsOfSlotAsync(slot.Id);
        Assert.Equal(1, rows.Count(a => a.Status != AppointmentStatus.Cancelled));
        Assert.Single(rows);
        Assert.Equal(SlotStatus.Reserved, await World.SlotStatusAsync(slot.Id));
    }

    [Fact]
    public async Task Twenty_parallel_bookings_by_the_same_patient_still_yield_one_success()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        var patient = await World.NewPatientAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id }))));

        Assert.Equal(1, results.Count(r => r.StatusCode == 201));
        Assert.All(results.Where(r => r.StatusCode != 201), r => Assert.Equal("SLOT_TAKEN", r.Code));
        Assert.Single(await World.AppointmentsOfSlotAsync(slot.Id));
    }

    [Fact]
    public async Task One_patient_cannot_exceed_the_limit_by_booking_different_slots_in_parallel()
    {
        var slots = new List<Slot>();
        for (var day = 2; day < 10; day++)
        {
            slots.Add(await LocalSlotAsync(day, 10));
        }

        var patient = await World.NewPatientAsync();
        var results = await Task.WhenAll(slots.Select(s => Task.Run(() => patient.PostAsync("/api/v1/appointments", new { slotId = s.Id }))));

        Assert.Equal(3, results.Count(r => r.StatusCode == 201));
        Assert.All(results.Where(r => r.StatusCode != 201), r => Assert.Equal("BOOKING_LIMIT", r.Code));
    }

    [Fact]
    public async Task Parallel_booking_and_staff_booking_of_the_same_slot_still_yield_one_success()
    {
        var slot = await SlotAsync(TimeSpan.FromDays(1));
        var patient = await World.NewPatientAsync();

        var results = await Task.WhenAll(
            Task.Run(() => patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id })),
            Task.Run(() => Staff.PostAsync("/api/v1/dashboard/appointments", new { slotId = slot.Id, patientPhone = TestWorld.NewPhone(), patientName = "Walk-in" })));

        Assert.Equal(1, results.Count(r => r.StatusCode == 201));
        Assert.Equal("SLOT_TAKEN", Assert.Single(results, r => r.StatusCode != 201).Code);
    }
}
