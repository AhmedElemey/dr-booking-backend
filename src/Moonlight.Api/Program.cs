using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
if (builder.Configuration.GetValue("Demo:SeedSyntheticData", false))
{
    if (!builder.Environment.IsStaging())
    {
        throw new InvalidOperationException("Hosted synthetic demo mode requires Staging.");
    }

    var otp = builder.Configuration["Auth:FixedOtp"];
    if (otp is not { Length: 6 } || !otp.All(char.IsAsciiDigit) || otp == "123456")
    {
        throw new InvalidOperationException("Hosted demo requires a private six-digit test OTP, not the public development default.");
    }

    if (!string.IsNullOrEmpty(builder.Configuration["Fcm:ServiceAccountJson"]) ||
        !string.IsNullOrEmpty(builder.Configuration["Console:ApiKey"]) ||
        !string.Equals(builder.Configuration["Payments:Provider"] ?? "Mock", "Mock", StringComparison.OrdinalIgnoreCase) ||
        !string.Equals(builder.Configuration["WhatsApp:Provider"] ?? "Mock", "Mock", StringComparison.OrdinalIgnoreCase))
    {
        throw new InvalidOperationException("Hosted demo must not have live provider credentials or console provisioning enabled.");
    }

    // Demo website requests may share a server-side proxy bucket. Phone limits stay unchanged.
    if (builder.Configuration.GetValue("RateLimits:OtpPerIpPerHour", 20) < 200)
    {
        builder.Configuration["RateLimits:OtpPerIpPerHour"] = "200";
    }
    builder.Configuration["Jobs:Enabled"] = "false";
    builder.Configuration["Reminders:Enabled"] = "false";
    builder.Configuration["Sms:NotificationsEnabled"] = "false";
    builder.Configuration["Payments:AllowMock"] = "false";
    builder.Configuration["WhatsApp:AllowMock"] = "false";
}
builder.AddMoonlight();

var app = builder.Build();

app.UseForwardingDiagnostics();
app.UseForwardedHeaders();
app.UseExceptionHandler();
app.UseStatusCodePages(context => Problems.WriteAsync(context.HttpContext, ErrorCodes.FromStatus(context.HttpContext.Response.StatusCode)));

var clinicName = app.Services.GetRequiredService<ClinicConfig>().Name.En;
if (app.Environment.IsDevelopment() || builder.Configuration.GetValue("Demo:SeedSyntheticData", false))
{
    if (!app.Environment.IsDevelopment() && !app.Environment.IsStaging())
    {
        throw new InvalidOperationException("Synthetic demo seeding is restricted to Development or Staging.");
    }

    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
    await db.Database.MigrateAsync();
    await DevSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<ClinicConfig>(), scope.ServiceProvider.GetRequiredService<TimeProvider>(), app.Logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", $"{clinicName} API v1"));
}

var storage = app.Services.GetRequiredService<IOptions<StorageOptions>>().Value;
var uploadsRoot = LocalDiskPhotoStorage.Root(storage, app.Environment);
Directory.CreateDirectory(uploadsRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(uploadsRoot),
    RequestPath = storage.RequestPath,
});

app.UseCors();
if (builder.Configuration.GetValue("Demo:SeedSyntheticData", false))
{
    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Robots-Tag"] = "noindex, nofollow, noarchive";
        if (context.Request.HasFormContentType)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new { code = "DEMO_UPLOADS_DISABLED" });
            return;
        }

        await next(context);
    });
}
app.UseOtpPhoneCapture();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthEndpoint();
app.MapControllers();

app.Run();

public partial class Program;
