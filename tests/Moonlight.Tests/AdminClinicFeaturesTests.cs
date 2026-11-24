using System.Text.Json;

using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

// Per-clinic feature overrides and deposit amount, managed by the platform admin.
public class AdminClinicFeaturesTests(PostgresFixture db) : AppointmentTestBase(db)
{
    private Task<Session> AdminAsync() => World.LoginAsync(World.NewAdminAsync().Result);

    private void EnableGlobally(string key)
    {
        var service = Factory.Services.GetRequiredService<FeatureService>();
        var features = service.Current.Features.ToDictionary(f => f.Key, f => f.Value);
        features[key] = true;
        service.Apply(features, FeatureSources.Console, DateTime.UtcNow);
    }

    [Fact]
    public async Task Features_default_to_the_deployment_values_with_no_overrides()
    {
        var admin = await AdminAsync();
        var response = await admin.GetAsync($"/api/v1/admin/clinics/{Clinic.Id}/features");

        Assert.Equal(200, response.StatusCode);
        Assert.False(response["features"].GetProperty("holidayCalendar").GetProperty("hasOverride").GetBoolean());
        Assert.Equal(FeatureKeys.All.Count, response["features"].EnumerateObject().Count());
        Assert.Equal(JsonValueKind.Null, response.Body.GetProperty("depositAmountEgp").ValueKind);
    }

    [Fact]
    public async Task Update_sets_and_clears_an_override_and_the_dashboard_enforces_it_per_clinic()
    {
        EnableGlobally(FeatureKeys.WorkingHoursTemplates);
        Assert.Equal(200, (await Staff.GetAsync("/api/v1/dashboard/working-hours-templates")).StatusCode);

        var admin = await AdminAsync();
        var off = await admin.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new
        {
            features = new Dictionary<string, bool?> { [FeatureKeys.WorkingHoursTemplates] = false },
        });
        Assert.Equal(200, off.StatusCode);
        Assert.False(off["features"].GetProperty("workingHoursTemplates").GetProperty("effective").GetBoolean());
        Assert.True(off["features"].GetProperty("workingHoursTemplates").GetProperty("hasOverride").GetBoolean());

        // This clinic's staff is blocked while other clinics keep the deployment value.
        Assert.Equal(403, (await Staff.GetAsync("/api/v1/dashboard/working-hours-templates")).StatusCode);
        var otherClinic = await World.NewClinicAsync("Other Clinic");
        var otherStaff = await World.LoginAsync(await World.NewStaffAsync(otherClinic));
        Assert.Equal(200, (await otherStaff.GetAsync("/api/v1/dashboard/working-hours-templates")).StatusCode);

        var cleared = await admin.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new
        {
            features = new Dictionary<string, bool?> { [FeatureKeys.WorkingHoursTemplates] = null },
        });
        Assert.Equal(200, cleared.StatusCode);
        Assert.False(cleared["features"].GetProperty("workingHoursTemplates").GetProperty("hasOverride").GetBoolean());
        Assert.Equal(200, (await Staff.GetAsync("/api/v1/dashboard/working-hours-templates")).StatusCode);
    }

    [Fact]
    public async Task Deposit_amount_roundtrips_and_clears()
    {
        var admin = await AdminAsync();
        var set = await admin.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new { depositAmountEgp = 150m });
        Assert.Equal(200, set.StatusCode);
        Assert.Equal(150m, set.Body.GetProperty("depositAmountEgp").GetDecimal());

        var cleared = await admin.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new { clearDepositAmountEgp = true });
        Assert.Equal(200, cleared.StatusCode);
        Assert.Equal(JsonValueKind.Null, cleared.Body.GetProperty("depositAmountEgp").ValueKind);
    }

    [Fact]
    public async Task Unknown_keys_and_negative_deposits_are_validation_errors()
    {
        var admin = await AdminAsync();
        var badKey = await admin.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new
        {
            features = new Dictionary<string, bool?> { ["notAFeature"] = true },
        });
        Assert.Equal(400, badKey.StatusCode);

        var badDeposit = await admin.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new { depositAmountEgp = -5m });
        Assert.Equal(400, badDeposit.StatusCode);
    }

    [Fact]
    public async Task Clinic_staff_cannot_read_or_change_feature_overrides()
    {
        Assert.Equal(403, (await Staff.GetAsync($"/api/v1/admin/clinics/{Clinic.Id}/features")).StatusCode);
        Assert.Equal(403, (await Staff.PutAsync($"/api/v1/admin/clinics/{Clinic.Id}/features", new { })).StatusCode);
    }
}
