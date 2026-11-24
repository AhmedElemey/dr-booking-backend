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
        "POST /api/v1/dashboard/appointments/{id}/correct-status",
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

    // Every endpoint of docs/FEATURES-PHASE2.md.
    public static readonly string[] Phase2Endpoints =
    [
        "GET /api/v1/clinics/{clinicId}/holidays",
        "GET /api/v1/dashboard/holidays",
        "POST /api/v1/dashboard/holidays",
        "DELETE /api/v1/dashboard/holidays/{id}",
        "POST /api/v1/dashboard/holidays/import-egypt",
        "GET /api/v1/dashboard/holidays/{id}/conflicts",
        "GET /api/v1/dashboard/working-hours-templates",
        "POST /api/v1/dashboard/working-hours-templates",
        "PUT /api/v1/dashboard/working-hours-templates/{id}",
        "DELETE /api/v1/dashboard/working-hours-templates/{id}",
        "POST /api/v1/dashboard/doctors/{id}/slots/apply-template",
        "POST /api/v1/waiting-list",
        "GET /api/v1/waiting-list",
        "DELETE /api/v1/waiting-list/{id}",
        "GET /api/v1/dashboard/waiting-list",
        "POST /api/v1/dashboard/waiting-list/{id}/notify",
        "GET /api/v1/me/family",
        "POST /api/v1/me/family",
        "PUT /api/v1/me/family/{id}",
        "DELETE /api/v1/me/family/{id}",
        "GET /api/v1/dashboard/messages",
        "POST /api/v1/appointments/{id}/pay",
        "GET /api/v1/payments/{id}",
        "POST /api/v1/payments/webhook/{provider}",
        "GET /api/v1/payments/return",
        "GET /api/v1/payments/mock-checkout/{paymentId}",
        "GET /api/v1/dashboard/payments",
        "POST /api/v1/dashboard/appointments/{id}/mark-paid",
        "POST /api/v1/dashboard/payments/{id}/refund",
        "GET /api/v1/insurance-providers",
        "GET /api/v1/dashboard/insurance-providers",
        "POST /api/v1/dashboard/insurance-providers",
        "PUT /api/v1/dashboard/insurance-providers/{id}",
        "DELETE /api/v1/dashboard/insurance-providers/{id}",
        "PUT /api/v1/dashboard/doctors/{id}/insurance-providers",
        "POST /api/v1/dashboard/appointments/{id}/insurance",
        "GET /api/v1/dashboard/patients",
        "GET /api/v1/dashboard/patients/{userId}",
        "PUT /api/v1/dashboard/patients/{userId}/medical-info",
        "POST /api/v1/dashboard/patients/{userId}/records",
        "PUT /api/v1/dashboard/records/{id}",
        "DELETE /api/v1/dashboard/records/{id}",
        "PUT /api/v1/dashboard/records/{id}/attachment",
        "GET /api/v1/dashboard/records/{id}/attachment",
        "GET /api/v1/me/records/{id}/attachment",
        "GET /api/v1/me/records",
        "POST /api/v1/appointments/{id}/review",
        "GET /api/v1/doctors/{idOrSlug}/reviews",
        "GET /api/v1/dashboard/reviews",
        "POST /api/v1/dashboard/reviews/{id}/hide",
        "POST /api/v1/dashboard/reviews/{id}/publish",
        "PUT /api/v1/dashboard/reviews/{id}/reply",
        "GET /api/v1/promotions",
        "POST /api/v1/coupons/validate",
        "GET /api/v1/dashboard/coupons",
        "POST /api/v1/dashboard/coupons",
        "GET /api/v1/dashboard/coupons/{id}",
        "PUT /api/v1/dashboard/coupons/{id}",
        "DELETE /api/v1/dashboard/coupons/{id}",
        "GET /api/v1/dashboard/coupons/{id}/redemptions",
        "GET /api/v1/dashboard/recall-campaigns",
        "POST /api/v1/dashboard/recall-campaigns",
        "GET /api/v1/dashboard/recall-campaigns/{id}",
        "PUT /api/v1/dashboard/recall-campaigns/{id}",
        "DELETE /api/v1/dashboard/recall-campaigns/{id}",
        "POST /api/v1/dashboard/recall-campaigns/{id}/preview",
        "POST /api/v1/dashboard/recall-campaigns/{id}/send-now",
        "GET /api/v1/dashboard/recall-campaigns/{id}/sends",
        "GET /api/v1/dashboard/reports/bookings",
        "GET /api/v1/dashboard/reports/revenue",
        "GET /api/v1/appointments/{id}/video",
        "GET /api/v1/dashboard/appointments/{id}/video",
    ];

    private static readonly string[] Anonymous =
    [
        "GET /api/v1/clinics/{clinicId}/holidays",
        "GET /api/v1/insurance-providers",
        "GET /api/v1/promotions",
        "GET /api/v1/doctors/{idOrSlug}/reviews",
        "POST /api/v1/payments/webhook/{provider}",
        "GET /api/v1/payments/return",
        "GET /api/v1/payments/mock-checkout/{paymentId}",
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

        var expected = SpecEndpoints.Concat(Phase2Endpoints).ToArray();
        Assert.Empty(expected.Except(documented.Keys));
        Assert.Empty(documented.Keys.Except(expected));
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
