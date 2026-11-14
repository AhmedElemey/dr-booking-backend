using System.Net.Http.Json;
using System.Text.Json;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Flags;

/// <summary>Resolved console settings: <c>Console:*</c> overrides, falling back to the <c>console</c> block of clinic.config.json.</summary>
public sealed record ConsoleSettings(string Url, string ClinicId, string ApiKey, int RefreshSeconds)
{
    public const string Section = "Console";
    public const string HttpClientName = "console";
    public const int DefaultRefreshSeconds = 300;

    public bool IsConfigured => Url.Length > 0 && ClinicId.Length > 0 && ApiKey.Length > 0;

    public static ConsoleSettings Resolve(IConfiguration configuration, ClinicConfig clinic)
    {
        static string Pick(string? setting, string? fallback) => string.IsNullOrWhiteSpace(setting) ? fallback?.Trim() ?? "" : setting.Trim();

        var section = configuration.GetSection(Section);
        var settings = new ConsoleSettings(
            Pick(section["Url"], clinic.Console.Url),
            Pick(section["ClinicId"], clinic.Console.ClinicId),
            (section["ApiKey"] ?? "").Trim(),
            section.GetValue("RefreshSeconds", DefaultRefreshSeconds));

        if (settings.RefreshSeconds < 1)
        {
            throw new InvalidOperationException("Console:RefreshSeconds must be at least 1.");
        }

        if (settings.Url.Length > 0 && !(Uri.TryCreate(settings.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            throw new InvalidOperationException("Console:Url must be an absolute http(s) URL.");
        }

        return settings;
    }
}

/// <summary>Pulls the runtime config from the console and keeps the last good copy. Never throws: failures keep the previous state.</summary>
public sealed class ConsolePuller(
    IHttpClientFactory httpClients,
    ConsoleSettings settings,
    FeatureService features,
    IFeatureStateStore store,
    TimeProvider clock,
    ILogger<ConsolePuller> logger)
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Loads the cached copy (if any) so the switches survive a restart while the console is down.</summary>
    public async Task LoadCacheAsync(CancellationToken ct)
    {
        try
        {
            var stored = await store.LoadAsync(ct);
            if (stored is not null)
            {
                features.Apply(stored.Features, FeatureSources.Cache, stored.UpdatedAt);
                logger.LogInformation("Loaded cached feature switches from {UpdatedAt:O}.", stored.UpdatedAt);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not load the cached feature switches; using clinic.config.json defaults.");
        }
    }

    public async Task<bool> PullOnceAsync(CancellationToken ct)
    {
        if (!settings.IsConfigured)
        {
            return false;
        }

        try
        {
            var incoming = await FetchAsync(ct);
            if (incoming is null)
            {
                return false;
            }

            var merged = features.Merge(incoming.Features);
            features.Apply(merged, FeatureSources.Console, incoming.UpdatedAt);
            await PersistAsync(merged, incoming.UpdatedAt, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Console pull failed ({Error}); keeping the current feature switches.", ex.Message);
            return false;
        }
    }

    private async Task<StoredFeatures?> FetchAsync(CancellationToken ct)
    {
        var url = $"{settings.Url.TrimEnd('/')}/api/v1/clinics/{Uri.EscapeDataString(settings.ClinicId)}/runtime-config";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("X-Clinic-Key", settings.ApiKey);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(RequestTimeout);
        using var client = httpClients.CreateClient(ConsoleSettings.HttpClientName);
        using var response = await client.SendAsync(request, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("Console pull failed: HTTP {Status}; keeping the current feature switches.", (int)response.StatusCode);
            return null;
        }

        var body = await response.Content.ReadFromJsonAsync<JsonElement>(timeout.Token);
        if (body.ValueKind != JsonValueKind.Object
            || !body.TryGetProperty("features", out var featureElement)
            || featureElement.ValueKind != JsonValueKind.Object)
        {
            logger.LogWarning("Console pull failed: response has no \"features\" object; keeping the current feature switches.");
            return null;
        }

        var parsed = new Dictionary<string, bool>();
        foreach (var property in featureElement.EnumerateObject())
        {
            // Unknown keys and non-boolean values are ignored; missing keys fall back to the clinic.config.json defaults.
            if (FeatureKeys.All.Contains(property.Name) && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                parsed[property.Name] = property.Value.GetBoolean();
            }
        }

        var updatedAt = body.TryGetProperty("updatedAt", out var updated) && updated.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updated.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsedAt)
                ? parsedAt.UtcDateTime
                : clock.GetUtcNow().UtcDateTime;
        return new StoredFeatures(parsed, updatedAt);
    }

    private async Task PersistAsync(IReadOnlyDictionary<string, bool> merged, DateTime updatedAt, CancellationToken ct)
    {
        try
        {
            await store.SaveAsync(merged, updatedAt, clock.GetUtcNow().UtcDateTime, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not persist the feature switches; they stay in memory until the next successful pull.");
        }
    }
}

/// <summary>Loads the cached copy at startup, then pulls from the console every <c>Console:RefreshSeconds</c>.</summary>
public sealed class ConsolePullService(ConsolePuller puller, ConsoleSettings settings, TimeProvider clock, ILogger<ConsolePullService> logger)
    : BackgroundService
{
    private readonly TaskCompletionSource _firstPull = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes after the first pull attempt (successful or not).</summary>
    public Task FirstPullCompleted => _firstPull.Task;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        // Before the server accepts requests, so the first response already reflects the last known switches.
        await puller.LoadCacheAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(settings.RefreshSeconds), clock);
        do
        {
            try
            {
                await puller.PullOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Console pull crashed; will retry on the next tick.");
            }

            _firstPull.TrySetResult();
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
