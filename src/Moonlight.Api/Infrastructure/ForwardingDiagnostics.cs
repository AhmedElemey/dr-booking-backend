namespace Moonlight.Api.Infrastructure;

// TEMPORARY. Measures what reaches the app behind Render's proxies so that
// KnownNetworks and ForwardLimit can be pinned from evidence. Off unless
// Diagnostics__LogForwarding=true. Logs only RemoteIpAddress, X-Forwarded-For,
// CF-Connecting-IP and the path, and only for GET /health. To be removed in
// the PR that sets KnownNetworks and ForwardLimit.
public static class ForwardingDiagnostics
{
    public static void UseForwardingDiagnostics(this WebApplication app)
    {
        if (!app.Configuration.GetValue<bool>("Diagnostics:LogForwarding"))
        {
            return;
        }

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ForwardingDiagnostics");
        app.Use(async (context, next) =>
        {
            if (HttpMethods.IsGet(context.Request.Method) &&
                context.Request.Path.Equals("/health", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning(
                    "FWDLOG path={Path} remote={Remote} xff=[{Xff}] cf=[{Cf}]",
                    context.Request.Path.Value,
                    context.Connection.RemoteIpAddress?.ToString(),
                    context.Request.Headers["X-Forwarded-For"].ToString(),
                    context.Request.Headers["CF-Connecting-IP"].ToString());
            }

            await next(context);
        });
    }
}
