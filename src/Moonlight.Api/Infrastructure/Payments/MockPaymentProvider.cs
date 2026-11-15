using System.Text.Json;

using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure.Payments;

// Development / demo provider: checkout is a page served by this API (GET /api/v1/payments/mock-checkout/{paymentId})
// whose buttons post to the mock webhook. It has no signature, so it only exists when Payments:Provider is Mock.
public sealed class MockPaymentProvider(IOptions<PaymentsOptions> options, IHttpContextAccessor accessor) : IPaymentProvider
{
    public string Name => "mock";

    public Task<CheckoutResult> CreateCheckoutAsync(Payment payment, string returnUrl, PaymentCustomer customer, CancellationToken ct = default)
    {
        var baseUrl = options.Value.PublicBaseUrl.TrimEnd('/');
        if (baseUrl.Length == 0 && accessor.HttpContext is { } context)
        {
            baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        }

        return Task.FromResult(new CheckoutResult($"{baseUrl}/api/v1/payments/mock-checkout/{payment.Id}", payment.Id.ToString()));
    }

    // Body: { "providerRef": "<payment id>", "success": true|false }
    public Task<WebhookEvent> ParseWebhookAsync(WebhookRequest request, CancellationToken ct = default)
    {
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;
            var providerRef = root.GetProperty("providerRef").GetString() ?? "";
            var success = root.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            return Task.FromResult(new WebhookEvent(WebhookKind.Payment, providerRef, "mock-" + providerRef, success, request.Body));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw ApiException.Validation("body", "Expected { providerRef, success }.");
        }
    }

    public Task<RefundResult> RefundAsync(Payment payment, CancellationToken ct = default) =>
        Task.FromResult(new RefundResult(true, "mock-refund-" + payment.Id));
}
