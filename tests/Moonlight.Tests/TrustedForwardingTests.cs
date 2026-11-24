using System.Net;

using Microsoft.AspNetCore.Http;

namespace Moonlight.Tests;

public sealed class TrustedForwardingTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Theory]
    [InlineData(false, "10.24.1.9", "10.24.1.9")]
    [InlineData(true, "192.0.2.99", "192.0.2.99")]
    [InlineData(true, "10.24.1.9", "203.0.113.7")]
    public async Task Forwarding_requires_explicit_trust_and_limits_the_chain(bool trusted, string sender, string expected)
    {
        var settings = new Dictionary<string, string?> { ["ReverseProxy:ForwardLimit"] = "1" };
        if (trusted) settings["ReverseProxy:KnownNetworks:0"] = "10.24.0.0/16";
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: settings);
        using var client = factory.CreateClient();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(sender);
            context.Request.Path = "/health";
            context.Request.Headers["X-Forwarded-For"] = "198.51.100.8, 203.0.113.7";
            context.Request.Headers["X-Forwarded-Proto"] = "https";
        });
        Assert.Equal(expected, result.Connection.RemoteIpAddress?.ToString());
    }

    [Fact]
    public async Task Forged_leftmost_forwarded_for_does_not_change_the_client_ip()
    {
        // Shape measured on Render: sender ::1, then client, Cloudflare, 10.x hop.
        var settings = new Dictionary<string, string?>
        {
            ["ReverseProxy:ForwardLimit"] = "3",
            ["ReverseProxy:KnownProxies:0"] = "::1",
            ["ReverseProxy:KnownNetworks:0"] = "10.0.0.0/8",
            ["ReverseProxy:KnownNetworks:1"] = "172.64.0.0/13",
        };
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: settings);
        using var client = factory.CreateClient();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("::1");
            context.Request.Path = "/health";
            context.Request.Headers["X-Forwarded-For"] = "1.2.3.4,203.0.113.9, 172.70.108.104, 10.24.205.99";
        });
        Assert.Equal("203.0.113.9", result.Connection.RemoteIpAddress?.ToString());
    }

    [Theory]
    [InlineData("development-only-signing-key-0123456789abcdef")]
    [InlineData("CHANGE-ME-signing-key-0123456789abcdef")]
    public async Task Non_development_refuses_public_signing_keys(string key)
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: new() { ["Jwt:Key"] = key });
        Assert.Throws<InvalidOperationException>(() => factory.CreateClient());
    }
}
