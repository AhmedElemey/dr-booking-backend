using System.Diagnostics;

using Microsoft.AspNetCore.Diagnostics;

namespace Moonlight.Api.Common;

// RFC 7807 application/problem+json with a stable machine `code`.
public static class Problems
{
    public static async Task WriteAsync(
        HttpContext context,
        string code,
        string? detail = null,
        IDictionary<string, string[]>? errors = null,
        IDictionary<string, object?>? extensions = null,
        int? statusOverride = null)
    {
        var lang = Language.FromRequest(context.Request);
        var status = statusOverride ?? ErrorCodes.StatusOf(code);
        var body = new Dictionary<string, object?>
        {
            ["type"] = $"https://moonlight.example/errors/{code.ToLowerInvariant().Replace('_', '-')}",
            ["title"] = ErrorCodes.TitleOf(code, lang),
            ["status"] = status,
            ["code"] = code,
            ["detail"] = detail ?? ErrorCodes.DetailOf(code, lang),
            ["traceId"] = Activity.Current?.Id ?? context.TraceIdentifier,
        };
        if (errors is { Count: > 0 })
        {
            body["errors"] = errors;
        }

        if (extensions is not null)
        {
            foreach (var (key, value) in extensions)
            {
                body[key] = value;
            }
        }

        context.Response.StatusCode = status;
        await context.Response.WriteAsJsonAsync(body, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json");
    }
}

public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is ApiException api)
        {
            await Problems.WriteAsync(httpContext, api.Code, api.Detail, api.Errors, api.Extensions, api.StatusOverride);
            return true;
        }

        logger.LogError(exception, "Unhandled exception for {Method} {Path}", httpContext.Request.Method, httpContext.Request.Path);
        await Problems.WriteAsync(httpContext, ErrorCodes.InternalError);
        return true;
    }
}

/// <summary>RFC 7807 problem details with a stable machine-readable <c>code</c> (documentation type for Swagger).</summary>
public sealed record ApiProblem(
    string Type,
    string Title,
    int Status,
    string Code,
    string Detail,
    string TraceId,
    IDictionary<string, string[]>? Errors,
    string? Feature = null);
