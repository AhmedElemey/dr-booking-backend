using System.Globalization;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure.Payments;

// Paymob Accept (https://docs.paymob.com): authentication token -> order registration -> payment key -> hosted iframe.
// Transaction callbacks are verified with HMAC-SHA512 over a fixed list of fields; refunds use void_refund/refund.
public sealed class PaymobPaymentProvider(HttpClient http, IOptions<PaymentsOptions> options, ILogger<PaymobPaymentProvider> logger) : IPaymentProvider
{
    // Paymob's documented order for the transaction processed callback: keys sorted lexicographically.
    public static readonly string[] HmacFields =
    [
        "amount_cents",
        "created_at",
        "currency",
        "error_occured",
        "has_parent_transaction",
        "id",
        "integration_id",
        "is_3d_secure",
        "is_auth",
        "is_capture",
        "is_refunded",
        "is_standalone_payment",
        "is_voided",
        "order.id",
        "owner",
        "pending",
        "source_data.pan",
        "source_data.sub_type",
        "source_data.type",
        "success",
    ];

    private PaymobOptions Settings => options.Value.Paymob;

    public string Name => "paymob";

    public static long ToCents(decimal amountEgp) => (long)Math.Round(amountEgp * 100m, 0, MidpointRounding.AwayFromZero);

    public async Task<CheckoutResult> CreateCheckoutAsync(Payment payment, string returnUrl, PaymentCustomer customer, CancellationToken ct = default)
    {
        var cents = ToCents(payment.AmountEgp);
        var authToken = await AuthenticateAsync(ct);

        var order = await PostAsync("/api/ecommerce/orders", new
        {
            auth_token = authToken,
            delivery_needed = false,
            amount_cents = cents,
            currency = "EGP",
            merchant_order_id = payment.Id.ToString(),
            items = Array.Empty<object>(),
        }, ct);
        var orderId = Prop(order, "id").GetInt64();

        var (first, last) = SplitName(customer.Name);
        var key = await PostAsync("/api/acceptance/payment_keys", new
        {
            auth_token = authToken,
            amount_cents = cents,
            expiration = 3600,
            order_id = orderId,
            currency = "EGP",
            integration_id = long.Parse(Settings.IntegrationId, CultureInfo.InvariantCulture),
            billing_data = new
            {
                apartment = "NA",
                email = "NA",
                floor = "NA",
                first_name = first,
                street = "NA",
                building = "NA",
                phone_number = customer.Phone,
                shipping_method = "NA",
                postal_code = "NA",
                city = "NA",
                country = "EG",
                last_name = last,
                state = "NA",
            },
        }, ct);
        var paymentToken = Prop(key, "token").GetString() ?? throw ProviderError("Paymob returned no payment token.");

        var url = $"{Settings.BaseUrl.TrimEnd('/')}/api/acceptance/iframes/{Uri.EscapeDataString(Settings.IframeId)}?payment_token={Uri.EscapeDataString(paymentToken)}";
        return new CheckoutResult(url, orderId.ToString(CultureInfo.InvariantCulture));
    }

    public Task<WebhookEvent> ParseWebhookAsync(WebhookRequest request, CancellationToken ct = default)
    {
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(request.Body).RootElement;
        }
        catch (JsonException)
        {
            throw new WebhookSignatureException("The body is not valid JSON.");
        }

        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("obj", out var obj) || obj.ValueKind != JsonValueKind.Object)
        {
            throw new WebhookSignatureException("The callback has no transaction object.");
        }

        request.Query.TryGetValue("hmac", out var received);
        if (!IsValidSignature(obj, Settings.HmacSecret, received))
        {
            throw new WebhookSignatureException("The HMAC signature does not match.");
        }

        var type = root.TryGetProperty("type", out var t) ? t.GetString() : "TRANSACTION";
        if (!string.Equals(type, "TRANSACTION", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(new WebhookEvent(WebhookKind.Ignored, "", null, false, request.Body));
        }

        var orderId = obj.TryGetProperty("order", out var order) && order.TryGetProperty("id", out var oid) ? Scalar(oid) : "";
        var transactionId = obj.TryGetProperty("id", out var id) ? Scalar(id) : null;
        var refunded = Flag(obj, "is_refunded");
        var voided = Flag(obj, "is_voided");
        if (refunded || voided)
        {
            return Task.FromResult(new WebhookEvent(WebhookKind.Refund, orderId, transactionId, Flag(obj, "success"), request.Body));
        }

        var succeeded = Flag(obj, "success") && !Flag(obj, "pending");
        return Task.FromResult(new WebhookEvent(WebhookKind.Payment, orderId, transactionId, succeeded, request.Body));
    }

    public async Task<RefundResult> RefundAsync(Payment payment, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payment.ProviderTransactionId))
        {
            throw ProviderError("The payment has no provider transaction to refund.");
        }

        var authToken = await AuthenticateAsync(ct);
        var response = await PostAsync("/api/acceptance/void_refund/refund", new
        {
            auth_token = authToken,
            transaction_id = long.Parse(payment.ProviderTransactionId, CultureInfo.InvariantCulture),
            amount_cents = ToCents(payment.AmountEgp),
        }, ct, throwOnFailure: false);

        var succeeded = response.ValueKind == JsonValueKind.Object && (!response.TryGetProperty("success", out _) || Flag(response, "success"));
        var reference = response.ValueKind == JsonValueKind.Object && response.TryGetProperty("id", out var id) ? Scalar(id) : null;
        return new RefundResult(succeeded, reference);
    }

    /// <summary>HMAC-SHA512 (hex) over the concatenated callback fields in <see cref="HmacFields"/> order.</summary>
    public static string ComputeHmac(JsonElement transaction, string secret)
    {
        var builder = new StringBuilder();
        foreach (var field in HmacFields)
        {
            builder.Append(FieldValue(transaction, field));
        }

        var hash = HMACSHA512.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(builder.ToString()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static bool IsValidSignature(JsonElement transaction, string secret, string? received)
    {
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(received))
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(ComputeHmac(transaction, secret));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(received.Trim().ToLowerInvariant()));
    }

    // "order.id" walks into the nested object; booleans are lower-case; missing or null values are empty.
    private static string FieldValue(JsonElement element, string path)
    {
        foreach (var part in path.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(part, out element))
            {
                return "";
            }
        }

        return Scalar(element);
    }

    private static string Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        JsonValueKind.Number => value.GetRawText(),
        _ => "",
    };

    private static bool Flag(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && (value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.String && value.GetString() == "true"));

    private static (string First, string Last) SplitName(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            0 => ("NA", "NA"),
            1 => (parts[0], "NA"),
            _ => (parts[0], string.Join(' ', parts.Skip(1))),
        };
    }

    private async Task<string> AuthenticateAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Settings.ApiKey))
        {
            throw ProviderError("Payments:Paymob:ApiKey is not configured.");
        }

        var response = await PostAsync("/api/auth/tokens", new { api_key = Settings.ApiKey }, ct);
        return Prop(response, "token").GetString() ?? throw ProviderError("Paymob returned no auth token.");
    }

    private async Task<JsonElement> PostAsync(string path, object body, CancellationToken ct, bool throwOnFailure = true)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(Settings.BaseUrl.TrimEnd('/') + path, body, ct);
            var text = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Paymob {Path} answered {Status}.", path, (int)response.StatusCode);
                if (throwOnFailure)
                {
                    throw ProviderError($"Paymob answered {(int)response.StatusCode}.");
                }

                return default;
            }

            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested)
            {
                throw;
            }

            logger.LogWarning(ex, "Paymob {Path} failed.", path);
            throw ProviderError("Paymob is unreachable or answered with an unexpected body.");
        }
    }

    private static JsonElement Prop(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            ? value
            : throw ProviderError($"Paymob response is missing '{name}'.");

    private static ApiException ProviderError(string detail) => new(ErrorCodes.ProviderError, detail);
}
