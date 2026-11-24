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
using Moonlight.Api.Features.Family;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Holidays;
using Moonlight.Api.Features.Insurance;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Features.Payments;
using Moonlight.Api.Features.Promotions;
using Moonlight.Api.Features.Recall;
using Moonlight.Api.Features.Records;
using Moonlight.Api.Features.Reports;
using Moonlight.Api.Features.Reviews;
using Moonlight.Api.Features.Templates;
using Moonlight.Api.Features.Video;
using Moonlight.Api.Features.WaitingList;
using Moonlight.Api.Infrastructure.Auth;
using Moonlight.Api.Infrastructure.Fcm;
using Moonlight.Api.Infrastructure.Payments;
using Moonlight.Api.Infrastructure.Sms;
using Moonlight.Api.Infrastructure.Storage;
using Moonlight.Api.Infrastructure.Video;
using Moonlight.Api.Infrastructure.WhatsApp;

using Swashbuckle.AspNetCore.SwaggerGen;

namespace Moonlight.Api.Infrastructure;

public static class ServiceRegistration
{
    public static void AddMoonlight(this WebApplicationBuilder builder)
    {
        var configuration = builder.Configuration;
        var services = builder.Services;
        var environment = builder.Environment;
        services.AddTrustedForwarding(configuration);

        var connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

        // Clinic identity and default feature switches (fail fast when the file is missing or invalid).
        ClinicConfig clinic;
        try
        {
            clinic = ClinicConfigLoader.LoadFile(ClinicConfigLoader.Locate(configuration["ClinicConfig:Path"], environment.ContentRootPath));
        }
        catch (ClinicConfigException ex)
        {
            throw new InvalidOperationException(ex.Message, ex);
        }

        if (configuration.GetValue("Demo:SeedSyntheticData", false))
        {
            // Hosted tests never expose records, uploads, payments or outbound messaging.
            var features = clinic.Features.ToDictionary(pair => pair.Key, pair => pair.Value);
            foreach (var key in new[] { "patientRecords", "onlinePayments", "pushReminders", "smsNotifications", "whatsappReminders", "recallCampaigns", "videoConsultation" })
            {
                features[key] = false;
            }

            clinic = clinic with { Features = features };
        }

        services.AddSingleton(clinic);
        services.AddSingleton<ClinicFeatureOverrides>();
        services.AddSingleton<FeatureService>();

        // Options
        services.Configure<BookingOptions>(configuration.GetSection(BookingOptions.Section));
        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.Section));
        services.Configure<RateLimitOptions>(configuration.GetSection(RateLimitOptions.Section));
        services.Configure<SmsOptions>(configuration.GetSection(SmsOptions.Section));
        services.Configure<FcmOptions>(configuration.GetSection(FcmOptions.Section));
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.Section));
        services.Configure<RemindersOptions>(configuration.GetSection(RemindersOptions.Section));
        services.Configure<PaymentsOptions>(configuration.GetSection(PaymentsOptions.Section));
        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.Section));
        services.Configure<VideoOptions>(configuration.GetSection(VideoOptions.Section));
        services.Configure<BackgroundJobsOptions>(configuration.GetSection(BackgroundJobsOptions.Section));

        var jwt = configuration.GetSection(JwtOptions.Section).Get<JwtOptions>() ?? new JwtOptions();
        if (jwt.Key.Length < 32)
        {
            throw new InvalidOperationException("Jwt:Key must be set (Jwt__Key) and at least 32 characters long.");
        }

        if (!environment.IsDevelopment() &&
            (jwt.Key.Contains("development-only", StringComparison.OrdinalIgnoreCase) ||
             jwt.Key.Contains("change-me", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Jwt:Key must not use a development signing key outside Development.");
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

        AddPhase2Providers(services, configuration, environment);

        services.AddSingleton<IPhotoStorage, LocalDiskPhotoStorage>();
        services.AddSingleton<IRecordFileStorage, LocalDiskRecordFileStorage>();
        services.AddScoped<MessageDispatcher>();
        services.AddScoped<INotificationService, NotificationService>();
        services.AddScoped<ReminderProcessor>();
        services.AddScoped<ReviewPromptProcessor>();
        services.AddScoped<MaintenanceProcessor>();
        if (configuration.GetValue("Reminders:Enabled", true))
        {
            services.AddHostedService<ReminderBackgroundService>();
        }

        if (configuration.GetValue("Jobs:Enabled", true))
        {
            services.AddHostedService<MaintenanceBackgroundService>();
        }

        // Runtime feature switches pulled from the super admin console (only when Console:Url/ClinicId/ApiKey are all set).
        var console = ConsoleSettings.Resolve(configuration, clinic);
        services.AddSingleton(console);
        services.AddHttpClient(ConsoleSettings.HttpClientName);
        services.AddSingleton<IFeatureStateStore, EfFeatureStateStore>();
        services.AddSingleton<ConsolePuller>();
        if (console.IsConfigured)
        {
            services.AddHostedService<ConsolePullService>();
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
        services.AddScoped<HolidayService>();
        services.AddScoped<TemplateService>();
        services.AddScoped<WaitingListService>();
        services.AddScoped<FamilyService>();
        services.AddScoped<PaymentService>();
        services.AddScoped<InsuranceService>();
        services.AddScoped<RecordsService>();
        services.AddScoped<ReviewService>();
        services.AddScoped<CouponService>();
        services.AddScoped<RecallService>();
        services.AddScoped<ReportService>();
        services.AddScoped<VideoService>();

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
                json.JsonSerializerOptions.Converters.Add(new FlexibleDateOnlyJsonConverter());
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
            policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials().WithExposedHeaders("X-Total-Count")));

        // OpenAPI
        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new OpenApiInfo
            {
                Title = $"{clinic.Name.En} API",
                Version = "v1",
                Description = $"Dentist reservation platform for {clinic.Name.En}. Errors are RFC 7807 problem+json with a stable `code`.",
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

    // Payment, WhatsApp and video providers are chosen by configuration (Payments:Provider, WhatsApp:Provider, Video:Provider).
    private static void AddPhase2Providers(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var payments = configuration.GetSection(PaymentsOptions.Section).Get<PaymentsOptions>() ?? new PaymentsOptions();
        switch (payments.Provider.ToLowerInvariant())
        {
            case "mock":
                if (environment.IsDevelopment() || payments.AllowMock)
                {
                    services.AddScoped<IPaymentProvider, MockPaymentProvider>();
                }
                else
                {
                    // Starts anyway: payments answer PAYMENT_NOT_ALLOWED and the mock endpoints answer 404.
                    services.AddSingleton<IPaymentProvider, UnconfiguredPaymentProvider>();
                }

                break;
            case "paymob":
                var paymob = payments.Paymob;
                var missing = new[]
                {
                    (nameof(PaymobOptions.ApiKey), paymob.ApiKey),
                    (nameof(PaymobOptions.IntegrationId), paymob.IntegrationId),
                    (nameof(PaymobOptions.IframeId), paymob.IframeId),
                    (nameof(PaymobOptions.HmacSecret), paymob.HmacSecret),
                }.Where(x => string.IsNullOrWhiteSpace(x.Item2)).Select(x => $"Payments:Paymob:{x.Item1}").ToList();
                if (missing.Count > 0)
                {
                    throw new InvalidOperationException($"Payments:Provider is Paymob but {string.Join(", ", missing)} {(missing.Count == 1 ? "is" : "are")} not set.");
                }

                services.AddHttpClient<IPaymentProvider, PaymobPaymentProvider>(client => client.Timeout = TimeSpan.FromSeconds(30));
                break;
            default:
                throw new InvalidOperationException($"Payments:Provider '{payments.Provider}' is not supported; use 'Mock' or 'Paymob'.");
        }

        var whatsApp = configuration.GetSection(WhatsAppOptions.Section).Get<WhatsAppOptions>() ?? new WhatsAppOptions();
        switch (whatsApp.Provider.ToLowerInvariant())
        {
            case "mock":
                if (environment.IsDevelopment() || whatsApp.AllowMock)
                {
                    services.AddSingleton<IWhatsAppSender, MockWhatsAppSender>();
                }
                else
                {
                    services.AddSingleton<IWhatsAppSender, UnconfiguredWhatsAppSender>();
                }

                break;
            case "meta":
                if (string.IsNullOrWhiteSpace(whatsApp.PhoneNumberId) || string.IsNullOrWhiteSpace(whatsApp.AccessToken))
                {
                    throw new InvalidOperationException("WhatsApp:Provider is Meta but WhatsApp:PhoneNumberId and WhatsApp:AccessToken are not both set.");
                }

                services.AddHttpClient<IWhatsAppSender, MetaWhatsAppSender>(client => client.Timeout = TimeSpan.FromSeconds(30));
                break;
            default:
                throw new InvalidOperationException($"WhatsApp:Provider '{whatsApp.Provider}' is not supported; use 'Mock' or 'Meta'.");
        }

        var video = configuration.GetSection(VideoOptions.Section).Get<VideoOptions>() ?? new VideoOptions();
        switch (video.Provider.ToLowerInvariant())
        {
            case "jitsi":
                services.AddSingleton<IVideoProvider, JitsiVideoProvider>();
                break;
            case "mock":
                services.AddSingleton<IVideoProvider, MockVideoProvider>();
                break;
            default:
                throw new InvalidOperationException($"Video:Provider '{video.Provider}' is not supported; use 'Jitsi' or 'Mock'.");
        }
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
