using System.Text.Json;

namespace Moonlight.Tests;

// Owns its database: these tests add doctors, so they must not share the read-only catalog fixture.
public sealed class PriceVisibilityFixture : IAsyncLifetime
{
    private readonly PostgresFixture _db = new();

    public MoonlightApiFactory Factory { get; private set; } = null!;

    public TestWorld World { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        Factory = new MoonlightApiFactory(_db.ConnectionString);
        World = new TestWorld(Factory);
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
    }
}

public class PriceVisibilityTests(PriceVisibilityFixture fixture) : IClassFixture<PriceVisibilityFixture>
{
    private TestWorld _world => fixture.World;

    [Fact]
    public async Task Prices_visible_by_default()
    {
        var clinic = await _world.NewClinicAsync("Priced Clinic");
        var doctor = await _world.NewDoctorAsync(clinic, "Hany Samy");
        await _world.NewServiceAsync(doctor);
        await _world.WithDbAsync(async db =>
        {
            (await db.Doctors.FindAsync(doctor.Id))!.ConsultationFeeEgp = 250m;
            return await db.SaveChangesAsync();
        });
        var anon = _world.Anonymous();

        var list = await anon.GetAsync("/api/v1/doctors?q=hany", "en");
        Assert.Equal(250m, Assert.Single(list["items"].EnumerateArray()).GetProperty("consultationFeeEgp").GetDecimal());

        var profile = await anon.GetAsync($"/api/v1/doctors/{doctor.Slug}", "en");
        Assert.Equal(250m, profile.Body.GetProperty("consultationFeeEgp").GetDecimal());
        Assert.Equal(300m, profile.Body.GetProperty("services")[0].GetProperty("priceEgp").GetDecimal());
    }

    [Fact]
    public async Task Prices_hidden_when_clinic_disables_them()
    {
        var clinic = await _world.NewClinicAsync("Quiet Prices Clinic");
        var doctor = await _world.NewDoctorAsync(clinic, "Nour Adel");
        await _world.NewServiceAsync(doctor);
        await _world.WithDbAsync(async db =>
        {
            (await db.Doctors.FindAsync(doctor.Id))!.ConsultationFeeEgp = 250m;
            (await db.Clinics.FindAsync(clinic.Id))!.ShowPrices = false;
            return await db.SaveChangesAsync();
        });
        var anon = _world.Anonymous();

        var list = await anon.GetAsync("/api/v1/doctors?q=nour", "en");
        Assert.Equal(JsonValueKind.Null, Assert.Single(list["items"].EnumerateArray()).GetProperty("consultationFeeEgp").ValueKind);

        var profile = await anon.GetAsync($"/api/v1/doctors/{doctor.Slug}", "en");
        Assert.Equal(JsonValueKind.Null, profile.Body.GetProperty("consultationFeeEgp").ValueKind);
        Assert.Equal(JsonValueKind.Null, profile.Body.GetProperty("services")[0].GetProperty("priceEgp").ValueKind);
    }
}
