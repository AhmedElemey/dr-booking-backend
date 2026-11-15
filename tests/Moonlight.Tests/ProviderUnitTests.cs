using System.Net;
using System.Text;
using System.Text.Json;

using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Holidays;
using Moonlight.Api.Infrastructure.Payments;
using Moonlight.Api.Infrastructure.WhatsApp;

namespace Moonlight.Tests;

public sealed class FakeHandler(Func<HttpRequestMessage, string, (HttpStatusCode, string)> respond) : HttpMessageHandler
{
    public List<(HttpMethod Method, string Url, string Body)> Calls { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
        Calls.Add((request.Method, request.RequestUri!.ToString(), body));
        var (status, text) = respond(request, body);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public class PaymobProviderTests
{
    private const string Secret = "unit-test-hmac-secret";

    private static PaymobPaymentProvider Provider(FakeHandler handler) => new(
        new HttpClient(handler),
        Options.Create(new PaymentsOptions
        {
            Provider = "Paymob",
            Paymob = new PaymobOptions { ApiKey = "api-key", IntegrationId = "4242", IframeId = "777", HmacSecret = Secret },
        }),
        NullLogger<PaymobPaymentProvider>.Instance);

    private static string TransactionJson(bool success = true, string orderId = "99") => $$$"""
        {"id":123456,"pending":false,"amount_cents":30000,"success":{{{(success ? "true" : "false")}}},"is_auth":false,"is_capture":false,
         "is_standalone_payment":true,"is_voided":false,"is_refunded":false,"is_3d_secure":true,"integration_id":4242,
         "has_parent_transaction":false,"order":{"id":{{{orderId}}}},"created_at":"2026-11-14T10:00:00.000000","currency":"EGP",
         "error_occured":false,"owner":55,"source_data":{"type":"card","pan":"2346","sub_type":"MasterCard"}}
        """;

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void Hmac_concatenates_the_documented_fields_in_order_with_lower_case_booleans()
    {
        var tx = Parse(TransactionJson());
        var expectedInput = "30000" + "2026-11-14T10:00:00.000000" + "EGP" + "false" + "false" + "123456" + "4242" + "true" + "false" + "false" + "false" + "true" + "false" + "99" + "55" + "false" + "2346" + "MasterCard" + "card" + "true";
        var expected = Convert.ToHexString(System.Security.Cryptography.HMACSHA512.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(expectedInput))).ToLowerInvariant();

        Assert.Equal(expected, PaymobPaymentProvider.ComputeHmac(tx, Secret));
        Assert.Equal(128, expected.Length);
    }

    [Fact]
    public void Signature_is_valid_only_for_the_right_secret_and_untampered_payload()
    {
        var tx = Parse(TransactionJson());
        var hmac = PaymobPaymentProvider.ComputeHmac(tx, Secret);

        Assert.True(PaymobPaymentProvider.IsValidSignature(tx, Secret, hmac));
        Assert.True(PaymobPaymentProvider.IsValidSignature(tx, Secret, hmac.ToUpperInvariant()));
        Assert.False(PaymobPaymentProvider.IsValidSignature(tx, "other-secret", hmac));
        Assert.False(PaymobPaymentProvider.IsValidSignature(Parse(TransactionJson(success: false)), Secret, hmac));
        Assert.False(PaymobPaymentProvider.IsValidSignature(tx, Secret, null));
        Assert.False(PaymobPaymentProvider.IsValidSignature(tx, Secret, ""));
        Assert.False(PaymobPaymentProvider.IsValidSignature(tx, "", hmac));
    }

    [Fact]
    public async Task Webhook_with_a_bad_or_missing_hmac_is_rejected()
    {
        var provider = Provider(new FakeHandler((_, _) => (HttpStatusCode.OK, "{}")));
        var body = """{"type":"TRANSACTION","obj":""" + TransactionJson() + "}";

        await Assert.ThrowsAsync<WebhookSignatureException>(() => provider.ParseWebhookAsync(new WebhookRequest(body, new Dictionary<string, string> { ["hmac"] = "deadbeef" })));
        await Assert.ThrowsAsync<WebhookSignatureException>(() => provider.ParseWebhookAsync(new WebhookRequest(body, new Dictionary<string, string>())));
        await Assert.ThrowsAsync<WebhookSignatureException>(() => provider.ParseWebhookAsync(new WebhookRequest("not json", new Dictionary<string, string>())));
    }

    [Fact]
    public async Task Webhook_with_a_valid_hmac_yields_the_order_and_transaction()
    {
        var provider = Provider(new FakeHandler((_, _) => (HttpStatusCode.OK, "{}")));
        var hmac = PaymobPaymentProvider.ComputeHmac(Parse(TransactionJson()), Secret);
        var body = """{"type":"TRANSACTION","obj":""" + TransactionJson() + "}";

        var evt = await provider.ParseWebhookAsync(new WebhookRequest(body, new Dictionary<string, string> { ["hmac"] = hmac }));

        Assert.Equal(WebhookKind.Payment, evt.Kind);
        Assert.Equal("99", evt.ProviderRef);
        Assert.Equal("123456", evt.TransactionId);
        Assert.True(evt.Succeeded);
    }

    [Fact]
    public async Task Checkout_runs_auth_then_order_then_payment_key_and_builds_the_iframe_url()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath switch
        {
            "/api/auth/tokens" => (HttpStatusCode.Created, """{"token":"auth-tok"}"""),
            "/api/ecommerce/orders" => (HttpStatusCode.Created, """{"id":99}"""),
            "/api/acceptance/payment_keys" => (HttpStatusCode.Created, """{"token":"pay tok"}"""),
            _ => (HttpStatusCode.NotFound, "{}"),
        });
        var payment = new Payment { Provider = "paymob", AmountEgp = 285.5m };

        var result = await Provider(handler).CreateCheckoutAsync(payment, "https://x.test/back", new PaymentCustomer("Nour Hassan", "+201012345678"));

        Assert.Equal(["/api/auth/tokens", "/api/ecommerce/orders", "/api/acceptance/payment_keys"], handler.Calls.Select(c => new Uri(c.Url).AbsolutePath));
        Assert.All(handler.Calls, c => Assert.Equal(HttpMethod.Post, c.Method));

        using var auth = JsonDocument.Parse(handler.Calls[0].Body);
        Assert.Equal("api-key", auth.RootElement.GetProperty("api_key").GetString());

        using var order = JsonDocument.Parse(handler.Calls[1].Body);
        Assert.Equal("auth-tok", order.RootElement.GetProperty("auth_token").GetString());
        Assert.Equal(28550, order.RootElement.GetProperty("amount_cents").GetInt64());
        Assert.Equal("EGP", order.RootElement.GetProperty("currency").GetString());
        Assert.Equal(payment.Id.ToString(), order.RootElement.GetProperty("merchant_order_id").GetString());

        using var key = JsonDocument.Parse(handler.Calls[2].Body);
        Assert.Equal(99, key.RootElement.GetProperty("order_id").GetInt64());
        Assert.Equal(4242, key.RootElement.GetProperty("integration_id").GetInt64());
        Assert.Equal(28550, key.RootElement.GetProperty("amount_cents").GetInt64());
        Assert.Equal("Nour", key.RootElement.GetProperty("billing_data").GetProperty("first_name").GetString());
        Assert.Equal("Hassan", key.RootElement.GetProperty("billing_data").GetProperty("last_name").GetString());
        Assert.Equal("+201012345678", key.RootElement.GetProperty("billing_data").GetProperty("phone_number").GetString());

        Assert.Equal("https://accept.paymob.com/api/acceptance/iframes/777?payment_token=pay%20tok", result.CheckoutUrl);
        Assert.Equal("99", result.ProviderRef);
    }

    [Fact]
    public async Task Checkout_failure_becomes_a_provider_error()
    {
        var handler = new FakeHandler((_, _) => (HttpStatusCode.InternalServerError, "{}"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => Provider(handler).CreateCheckoutAsync(new Payment { Provider = "paymob", AmountEgp = 10 }, "x", new PaymentCustomer("A", "+20")));

        Assert.Equal(ErrorCodes.ProviderError, ex.Code);
    }

    [Fact]
    public async Task Refund_posts_the_transaction_id_and_amount_in_cents()
    {
        var handler = new FakeHandler((request, _) => request.RequestUri!.AbsolutePath == "/api/auth/tokens"
            ? (HttpStatusCode.Created, """{"token":"auth-tok"}""")
            : (HttpStatusCode.Created, """{"id":777,"success":true}"""));
        var payment = new Payment { Provider = "paymob", AmountEgp = 300, ProviderTransactionId = "123456" };

        var result = await Provider(handler).RefundAsync(payment);

        Assert.True(result.Succeeded);
        Assert.Equal("/api/acceptance/void_refund/refund", new Uri(handler.Calls[1].Url).AbsolutePath);
        using var body = JsonDocument.Parse(handler.Calls[1].Body);
        Assert.Equal(123456, body.RootElement.GetProperty("transaction_id").GetInt64());
        Assert.Equal(30000, body.RootElement.GetProperty("amount_cents").GetInt64());
        Assert.Equal("auth-tok", body.RootElement.GetProperty("auth_token").GetString());
    }

    [Theory]
    [InlineData(300, 30000)]
    [InlineData(99.99, 9999)]
    [InlineData(0.005, 1)]
    public void Amounts_convert_to_cents(double egp, long cents) => Assert.Equal(cents, PaymobPaymentProvider.ToCents((decimal)egp));
}

public class MetaWhatsAppTests
{
    [Fact]
    public async Task Send_posts_a_template_message_with_a_bearer_token()
    {
        HttpRequestMessage? seen = null;
        var handler = new FakeHandler((request, _) =>
        {
            seen = request;
            return (HttpStatusCode.OK, """{"messages":[{"id":"wamid.1"}]}""");
        });
        var options = Options.Create(new WhatsAppOptions { Provider = "Meta", PhoneNumberId = "1555", AccessToken = "tok" });
        var sender = new MetaWhatsAppSender(new HttpClient(handler), options, NullLogger<MetaWhatsAppSender>.Instance);

        var result = await sender.SendAsync("+201012345678", "booking_confirmed", "en", ["Nour", "Dr Sara"]);

        Assert.Equal("wamid.1", result.ProviderRef);
        Assert.Equal("https://graph.facebook.com/v21.0/1555/messages", handler.Calls[0].Url);
        Assert.Equal("Bearer tok", seen!.Headers.Authorization!.ToString());
        using var body = JsonDocument.Parse(handler.Calls[0].Body);
        var root = body.RootElement;
        Assert.Equal("whatsapp", root.GetProperty("messaging_product").GetString());
        Assert.Equal("201012345678", root.GetProperty("to").GetString());
        Assert.Equal("booking_confirmed", root.GetProperty("template").GetProperty("name").GetString());
        Assert.Equal("en", root.GetProperty("template").GetProperty("language").GetProperty("code").GetString());
        var parameters = root.GetProperty("template").GetProperty("components")[0].GetProperty("parameters");
        Assert.Equal("Nour", parameters[0].GetProperty("text").GetString());
        Assert.Equal("Dr Sara", parameters[1].GetProperty("text").GetString());
    }
}

public class EgyptHolidaysTests
{
    [Theory]
    [InlineData(2026)]
    [InlineData(2027)]
    [InlineData(2028)]
    public void Years_in_the_table_have_fixed_and_moveable_holidays(int year)
    {
        var holidays = EgyptHolidays.ForYear(year);

        Assert.True(EgyptHolidays.HasMoveableData(year));
        Assert.All(holidays, h => Assert.Equal(year, h.Date.Year));
        Assert.Contains(holidays, h => h.Date == new DateOnly(year, 1, 7));
        Assert.Contains(holidays, h => h.Date == new DateOnly(year, 10, 6));
        Assert.True(holidays.Count > 12);
        Assert.All(holidays, h => Assert.False(string.IsNullOrWhiteSpace(h.NameAr) || string.IsNullOrWhiteSpace(h.NameEn)));
    }

    [Fact]
    public void Years_outside_the_table_only_get_fixed_holidays()
    {
        Assert.False(EgyptHolidays.HasMoveableData(2030));
        Assert.NotEmpty(EgyptHolidays.ForYear(2030));
    }
}
