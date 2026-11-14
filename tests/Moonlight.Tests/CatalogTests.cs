using Moonlight.Api.Domain;

namespace Moonlight.Tests;

// Read-only catalog data is seeded once for the whole class.
public sealed class CatalogFixture : IAsyncLifetime
{
    private readonly PostgresFixture _db = new();

    public MoonlightApiFactory Factory { get; private set; } = null!;

    public TestWorld World { get; private set; } = null!;

    public Clinic Cairo { get; private set; } = null!;

    public Doctor Amr { get; private set; } = null!;

    public Doctor Sara { get; private set; } = null!;

    public Doctor Hidden { get; private set; } = null!;

    public Doctor InactiveClinicDoctor { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        Factory = new MoonlightApiFactory(_db.ConnectionString);
        World = new TestWorld(Factory);
        Cairo = await World.NewClinicAsync("Cairo Clinic");
        var alex = await World.NewClinicAsync("Alex Clinic", city: "Alexandria");
        var closed = await World.NewClinicAsync("Closed Clinic", active: false, city: "Luxor");
        Amr = await World.NewDoctorAsync(Cairo, "Amr Hassan");
        Sara = await World.NewDoctorAsync(alex, "Sara Mahmoud", specialtyId: ReferenceData.OrthodonticsId);
        Hidden = await World.NewDoctorAsync(Cairo, "Unpublished Doc", published: false);
        InactiveClinicDoctor = await World.NewDoctorAsync(closed, "Closed Doc");
    }

    public async Task DisposeAsync()
    {
        await Factory.DisposeAsync();
        await _db.DisposeAsync();
    }
}

public class CatalogTests(CatalogFixture fixture) : IClassFixture<CatalogFixture>
{
    private MoonlightApiFactory _factory => fixture.Factory;
    private TestWorld _world => fixture.World;
    private Doctor _amr => fixture.Amr;
    private Doctor _sara => fixture.Sara;
    private Doctor _hidden => fixture.Hidden;
    private Doctor _inactiveClinicDoctor => fixture.InactiveClinicDoctor;

    [Fact]
    public async Task Specialties_are_seeded_and_localized()
    {
        var anon = _world.Anonymous();
        var en = await anon.GetAsync("/api/v1/specialties", "en");
        var ar = await anon.GetAsync("/api/v1/specialties", "ar");

        Assert.Equal(6, en.Body.GetArrayLength());
        Assert.Contains(en.Body.EnumerateArray(), s => s.GetProperty("name").GetString() == "Orthodontics");
        Assert.Contains(ar.Body.EnumerateArray(), s => s.GetProperty("name").GetString() == "تقويم الأسنان");
    }

    [Fact]
    public async Task Cities_list_only_active_clinics_with_published_doctors()
    {
        var cities = (await _world.Anonymous().GetAsync("/api/v1/cities")).Body.EnumerateArray().Select(c => c.GetString()).ToList();

        Assert.Equal(["Alexandria", "Cairo"], cities);
    }

    [Fact]
    public async Task Doctor_search_lists_only_published_doctors_of_active_clinics_with_a_paging_envelope()
    {
        var response = await _world.Anonymous().GetAsync("/api/v1/doctors?pageSize=10", "en");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(1, response["page"].GetInt32());
        Assert.Equal(10, response["pageSize"].GetInt32());
        Assert.Equal(2, response["totalCount"].GetInt32());
        Assert.Equal(["Amr Hassan", "Sara Mahmoud"], response["items"].EnumerateArray().Select(i => i.GetProperty("name").GetString()));
    }

    [Fact]
    public async Task Doctor_search_filters_by_specialty_city_and_name_and_pages()
    {
        var anon = _world.Anonymous();

        var bySpecialty = await anon.GetAsync($"/api/v1/doctors?specialtyId={ReferenceData.OrthodonticsId}");
        Assert.Equal(_sara.Id, Assert.Single(bySpecialty["items"].EnumerateArray()).GetProperty("id").GetGuid());

        var byCity = await anon.GetAsync("/api/v1/doctors?city=cairo");
        Assert.Equal(_amr.Id, Assert.Single(byCity["items"].EnumerateArray()).GetProperty("id").GetGuid());

        var byName = await anon.GetAsync("/api/v1/doctors?q=mahm");
        Assert.Equal(_sara.Id, Assert.Single(byName["items"].EnumerateArray()).GetProperty("id").GetGuid());
        Assert.Equal(0, (await anon.GetAsync("/api/v1/doctors?q=%25")).Body.GetProperty("totalCount").GetInt32());

        var page2 = await anon.GetAsync("/api/v1/doctors?pageSize=1&page=2", "en");
        Assert.Equal(2, page2["totalCount"].GetInt32());
        Assert.Equal(_sara.Id, Assert.Single(page2["items"].EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Theory]
    [InlineData("page=0")]
    [InlineData("pageSize=0")]
    [InlineData("pageSize=101")]
    public async Task Invalid_paging_is_a_validation_error(string query)
    {
        var response = await _world.Anonymous().GetAsync("/api/v1/doctors?" + query);

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", response.Code);
    }

    [Fact]
    public async Task Doctor_profile_by_id_or_slug_includes_clinic_and_only_active_services()
    {
        await _world.NewServiceAsync(_amr);
        await _world.NewServiceAsync(_amr, active: false);
        var anon = _world.Anonymous();

        var byId = await anon.GetAsync($"/api/v1/doctors/{_amr.Id}", "en");
        var bySlug = await anon.GetAsync($"/api/v1/doctors/{_amr.Slug}", "en");

        Assert.Equal(200, byId.StatusCode);
        Assert.Equal(_amr.Id, bySlug["id"].GetGuid());
        Assert.Equal("Amr Hassan", byId["name"].GetString());
        Assert.Equal("Cairo Clinic", byId["clinic"].GetProperty("name").GetString());
        Assert.Equal("Africa/Cairo", byId["clinic"].GetProperty("timezone").GetString());
        Assert.Equal(1, byId["services"].GetArrayLength());
        Assert.Equal("Amr Hassan ar", (await anon.GetAsync($"/api/v1/doctors/{_amr.Id}", "ar"))["name"].GetString());
    }

    [Fact]
    public async Task Unpublished_doctors_and_doctors_of_inactive_clinics_are_not_found()
    {
        var anon = _world.Anonymous();

        Assert.Equal("NOT_FOUND", (await anon.GetAsync($"/api/v1/doctors/{_hidden.Id}")).Code);
        Assert.Equal(404, (await anon.GetAsync($"/api/v1/doctors/{_inactiveClinicDoctor.Slug}")).StatusCode);
        Assert.Equal(404, (await anon.GetAsync($"/api/v1/doctors/{_hidden.Id}/slots")).StatusCode);
        Assert.Equal(404, (await anon.GetAsync("/api/v1/doctors/no-such-doctor")).StatusCode);
    }

    [Fact]
    public async Task Public_slots_return_only_open_slots_outside_the_lead_time_grouped_by_clinic_day()
    {
        var tooSoon = await _world.NewSlotAsync(_amr, TimeSpan.FromMinutes(30));
        var open = await _world.NewSlotAsync(_amr, TimeSpan.FromHours(3));
        await _world.NewSlotAsync(_amr, TimeSpan.FromHours(4), status: SlotStatus.Reserved);
        await _world.NewSlotAsync(_amr, TimeSpan.FromHours(5), status: SlotStatus.Blocked);
        var tomorrow = await _world.NewSlotAsync(_amr, TimeSpan.FromHours(30));
        await _world.NewSlotAsync(_hidden, TimeSpan.FromHours(3));

        var response = await _world.Anonymous().GetAsync($"/api/v1/doctors/{_amr.Slug}/slots?from={DateOnly.FromDateTime(_factory.Clock.UtcNow.AddDays(-1)):yyyy-MM-dd}&to={DateOnly.FromDateTime(_factory.Clock.UtcNow.AddDays(3)):yyyy-MM-dd}");

        Assert.Equal(200, response.StatusCode);
        Assert.Equal(_amr.Id, response["doctorId"].GetGuid());
        Assert.Equal("Africa/Cairo", response["timezone"].GetString());
        var ids = response["days"].EnumerateArray().SelectMany(d => d.GetProperty("slots").EnumerateArray()).Select(s => s.GetProperty("id").GetGuid()).ToList();
        Assert.Contains(open.Id, ids);
        Assert.Contains(tomorrow.Id, ids);
        Assert.DoesNotContain(tooSoon.Id, ids);
        Assert.Equal(2, ids.Count);

        var first = response["days"][0].GetProperty("slots")[0];
        var zone = ClinicTime.FindZone("Africa/Cairo");
        Assert.Equal(ClinicTime.ToLocal(first.GetProperty("startsAt").GetDateTime(), zone).ToString("HH:mm"), first.GetProperty("local").GetString());
    }

    [Fact]
    public async Task Public_slot_range_is_limited_to_14_days()
    {
        var anon = _world.Anonymous();

        Assert.Equal("VALIDATION_FAILED", (await anon.GetAsync($"/api/v1/doctors/{_amr.Id}/slots?from=2026-11-01&to=2026-11-20")).Code);
        Assert.Equal(200, (await anon.GetAsync($"/api/v1/doctors/{_amr.Id}/slots?from=2026-11-01&to=2026-11-14")).StatusCode);
        Assert.Equal(400, (await anon.GetAsync($"/api/v1/doctors/{_amr.Id}/slots?from=garbage")).StatusCode);
    }
}
