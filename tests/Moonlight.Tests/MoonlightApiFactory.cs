using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Moonlight.Api.Infrastructure.Fcm;
using Moonlight.Api.Infrastructure.Sms;

namespace Moonlight.Tests;

public sealed class MoonlightApiFactory : WebApplicationFactory<Program>
{
    public const string TestJwtKey = "integration-test-signing-key-0123456789abcdef";
    public const string FixedOtp = "123456";

    private readonly string _connectionString;
    private readonly string _environment;
    private readonly Dictionary<string, string?> _settings;
    private readonly Action<IServiceCollection>? _configureServices;

    public MoonlightApiFactory(
        string connectionString,
        string environment = "Testing",
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? configureServices = null)
    {
        _configureServices = configureServices;
        _connectionString = connectionString;
        _environment = environment;
        _settings = settings ?? [];
    }

    // Deterministic, test-controlled clock. Starts at the real current time so JWT validation (real clock) stays in step.
    public TestClock Clock { get; } = new();

    public RecordingPushSender Push { get; } = new();

    public RecordingSmsSender Sms { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        builder.UseSetting("ConnectionStrings:Default", _connectionString);
        builder.UseSetting("Jwt:Key", TestJwtKey);
        builder.UseSetting("Reminders:Enabled", "false");
        builder.UseSetting("Jobs:Enabled", "false");
        builder.UseSetting("Payments:AllowMock", "true");
        builder.UseSetting("WhatsApp:AllowMock", "true");
        builder.UseSetting("RateLimits:OtpPerPhonePerMinute", "1000");
        builder.UseSetting("RateLimits:OtpPerPhonePerHour", "1000");
        builder.UseSetting("RateLimits:OtpPerIpPerHour", "100000");
        builder.UseSetting("Storage:Path", Path.Combine(Path.GetTempPath(), "moonlight-test-uploads"));
        builder.UseSetting("Storage:RecordsPath", Path.Combine(Path.GetTempPath(), "moonlight-test-records"));
        if (_environment != "Production")
        {
            builder.UseSetting("Auth:FixedOtp", FixedOtp);
        }

        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            services.RemoveAll<IPushSender>();
            services.AddSingleton<IPushSender>(Push);
            services.RemoveAll<ISmsSender>();
            services.AddSingleton<ISmsSender>(Sms);
            _configureServices?.Invoke(services);
        });
    }
}

public sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow() => _now;

    public DateTime UtcNow => _now.UtcDateTime;

    public void Advance(TimeSpan by) => _now += by;

    // The next 15-minute boundary at least `from now` away: always a valid slot start.
    public DateTime SlotStart(TimeSpan fromNow)
    {
        var target = _now.UtcDateTime + fromNow;
        var ticks = TimeSpan.FromMinutes(15).Ticks;
        return new DateTime((target.Ticks + ticks - 1) / ticks * ticks, DateTimeKind.Utc);
    }
}

public sealed class RecordingPushSender : IPushSender
{
    private readonly List<(IReadOnlyList<string> Tokens, PushMessage Message)> _sent = [];

    private readonly SemaphoreSlim _signal = new(0);

    public List<string> InvalidTokens { get; } = [];

    public bool ThrowOnSend { get; set; }

    // Completes once `count` more pushes have been sent (no polling, no sleeping).
    public async Task<bool> WaitForSendsAsync(int count, TimeSpan timeout)
    {
        for (var i = 0; i < count; i++)
        {
            if (!await _signal.WaitAsync(timeout))
            {
                return false;
            }
        }

        return true;
    }

    public IReadOnlyList<(IReadOnlyList<string> Tokens, PushMessage Message)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("FCM is down");
        }

        lock (_sent)
        {
            _sent.Add((tokens, message));
        }

        _signal.Release();
        return Task.FromResult<IReadOnlyList<string>>(tokens.Where(InvalidTokens.Contains).ToList());
    }
}

public sealed class RecordingSmsSender : ISmsSender
{
    private readonly List<(string Phone, string Message)> _sent = [];

    public IReadOnlyList<(string Phone, string Message)> Sent
    {
        get
        {
            lock (_sent)
            {
                return _sent.ToList();
            }
        }
    }

    public Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        lock (_sent)
        {
            _sent.Add((phone, message));
        }

        return Task.CompletedTask;
    }
}
