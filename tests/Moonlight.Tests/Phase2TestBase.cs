using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

// Phase 2 features are off in the template config: tests switch on what they exercise.
public abstract class Phase2TestBase(PostgresFixture db, Dictionary<string, string?>? settings = null) : AppointmentTestBase(db, settings)
{
    protected void Enable(params string[] keys) => Set(true, keys);

    protected void Disable(params string[] keys) => Set(false, keys);

    private void Set(bool on, string[] keys)
    {
        var service = Factory.Services.GetRequiredService<FeatureService>();
        var features = service.Current.Features.ToDictionary(f => f.Key, f => f.Value);
        foreach (var key in keys)
        {
            features[key] = on;
        }

        service.Apply(features, FeatureSources.Console, DateTime.UtcNow);
    }

    // Books a slot in two hours, lets the visit start and have the staff complete it.
    protected async Task<(Guid Id, Session Patient, Slot Slot)> CompletedVisitAsync(Session? patient = null, Doctor? doctor = null)
    {
        patient ??= await World.NewPatientAsync();
        var slot = await SlotAsync(TimeSpan.FromHours(2), doctor);
        var id = await World.BookAsync(patient, slot);
        Factory.Clock.Advance(TimeSpan.FromHours(3));
        var done = await Staff.PostAsync($"{Dash(id)}/complete");
        Assert.Equal(200, done.StatusCode);
        return (id, patient, slot);
    }

    protected async Task<Guid> NewCouponAsync(string code, string type = "Percent", decimal value = 10, Action<Dictionary<string, object?>>? tweak = null)
    {
        var body = new Dictionary<string, object?>
        {
            ["code"] = code,
            ["titleAr"] = "خصم",
            ["titleEn"] = "Discount",
            ["discountType"] = type,
            ["value"] = value,
            ["validFrom"] = ClinicTime.LocalDate(Now, Cairo).AddDays(-1).ToString("yyyy-MM-dd"),
            ["validTo"] = ClinicTime.LocalDate(Now, Cairo).AddDays(30).ToString("yyyy-MM-dd"),
            ["perPatientLimit"] = 1,
            ["isPublic"] = true,
            ["isActive"] = true,
        };
        tweak?.Invoke(body);
        var response = await Staff.PostAsync("/api/v1/dashboard/coupons", body);
        Assert.Equal(201, response.StatusCode);
        return response.Id;
    }

    protected async Task<FamilyMemberRef> NewMemberAsync(Session owner, string name = "Omar")
    {
        var response = await owner.PostAsync("/api/v1/me/family", new { fullName = name, relation = "Child" });
        Assert.Equal(201, response.StatusCode);
        return new FamilyMemberRef(response.Id);
    }

    protected Task SetAsync(Func<Moonlight.Api.Infrastructure.MoonlightDbContext, Task> action) => World.WithDbAsync(async d =>
    {
        await action(d);
        return 0;
    });

    protected static DateOnly Today(DateTime now) => ClinicTime.LocalDate(now, Cairo);

    protected async Task<DateOnly> LocalDateOfAsync(Slot slot) => await Task.FromResult(ClinicTime.LocalDate(slot.StartsAt, Cairo));
}

public sealed record FamilyMemberRef(Guid Id);
