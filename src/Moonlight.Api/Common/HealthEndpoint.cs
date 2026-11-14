using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Moonlight.Api.Common;

public static class HealthEndpoint
{
    // GET /health: anonymous liveness + database check. 200 when healthy, 503 otherwise.
    public static void MapHealthEndpoint(this WebApplication app) =>
        app.MapGet("/health", async (HealthCheckService health, CancellationToken ct) =>
            {
                var report = await health.CheckHealthAsync(ct);
                var body = new HealthBody(report.Status.ToString(), report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()));
                return Results.Json(body, statusCode: report.Status == HealthStatus.Unhealthy ? StatusCodes.Status503ServiceUnavailable : StatusCodes.Status200OK);
            })
            .AllowAnonymous()
            .WithTags("Ops")
            .WithSummary("Liveness and database check")
            .WithDescription("200 with status Healthy when the API and database are reachable, 503 otherwise.")
            .Produces<HealthBody>(StatusCodes.Status200OK)
            .Produces<HealthBody>(StatusCodes.Status503ServiceUnavailable);

    public sealed record HealthBody(string Status, Dictionary<string, string> Checks);
}
