using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.AddMoonlight();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages(context => Problems.WriteAsync(context.HttpContext, ErrorCodes.FromStatus(context.HttpContext.Response.StatusCode)));

var clinicName = app.Services.GetRequiredService<ClinicConfig>().Name.En;
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options => options.SwaggerEndpoint("/swagger/v1/swagger.json", $"{clinicName} API v1"));

    // Migrations and demo data run on startup in Development only; production migrates in an explicit CI/CD step.
    using var scope = app.Services.CreateScope();
    var db = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
    await db.Database.MigrateAsync();
    await DevSeeder.SeedAsync(db, scope.ServiceProvider.GetRequiredService<ClinicConfig>(), scope.ServiceProvider.GetRequiredService<TimeProvider>(), app.Logger);
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
app.UseOtpPhoneCapture();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthEndpoint();
app.MapControllers();

app.Run();

public partial class Program;
