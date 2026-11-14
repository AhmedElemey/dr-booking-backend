using Microsoft.Extensions.DependencyInjection;

using Swashbuckle.AspNetCore.Swagger;

namespace Moonlight.Tests;

public class SwaggerTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    // Every endpoint of spec section 4.
    public static readonly string[] SpecEndpoints =
    [
        "POST /api/v1/auth/otp/request",
        "POST /api/v1/auth/otp/verify",
        "POST /api/v1/auth/refresh",
        "POST /api/v1/auth/logout",
        "GET /api/v1/me",
        "PUT /api/v1/me",
        "PUT /api/v1/me/device-tokens",
        "DELETE /api/v1/me/device-tokens/{id}",
        "GET /api/v1/specialties",
        "GET /api/v1/cities",
        "GET /api/v1/doctors",
        "GET /api/v1/doctors/{idOrSlug}",
        "GET /api/v1/doctors/{idOrSlug}/slots",
        "POST /api/v1/appointments",
        "GET /api/v1/appointments",
        "GET /api/v1/appointments/{id}",
        "POST /api/v1/appointments/{id}/cancel",
        "POST /api/v1/appointments/{id}/reschedule",
        "GET /api/v1/dashboard/appointments",
        "GET /api/v1/dashboard/appointments/{id}",
        "POST /api/v1/dashboard/appointments",
        "POST /api/v1/dashboard/appointments/{id}/reschedule",
        "POST /api/v1/dashboard/appointments/{id}/cancel",
        "POST /api/v1/dashboard/appointments/{id}/complete",
        "POST /api/v1/dashboard/appointments/{id}/no-show",
        "GET /api/v1/dashboard/doctors",
        "GET /api/v1/dashboard/doctors/{id}",
        "PUT /api/v1/dashboard/doctors/{id}",
        "PUT /api/v1/dashboard/doctors/{id}/photo",
        "GET /api/v1/dashboard/doctors/{id}/services",
        "POST /api/v1/dashboard/doctors/{id}/services",
        "PUT /api/v1/dashboard/doctors/{id}/services/{serviceId}",
        "DELETE /api/v1/dashboard/doctors/{id}/services/{serviceId}",
        "GET /api/v1/dashboard/doctors/{id}/slots",
        "POST /api/v1/dashboard/doctors/{id}/slots",
        "POST /api/v1/dashboard/doctors/{id}/slots/bulk",
        "POST /api/v1/dashboard/doctors/{id}/slots/{slotId}/block",
        "POST /api/v1/dashboard/doctors/{id}/slots/{slotId}/unblock",
        "DELETE /api/v1/dashboard/doctors/{id}/slots/{slotId}",
        "DELETE /api/v1/dashboard/doctors/{id}/slots",
        "GET /api/v1/dashboard/clinic",
        "PUT /api/v1/dashboard/clinic",
        "GET /api/v1/admin/clinics",
        "POST /api/v1/admin/clinics",
        "PATCH /api/v1/admin/clinics/{id}/active",
        "POST /api/v1/admin/clinics/{id}/users",
        "PATCH /api/v1/admin/users/{id}/active",
        "GET /health",
        "GET /api/v1/features",
        "GET /api/v1/branding",
        "GET /api/v1/branding/logo",
    ];

    private static readonly string[] Anonymous =
    [
        "POST /api/v1/auth/otp/request",
        "POST /api/v1/auth/otp/verify",
        "POST /api/v1/auth/refresh",
        "GET /api/v1/specialties",
        "GET /api/v1/cities",
        "GET /api/v1/doctors",
        "GET /api/v1/doctors/{idOrSlug}",
        "GET /api/v1/doctors/{idOrSlug}/slots",
        "GET /api/v1/features",
        "GET /api/v1/branding",
        "GET /api/v1/branding/logo",
        "GET /health",
    ];

    [Fact]
    public async Task Every_endpoint_of_the_spec_is_documented_with_a_summary_and_responses()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString);
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        var documented = document.Paths
            .SelectMany(p => p.Value.Operations.Select(o => (Key: $"{o.Key.ToString().ToUpperInvariant()} {p.Key}", Operation: o.Value)))
            .ToDictionary(x => x.Key, x => x.Operation);

        Assert.Empty(SpecEndpoints.Except(documented.Keys));
        Assert.Empty(documented.Keys.Except(SpecEndpoints));
        foreach (var (key, operation) in documented)
        {
            Assert.False(string.IsNullOrWhiteSpace(operation.Summary), $"{key} has no summary");
            Assert.NotEmpty(operation.Responses);
            var requiresAuth = !Anonymous.Contains(key);
            Assert.Equal(requiresAuth, operation.Security.Count > 0);
            if (requiresAuth)
            {
                Assert.Contains("401", operation.Responses.Keys);
            }
        }
    }

    [Fact]
    public async Task The_document_declares_the_bearer_scheme_and_the_problem_shape()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString);
        var document = factory.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");

        Assert.Contains("Bearer", document.Components.SecuritySchemes.Keys);
        Assert.Contains("ApiProblem", document.Components.Schemas.Keys);
        Assert.Contains(document.Components.Schemas["ApiProblem"].Properties.Keys, k => k == "code");
    }

    [Fact]
    public async Task Swagger_ui_is_not_exposed_outside_development()
    {
        await using var testing = new MoonlightApiFactory(db.ConnectionString);
        Assert.Equal(404, (int)(await testing.CreateClient().GetAsync("/swagger/v1/swagger.json")).StatusCode);
    }
}
