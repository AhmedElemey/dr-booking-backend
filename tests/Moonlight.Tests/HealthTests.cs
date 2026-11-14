using System.Net;
using System.Net.Http.Json;

namespace Moonlight.Tests;

public class HealthTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Health_returns_200_when_database_is_reachable()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString);
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        Assert.Equal("Healthy", body?.Status);
        Assert.Equal("Healthy", body?.Checks["database"]);
    }

    [Fact]
    public async Task Health_returns_503_when_database_is_unreachable()
    {
        await using var factory = new MoonlightApiFactory(
            "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=2");
        var response = await factory.CreateClient().GetAsync("/health");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed record HealthBody(string Status, Dictionary<string, string> Checks);
}
