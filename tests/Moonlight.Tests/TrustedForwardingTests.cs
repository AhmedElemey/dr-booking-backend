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

    [Theory]
    [InlineData("203.0.113.9, 172.70.108.104, 10.24.205.99")]
    [InlineData("1.2.3.4,203.0.113.9, 172.70.108.104, 10.24.205.99")]
    public async Task Render_chain_resolves_to_the_real_client_and_ignores_a_forged_leftmost_entry(string forwardedFor)
    {
        // Shape measured on Render: sender ::1, then [client, Cloudflare, 10.x hop].
        // Cloudflare ranges are from https://www.cloudflare.com/ips-v4 and /ips-v6.
        var settings = new Dictionary<string, string?>
        {
            ["ReverseProxy:ForwardLimit"] = "3",
            ["ReverseProxy:KnownProxies:0"] = "::1",
            ["ReverseProxy:KnownNetworks:0"] = "10.0.0.0/8",
            ["ReverseProxy:KnownNetworks:1"] = "173.245.48.0/20",
            ["ReverseProxy:KnownNetworks:2"] = "103.21.244.0/22",
            ["ReverseProxy:KnownNetworks:3"] = "103.22.200.0/22",
            ["ReverseProxy:KnownNetworks:4"] = "103.31.4.0/22",
            ["ReverseProxy:KnownNetworks:5"] = "141.101.64.0/18",
            ["ReverseProxy:KnownNetworks:6"] = "108.162.192.0/18",
            ["ReverseProxy:KnownNetworks:7"] = "190.93.240.0/20",
            ["ReverseProxy:KnownNetworks:8"] = "188.114.96.0/20",
            ["ReverseProxy:KnownNetworks:9"] = "197.234.240.0/22",
            ["ReverseProxy:KnownNetworks:10"] = "198.41.128.0/17",
            ["ReverseProxy:KnownNetworks:11"] = "162.158.0.0/15",
            ["ReverseProxy:KnownNetworks:12"] = "104.16.0.0/13",
            ["ReverseProxy:KnownNetworks:13"] = "104.24.0.0/14",
            ["ReverseProxy:KnownNetworks:14"] = "172.64.0.0/13",
            ["ReverseProxy:KnownNetworks:15"] = "131.0.72.0/22",
            ["ReverseProxy:KnownNetworks:16"] = "2400:cb00::/32",
            ["ReverseProxy:KnownNetworks:17"] = "2606:4700::/32",
            ["ReverseProxy:KnownNetworks:18"] = "2803:f800::/32",
            ["ReverseProxy:KnownNetworks:19"] = "2405:b500::/32",
            ["ReverseProxy:KnownNetworks:20"] = "2405:8100::/32",
            ["ReverseProxy:KnownNetworks:21"] = "2a06:98c0::/29",
            ["ReverseProxy:KnownNetworks:22"] = "2c0f:f248::/32",
        };
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: settings);
        using var client = factory.CreateClient();
        var result = await factory.Server.SendAsync(context =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse("::1");
            context.Request.Path = "/health";
            context.Request.Headers["X-Forwarded-For"] = forwardedFor;
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
