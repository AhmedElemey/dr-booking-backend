using System.Reflection;
using System.Text.Json.Serialization;

using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi.Models;

using Moonlight.Api.Common;
using Moonlight.Api.Features.Admin;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Auth;
using Moonlight.Api.Features.Catalog;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Infrastructure.Auth;
using Moonlight.Api.Infrastructure.Fcm;
using Moonlight.Api.Infrastructure.Sms;
using Moonlight.Api.Infrastructure.Storage;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace Moonlight.Api.Infrastructure;

public static class ServiceRegistration
{
    public static void AddMoonlight(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;
        var environment = builder.Environment;

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        // Options
        services.Configure<BookingOptions>(configuration.GetSection(BookingOptions.Section));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));
        services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.Section));
        services.Configure<FcmOptions>(configuration.GetSection(FcmOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<RemindersOptions>(configuration.GetSection(RemindersOptions.Section));

        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        if (jwt.Key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key must be set (Jwt__Key) and at least 32 characters long.");
        }

        // The fixed OTP is a development convenience. Gate it on the hosting environment, not just on a flag.
        var fixedOtp = configuration[$"{AuthOptions.Section}:{nameof(AuthOptions.FixedOtp)}"];
        if (environment.IsProduction() && !string.IsNullOrEmpty(fixedOtp))
        {
            throw new InvalidOperationException("Auth:FixedOtp must not be set in Production.");
        }

        if (!environment.IsProduction() && !string.IsNullOrEmpty(fixedOtp))
        {
            services.AddSingleton(new FixedOtpBypass(fixedOtp));
        }

        // Persistence
        services.AddDbContext<MoonlightDbContext>(options => options.UseNpgsql(connectionString).UseSnakeCaseNamingConvention());
        services.AddHealthChecks().AddNpgSql(connectionString, name: "database");

        // Cross-cutting
        services.AddHttpContextAccessor();
        services.AddSingleton(TimeProvider.System);
        services.AddScoped<CurrentUser>();
        services.AddScoped<Language>();
        services.AddSingleton<TokenService>();
        services.AddProblemDetails();
        services.AddExceptionHandler<ApiExceptionHandler>();
        services.AddMoonlightRateLimiting();

        // Integrations (SMS and push are interfaces; only the stubs / FCM exist until M6)
        var smsProvider = configuration[$"{SmsOptions.Section}:{nameof(SmsOptions.Provider)}"] ?? "Log";
        if (!smsProvider.Equals("Log", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Sms:Provider '{smsProvider}' is not supported yet; use 'Log'.");
        }

        services.AddSingleton<ISmsSender, LoggingSmsSender>();
        var fcmJson = configuration[$"{FcmOptions.Section}:{nameof(FcmOptions.ServiceAccountJson)}"];
        if (string.IsNullOrWhiteSpace(fcmJson))
        {
            services.AddSingleton<IPushSender, LoggingPushSender>();
        }
        else
        {
            services.AddSingleton<IPushSender>(sp => new FcmPushSender(fcmJson, sp.GetRequiredService<ILogger<FcmPushSender>>()));
        }

        services.AddSingleton<IPhotoStorage, LocalDiskPhotoStorage>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ReminderProcessor>();
        if (configuration.GetValue("Reminders:Enabled", true))
        {
            services.AddHostedService<ReminderBackgroundService>();
        }

        // Features
        services.AddScoped<AuthService>();
        services.AddScoped<CatalogService>();
        services.AddScoped<AppointmentMapper>();
        services.AddScoped<AppointmentService>();
        services.AddScoped<DashboardScope>();
        services.AddScoped<DoctorManagementService>();
        services.AddScoped<SlotService>();
        services.AddScoped<ClinicManagementService>();
        services.AddScoped<AdminService>();

        // Auth
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme).Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
        {
            var o = jwtOptions.Value;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new Microsoft.IdentityModel.Tokens.TokenValidationParameters
            {
                ValidIssuer = o.Issuer,
                ValidAudience = o.Audience,
                IssuerSigningKey = TokenService.SigningKey(o.Key),
                ValidateIssuerSigningKey = true,
                ValidAlgorithms = [Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256],
                NameClaimType = "sub",
                RoleClaimType = "role",
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });
        services.AddAuthorization();

        // MVC
        services.AddControllers()
            .AddJsonOptions(json =>
            {
                json.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                json.JsonSerializerOptions.Converters.Add(new TimeOnlyJsonConverter());
            })
            .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
            {
                var errors = context.ModelState
                    .Where(e => e.Value is { Errors.Count: > 0 })
                    .ToDictionary(
                        e => ToFieldName(e.Key),
                        e => e.Value!.Errors.Select(err => ToMessage(e.Key, err.ErrorMessage)).Distinct().ToArray());
                return new ProblemResult(ErrorCodes.ValidationFailed, errors);
            });

        // CORS
        var corsOrigins = configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
        services.AddCors(options => options.AddDefaultPolicy(policy =>
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

        // OpenAPI
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = "Moonlight API",
                Version = "v1",
                Description = "Dentist reservation platform. Errors are RFC 7807 problem+json with a stable `code`.",
            });
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, $"{Assembly.GetExecutingAssembly().GetName().Name}.xml"));
            options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Type = SecuritySchemeType.Http,
                Scheme = "bearer",
                BearerFormat = "JWT",
                Description = "Access token from POST /api/v1/auth/otp/verify.",
            });
            options.OperationFilter<BearerOperationFilter>();
        });
    }

    private static string ToFieldName(string key)
    {
        var name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key;
        name = name.Length == 0 ? "request" : name;
        return char.ToLowerInvariant(name[0]) + name[1..];
    }

    private static string ToMessage(string key, string message) =>
        key.StartsWith('$') || message.Contains("JSON", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(message)
            ? "Invalid value."
            : message;
}

public sealed class ProblemResult(string code, IDictionary<string, string[]>? errors = null) : IActionResult
{
    public Task ExecuteResultAsync(ActionContext context) => Problems.WriteAsync(context.HttpContext, code, errors: errors);
}

// Adds the bearer requirement to every endpoint that requires authorization.
public sealed class BearerOperationFilter : IOperationFilter
{
    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        var metadata = context.ApiDescription.ActionDescriptor.EndpointMetadata;
        var requiresAuth = metadata.OfType<IAuthorizeData>().Any() && !metadata.OfType<IAllowAnonymous>().Any();
        if (!requiresAuth)
        {
            return;
        }

        operation.Security =
        [
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [],
            },
        ];
    }
}
