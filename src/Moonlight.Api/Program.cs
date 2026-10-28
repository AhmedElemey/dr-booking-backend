using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration.GetConnectionString("Default")
    ?? throw new InvalidOperationException("ConnectionStrings:Default is not configured.");

builder.Services.AddDbContext<MoonlightDbContext>(options => options.UseNpgsql(connectionString));
builder.Services.AddHealthChecks().AddNpgSql(connectionString, name: "database");
builder.Services.AddProblemDetails();
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
    policy.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();

    using var scope = app.Services.CreateScope();
    scope.ServiceProvider.GetRequiredService<MoonlightDbContext>().Database.Migrate();
}

app.UseCors();
app.MapHealthEndpoint();
app.MapControllers();

app.Run();

public partial class Program;
