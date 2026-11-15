using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure.Payments;

public sealed record PaymentCustomer(string Name, string Phone);

public sealed record CheckoutResult(string CheckoutUrl, string ProviderRef);

public enum WebhookKind
{
    Payment,
    Refund,
    Ignored,
}

// ProviderRef is the provider order reference (equal to Payment.ProviderRef); TransactionId is needed later to refund.
public sealed record WebhookEvent(WebhookKind Kind, string ProviderRef, string? TransactionId, bool Succeeded, string Raw, long? AmountCents = null, string? Currency = null);

public sealed record WebhookRequest(string Body, IReadOnlyDictionary<string, string> Query);

public sealed record RefundResult(bool Succeeded, string? ProviderRef);

/// <summary>The signature of a webhook did not verify (answered with 401).</summary>
public sealed class WebhookSignatureException(string message) : Exception(message);

public interface IPaymentProvider
{
    /// <summary>Route segment of <c>POST /api/v1/payments/webhook/{provider}</c> (lower case).</summary>
    string Name { get; }

    /// <summary>Starts a hosted checkout. Throws <c>PROVIDER_ERROR</c> (502) when the provider fails.</summary>
    Task<CheckoutResult> CreateCheckoutAsync(Payment payment, string returnUrl, PaymentCustomer customer, CancellationToken ct = default);

    /// <summary>Verifies the signature and extracts the outcome. Throws <see cref="WebhookSignatureException"/> when it does not verify.</summary>
    Task<WebhookEvent> ParseWebhookAsync(WebhookRequest request, CancellationToken ct = default);

    /// <summary>Asks the provider to refund a paid payment. Throws <c>PROVIDER_ERROR</c> (502) on transport errors.</summary>
    Task<RefundResult> RefundAsync(Payment payment, CancellationToken ct = default);
}
