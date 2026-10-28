using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Moonlight.Api.Common;

public static class HealthEndpoint
{
    // GET /health: anonymous liveness + database check. 200 when healthy, 503 otherwise.
    public static void MapHealthEndpoint(this WebApplication app) =>
        app.MapHealthChecks("/health", new HealthCheckOptions
        {
            ResponseWriter = (context, report) => context.Response.WriteAsJsonAsync(new
            {
                status = report.Status.ToString(),
                checks = report.Entries.ToDictionary(e => e.Key, e => e.Value.Status.ToString()),
            }),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        });
}
