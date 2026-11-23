using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Moonlight.Tests;

public sealed class HostedDemoTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    private Dictionary<string, string?> Settings => new()
    {
        ["Demo:SeedSyntheticData"] = "true",
        ["Auth:FixedOtp"] = "654321",
        ["Payments:PublicBaseUrl"] = "https://example.invalid",
        ["Reminders:Enabled"] = "true",
        ["Jobs:Enabled"] = "true",
    };

    [Fact]
    public async Task Hosted_demo_does_not_register_scheduled_outbound_or_console_pull_services()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, "Staging", Settings);
        using var client = factory.CreateClient();
        var names = factory.Services.GetServices<IHostedService>().Select(service => service.GetType().Name).ToArray();
        Assert.DoesNotContain("ReminderBackgroundService", names);
        Assert.DoesNotContain("MaintenanceBackgroundService", names);
        Assert.DoesNotContain("ConsolePullService", names);
    }

    [Theory]
    [InlineData("Staging", "123456")]
    [InlineData("Production", "654321")]
    public async Task Hosted_demo_rejects_public_default_otp_and_production_seed(string environment, string otp)
    {
        var settings = Settings;
        settings["Auth:FixedOtp"] = otp;
        await using var factory = new MoonlightApiFactory(db.ConnectionString, environment, settings);
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
