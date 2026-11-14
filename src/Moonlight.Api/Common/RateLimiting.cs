using System.Text.Json;
using System.Threading.RateLimiting;

using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Moonlight.Api.Common;

// OTP request limits (spec section 4): 1/min and 5/hour per phone, 20/hour per IP, using the built-in ASP.NET rate limiter.
public static class RateLimiting
{
    private const string OtpRequestPath = "/api/v1/auth/otp/request";
    private const string PhoneItemKey = "moonlight.otp.phone";

    public static IServiceCollection AddMoonlightRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(_ => { });
        services.AddOptions<RateLimiterOptions>().Configure<IOptions<RateLimitOptions>>((options, limits) =>
        {
            var l = limits.Value;
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, _) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await Problems.WriteAsync(context.HttpContext, ErrorCodes.RateLimited);
            };
            options.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PhoneLimiter("min", l.OtpPerPhonePerMinute, TimeSpan.FromMinutes(1)),
                PhoneLimiter("hour", l.OtpPerPhonePerHour, TimeSpan.FromHours(1)),
                PartitionedRateLimiter.Create<HttpContext, string>(context =>
                    IsOtpRequest(context.Request)
                        ? RateLimitPartition.GetFixedWindowLimiter($"ip:{context.Connection.RemoteIpAddress}", _ => Window(l.OtpPerIpPerHour, TimeSpan.FromHours(1)))
                        : RateLimitPartition.GetNoLimiter("none")));
        });
        return services;
    }

    // Reads the phone out of the OTP request body (and rewinds it) so the limiter can partition by phone.
    public static IApplicationBuilder UseOtpPhoneCapture(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            if (IsOtpRequest(context.Request))
            {
                try
                {
                    context.Request.EnableBuffering(bufferThreshold: 8192, bufferLimit: 8192);
                    using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                    if (document.RootElement.ValueKind == JsonValueKind.Object
                        && document.RootElement.EnumerateObject().FirstOrDefault(p => p.NameEquals("phone") || p.Name.Equals("phone", StringComparison.OrdinalIgnoreCase)) is { Value.ValueKind: JsonValueKind.String } phone
                        && PhoneNumber.TryNormalize(phone.Value.GetString(), out var e164))
                    {
                        context.Items[PhoneItemKey] = e164;
                    }
                }
                catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
                {
                    // Malformed body: the endpoint itself reports the validation error; only the IP limit applies.
                }
                finally
                {
                    if (context.Request.Body.CanSeek)
                    {
                        context.Request.Body.Position = 0;
                    }
                }
            }

            await next();
        });

    private static PartitionedRateLimiter<HttpContext> PhoneLimiter(string name, int permits, TimeSpan window) =>
        PartitionedRateLimiter.Create<HttpContext, string>(context =>
            IsOtpRequest(context.Request) && context.Items[PhoneItemKey] is string phone
                ? RateLimitPartition.GetFixedWindowLimiter($"phone-{name}:{phone}", _ => Window(permits, window))
                : RateLimitPartition.GetNoLimiter("none"));

    private static FixedWindowRateLimiterOptions Window(int permits, TimeSpan window) =>
        new() { PermitLimit = permits, Window = window, QueueLimit = 0, AutoReplenishment = true };

    private static bool IsOtpRequest(HttpRequest request) =>
        HttpMethods.IsPost(request.Method) && request.Path.Equals(OtpRequestPath, StringComparison.OrdinalIgnoreCase);
}
