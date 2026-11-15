using System.Net;
using System.Text;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Moonlight.Api.Common;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public sealed class FakeConsoleHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> _responses = new();

    public List<(Uri? Uri, string? Key)> Requests { get; } = [];

    public void Respond(HttpStatusCode status, string body) =>
        _responses.Enqueue(_ => new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });

    public void Ok(string featuresJson, string? updatedAt = "2026-11-14T17:00:00Z") =>
        Respond(HttpStatusCode.OK, $$"""{ "features": {{featuresJson}}{{(updatedAt is null ? "" : $", \"updatedAt\": \"{updatedAt}\"")}} }""");

    public void Throw(Exception ex) => _responses.Enqueue(_ => throw ex);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add((request.RequestUri, request.Headers.TryGetValues("X-Clinic-Key", out var keys) ? keys.Single() : null));
        if (_responses.Count == 0)
        {
            throw new HttpRequestException("console unreachable");
        }

        return Task.FromResult(_responses.Dequeue()(request));
    }
}

public sealed class FakeHttpClientFactory(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
}

public sealed class InMemoryFeatureStore : IFeatureStateStore
{
    public StoredFeatures? Stored { get; set; }

    public bool ThrowOnSave { get; set; }

    public bool ThrowOnLoad { get; set; }

    public Task<StoredFeatures?> LoadAsync(CancellationToken ct) =>
        ThrowOnLoad ? throw new InvalidOperationException("db down") : Task.FromResult(Stored);

    public Task SaveAsync(IReadOnlyDictionary<string, bool> features, DateTime consoleUpdatedAt, DateTime fetchedAt, CancellationToken ct)
    {
        if (ThrowOnSave)
        {
            throw new InvalidOperationException("db down");
        }

        Stored = new StoredFeatures(features, consoleUpdatedAt);
        return Task.CompletedTask;
    }
}

public sealed class FeatureServiceTests
{
    private static readonly ClinicConfig Config = ClinicConfigLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "clinic.config.json"));

    private static FeatureService NewService() => new(Config, TimeProvider.System);

    [Fact]
    public void Starts_with_the_clinic_config_defaults()
    {
        var service = NewService();

        Assert.Equal(FeatureSources.Defaults, service.Current.Source);
        Assert.Equal(FeatureKeys.All.Count, service.Current.Features.Count);
        Assert.True(service.IsEnabled(FeatureKeys.GuestBrowsing));
        Assert.False(service.IsEnabled(FeatureKeys.SmsNotifications));
    }

    [Fact]
    public void Apply_ignores_unknown_keys_and_falls_back_for_missing_ones()
    {
        var service = NewService();
        var at = new DateTime(2026, 11, 14, 17, 0, 0, DateTimeKind.Utc);

        service.Apply(new Dictionary<string, bool> { [FeatureKeys.MultiBranch] = false, ["telepathy"] = true }, FeatureSources.Console, at);

        Assert.False(service.IsEnabled(FeatureKeys.MultiBranch));
        Assert.True(service.IsEnabled(FeatureKeys.PatientSelfService));
        Assert.False(service.Current.Features.ContainsKey("telepathy"));
        Assert.Equal(FeatureSources.Console, service.Current.Source);
        Assert.Equal(at, service.Current.UpdatedAt);
    }

    [Fact]
    public void Require_throws_a_feature_disabled_problem_naming_the_feature()
    {
        var service = NewService();
        service.Require(FeatureKeys.PatientSelfService);

        service.Apply(new Dictionary<string, bool> { [FeatureKeys.PatientSelfService] = false }, FeatureSources.Console, DateTime.UtcNow);
        var ex = Assert.Throws<ApiException>(() => service.Require(FeatureKeys.PatientSelfService));

        Assert.Equal("FEATURE_DISABLED", ex.Code);
        Assert.Equal(FeatureKeys.PatientSelfService, ex.Extensions!["feature"]);
        Assert.Equal(403, ErrorCodes.StatusOf(ex.Code));
    }

    [Fact]
    public void The_problem_title_is_localized()
    {
        Assert.Equal("Feature unavailable", ErrorCodes.TitleOf(ErrorCodes.FeatureDisabled, "en"));
        Assert.Equal("الخدمة غير متاحة", ErrorCodes.TitleOf(ErrorCodes.FeatureDisabled, "ar"));
    }
}

public sealed class ConsolePullerTests
{
    private static readonly ClinicConfig Config = ClinicConfigLoader.LoadFile(Path.Combine(AppContext.BaseDirectory, "clinic.config.json"));
    private static readonly ConsoleSettings Settings = new("https://console.example/", "clinic 1", "secret-key", 300);

    private readonly FakeConsoleHandler _handler = new();
    private readonly InMemoryFeatureStore _store = new();
    private readonly FeatureService _features = new(Config, TimeProvider.System);

    private ConsolePuller Puller(ConsoleSettings? settings = null) =>
        new(new FakeHttpClientFactory(_handler), settings ?? Settings, _features, _store, TimeProvider.System, NullLogger<ConsolePuller>.Instance);

    [Fact]
    public async Task A_successful_pull_applies_persists_and_sends_the_clinic_key()
    {
        _handler.Ok("""{ "guestBrowsing": false, "smsNotifications": true }""");

        Assert.True(await Puller().PullOnceAsync(default));

        var request = Assert.Single(_handler.Requests);
        Assert.Equal("https://console.example/api/v1/clinics/clinic%201/runtime-config", request.Uri!.OriginalString);
        Assert.Equal("secret-key", request.Key);
        Assert.False(_features.IsEnabled(FeatureKeys.GuestBrowsing));
        Assert.True(_features.IsEnabled(FeatureKeys.SmsNotifications));
        Assert.True(_features.IsEnabled(FeatureKeys.PushReminders));
        Assert.Equal(FeatureSources.Console, _features.Current.Source);
        Assert.Equal(new DateTime(2026, 11, 14, 17, 0, 0, DateTimeKind.Utc), _features.Current.UpdatedAt);
        Assert.False(_store.Stored!.Features[FeatureKeys.GuestBrowsing]);
        Assert.Equal(FeatureKeys.All.Count, _store.Stored.Features.Count);
    }

    [Fact]
    public async Task Unknown_keys_and_non_boolean_values_are_ignored()
    {
        _handler.Ok("""{ "telepathy": true, "website": "yes", "multiBranch": false }""");

        Assert.True(await Puller().PullOnceAsync(default));

        Assert.False(_features.IsEnabled(FeatureKeys.MultiBranch));
        Assert.True(_features.IsEnabled(FeatureKeys.Website));
        Assert.DoesNotContain("telepathy", _store.Stored!.Features.Keys);
    }

    [Fact]
    public async Task A_missing_updatedAt_falls_back_to_now()
    {
        _handler.Ok("{}", updatedAt: null);
        var before = DateTime.UtcNow.AddSeconds(-1);

        Assert.True(await Puller().PullOnceAsync(default));

        Assert.True(_features.Current.UpdatedAt >= before);
    }

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, "oops")]
    [InlineData(HttpStatusCode.Unauthorized, """{"error":"bad key"}""")]
    [InlineData(HttpStatusCode.OK, "not json")]
    [InlineData(HttpStatusCode.OK, """{ "updatedAt": "2026-11-14T17:00:00Z" }""")]
    [InlineData(HttpStatusCode.OK, """{ "features": [] }""")]
    public async Task A_failed_pull_keeps_the_previous_switches_and_never_throws(HttpStatusCode status, string body)
    {
        _handler.Ok("""{ "guestBrowsing": false }""");
        var puller = Puller();
        Assert.True(await puller.PullOnceAsync(default));

        _handler.Respond(status, body);
        Assert.False(await puller.PullOnceAsync(default));

        Assert.False(_features.IsEnabled(FeatureKeys.GuestBrowsing));
        Assert.Equal(FeatureSources.Console, _features.Current.Source);
    }

    [Fact]
    public async Task An_unreachable_console_keeps_the_defaults()
    {
        _handler.Throw(new HttpRequestException("connection refused"));

        Assert.False(await Puller().PullOnceAsync(default));
        Assert.Equal(FeatureSources.Defaults, _features.Current.Source);
        Assert.Null(_store.Stored);

        // Nothing queued: the fake handler fails like a dead host.
        Assert.False(await Puller().PullOnceAsync(default));
    }

    [Fact]
    public async Task A_database_failure_while_saving_does_not_lose_the_pulled_switches()
    {
        _store.ThrowOnSave = true;
        _handler.Ok("""{ "website": false }""");

        Assert.True(await Puller().PullOnceAsync(default));

        Assert.False(_features.IsEnabled(FeatureKeys.Website));
    }

    [Fact]
    public async Task The_cached_copy_is_used_when_the_console_is_down_after_a_restart()
    {
        _store.Stored = new StoredFeatures(
            new Dictionary<string, bool> { [FeatureKeys.PatientSelfService] = false },
            new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc));
        var puller = Puller();

        await puller.LoadCacheAsync(default);
        Assert.False(await puller.PullOnceAsync(default));

        Assert.Equal(FeatureSources.Cache, _features.Current.Source);
        Assert.False(_features.IsEnabled(FeatureKeys.PatientSelfService));
        Assert.True(_features.IsEnabled(FeatureKeys.GuestBrowsing));
        Assert.Equal(new DateTime(2026, 11, 1, 8, 0, 0, DateTimeKind.Utc), _features.Current.UpdatedAt);
    }

    [Fact]
    public async Task A_broken_cache_read_falls_back_to_the_defaults()
    {
        _store.ThrowOnLoad = true;

        await Puller().LoadCacheAsync(default);

        Assert.Equal(FeatureSources.Defaults, _features.Current.Source);
    }

    [Fact]
    public async Task Without_settings_nothing_is_requested()
    {
        Assert.False(await Puller(Settings with { ApiKey = "" }).PullOnceAsync(default));
        Assert.Empty(_handler.Requests);
    }

    [Fact]
    public void Settings_resolve_from_configuration_with_clinic_config_defaults()
    {
        static IConfiguration Build(Dictionary<string, string?> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var withConsole = Config with { Console = new ClinicConsoleRef("https://from-file.example", "file-id") };

        var fromFile = ConsoleSettings.Resolve(Build(new() { ["Console:ApiKey"] = "k" }), withConsole);
        Assert.True(fromFile.IsConfigured);
        Assert.Equal("https://from-file.example", fromFile.Url);
        Assert.Equal(300, fromFile.RefreshSeconds);

        var overridden = ConsoleSettings.Resolve(
            Build(new() { ["Console:Url"] = "https://env.example", ["Console:ClinicId"] = "env-id", ["Console:ApiKey"] = "k", ["Console:RefreshSeconds"] = "30" }), withConsole);
        Assert.Equal(("https://env.example", "env-id", 30), (overridden.Url, overridden.ClinicId, overridden.RefreshSeconds));

        Assert.False(ConsoleSettings.Resolve(Build([]), Config).IsConfigured);
        Assert.Throws<InvalidOperationException>(() => ConsoleSettings.Resolve(Build(new() { ["Console:Url"] = "nope" }), Config));
        Assert.Throws<InvalidOperationException>(() => ConsoleSettings.Resolve(Build(new() { ["Console:RefreshSeconds"] = "0" }), Config));
    }
}
