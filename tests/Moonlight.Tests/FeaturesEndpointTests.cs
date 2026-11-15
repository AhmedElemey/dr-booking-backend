using System.Net;
using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public sealed class FeaturesEndpointTests : IAsyncLifetime
{
    private readonly PostgresFixture _db = new();
    private MoonlightApiFactory _plain = null!;

    public async Task InitializeAsync()
    {
        await _db.InitializeAsync();
        _plain = new MoonlightApiFactory(_db.ConnectionString);
    }

    public async Task DisposeAsync()
    {
        await _plain.DisposeAsync();
        await _db.DisposeAsync();
    }

    private static string ShippedConfigPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "clinic.config.json");
            if (File.Exists(candidate) && File.Exists(Path.Combine(dir.FullName, "Moonlight.sln")))
            {
                return candidate;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static readonly Dictionary<string, string?> ConsoleSettingsMap = new()
    {
        ["Console:Url"] = "https://console.test",
        ["Console:ClinicId"] = "clinic-7",
        ["Console:ApiKey"] = "key-123",
    };

    private MoonlightApiFactory WithConsole(FakeConsoleHandler handler) =>
        new(_db.ConnectionString, settings: ConsoleSettingsMap, configureServices: services =>
            services.AddHttpClient(ConsoleSettings.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler));

    private static async Task<Resp> FeaturesAfterFirstPullAsync(MoonlightApiFactory factory)
    {
        var service = factory.Services.GetServices<IHostedService>().OfType<ConsolePullService>().Single();
        await service.FirstPullCompleted.WaitAsync(TimeSpan.FromSeconds(30));
        return await new TestWorld(factory).Anonymous().GetAsync("/api/v1/features");
    }

    [Fact]
    public async Task Features_are_public_cacheable_and_default_to_the_clinic_config()
    {
        using var client = _plain.CreateClient();
        using var response = await client.GetAsync("/api/v1/features");
        var body = await new Session(client).GetAsync("/api/v1/features");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.CacheControl?.MaxAge);
        Assert.True(response.Headers.CacheControl!.Public);
        Assert.Equal("defaults", body["source"].GetString());
        Assert.DoesNotContain(body["updatedAt"].GetString()!, "0001");
        var features = body["features"];
        Assert.Equal(FeatureKeys.All.Count, features.EnumerateObject().Count());
        Assert.True(features.GetProperty("mobileApp").GetBoolean());
        Assert.True(features.GetProperty("guestBrowsing").GetBoolean());
        Assert.False(features.GetProperty("smsNotifications").GetBoolean());
    }

    [Fact]
    public async Task Branding_comes_from_the_clinic_config_and_the_logo_is_served()
    {
        using var client = _plain.CreateClient();
        var branding = await new Session(client).GetAsync("/api/v1/branding");

        var shipped = JsonNode.Parse(File.ReadAllText(ShippedConfigPath()))!;
        Assert.Equal(shipped["slug"]!.GetValue<string>(), branding["slug"].GetString());
        Assert.Equal(shipped["name"]!["en"]!.GetValue<string>(), branding["name"].GetProperty("en").GetString());
        Assert.Equal(shipped["name"]!["ar"]!.GetValue<string>(), branding["name"].GetProperty("ar").GetString());
        Assert.Equal(shipped["tagline"]!["en"]!.GetValue<string>(), branding["tagline"].GetProperty("en").GetString());
        Assert.Equal(shipped["tagline"]!["ar"]!.GetValue<string>(), branding["tagline"].GetProperty("ar").GetString());
        Assert.Equal(shipped["colors"]!["primary"]!.GetValue<string>(), branding["colors"].GetProperty("primary").GetString());
        Assert.Equal(shipped["colors"]!["accent"]!.GetValue<string>(), branding["colors"].GetProperty("accent").GetString());
        Assert.Equal(shipped["supportPhone"]!.GetValue<string>(), branding["supportPhone"].GetString());

        using var logo = await client.GetAsync(branding["logoUrl"].GetString());
        Assert.Equal(HttpStatusCode.OK, logo.StatusCode);
        Assert.Equal("image/svg+xml", logo.Content.Headers.ContentType?.MediaType);
        Assert.Contains("<svg", await logo.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_console_pull_replaces_the_defaults_and_is_saved_in_the_database()
    {
        var handler = new FakeConsoleHandler();
        handler.Ok("""{ "guestBrowsing": false, "smsNotifications": true, "telepathy": true }""", "2026-11-14T17:30:00Z");
        await using var factory = WithConsole(handler);

        var response = await FeaturesAfterFirstPullAsync(factory);

        Assert.Equal("console", response["source"].GetString());
        Assert.Equal(DateTime.Parse("2026-11-14T17:30:00Z").ToUniversalTime(), response["updatedAt"].GetDateTime().ToUniversalTime());
        Assert.False(response["features"].GetProperty("guestBrowsing").GetBoolean());
        Assert.True(response["features"].GetProperty("smsNotifications").GetBoolean());
        Assert.True(response["features"].GetProperty("patientSelfService").GetBoolean());
        Assert.False(response["features"].TryGetProperty("telepathy", out _));
        var request = Assert.Single(handler.Requests);
        Assert.Equal("https://console.test/api/v1/clinics/clinic-7/runtime-config", request.Uri!.ToString());
        Assert.Equal("key-123", request.Key);

        var row = await new TestWorld(factory).WithDbAsync(d => d.FeatureStates.AsNoTracking().SingleAsync());
        Assert.Equal(FeatureState.SingletonId, row.Id);
        Assert.Contains("\"guestBrowsing\": false", row.Features);
    }

    [Fact]
    public async Task After_a_restart_with_the_console_down_the_last_copy_from_the_database_is_served()
    {
        var first = new FakeConsoleHandler();
        first.Ok("""{ "patientSelfService": false }""");
        await using (var factory = WithConsole(first))
        {
            Assert.Equal("console", (await FeaturesAfterFirstPullAsync(factory))["source"].GetString());
        }

        var down = new FakeConsoleHandler();
        down.Respond(HttpStatusCode.ServiceUnavailable, "maintenance");
        await using var restarted = WithConsole(down);
        var response = await FeaturesAfterFirstPullAsync(restarted);

        Assert.Equal("cache", response["source"].GetString());
        Assert.False(response["features"].GetProperty("patientSelfService").GetBoolean());
        Assert.True(response["features"].GetProperty("guestBrowsing").GetBoolean());
        Assert.Single(down.Requests);
    }

    [Fact]
    public async Task With_no_cache_and_the_console_down_the_api_serves_the_defaults()
    {
        await using (var cleanup = new MoonlightApiFactory(_db.ConnectionString))
        {
            await new TestWorld(cleanup).WithDbAsync(d => d.FeatureStates.ExecuteDeleteAsync());
        }

        var down = new FakeConsoleHandler();
        down.Throw(new HttpRequestException("connection refused"));
        await using var factory = WithConsole(down);
        var response = await FeaturesAfterFirstPullAsync(factory);

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("defaults", response["source"].GetString());
        Assert.True(response["features"].GetProperty("guestBrowsing").GetBoolean());
        Assert.Equal(200, (await new TestWorld(factory).Anonymous().GetAsync("/health")).StatusCode);
    }

    [Fact]
    public async Task Without_console_settings_a_stale_cache_is_ignored_and_no_puller_runs()
    {
        var seed = new FakeConsoleHandler();
        seed.Ok("""{ "website": false }""");
        await using (var factory = WithConsole(seed))
        {
            await FeaturesAfterFirstPullAsync(factory);
        }

        Assert.Empty(_plain.Services.GetServices<IHostedService>().OfType<ConsolePullService>());
        var response = await new TestWorld(_plain).Anonymous().GetAsync("/api/v1/features");
        Assert.Equal("defaults", response["source"].GetString());
        Assert.True(response["features"].GetProperty("website").GetBoolean());
    }
}
