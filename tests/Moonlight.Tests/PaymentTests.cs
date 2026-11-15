using System.Net;
using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure.Payments;

namespace Moonlight.Tests;

public class MockPaymentTests(PostgresFixture db) : Phase2TestBase(db, new Dictionary<string, string?> { ["Payments:ReturnUrlHosts:0"] = "shop.example.test" })
{
    private const string ReturnUrl = "https://shop.example.test/ar/appointments/1";

    private async Task<(Guid Appointment, Session Patient)> BookedAsync()
    {
        var patient = await World.NewPatientAsync();
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id });
        Assert.Equal(201, response.StatusCode);
        return (response.Id, patient);
    }

    private Task<Resp> WebhookAsync(Guid paymentId, bool success) =>
        World.Anonymous().PostAsync("/api/v1/payments/webhook/mock", new { providerRef = paymentId.ToString(), success });

    private Task<Payment> PaymentAsync(Guid id) => World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.Id == id));

    [Fact]
    public async Task Pay_returns_a_mock_checkout_and_the_webhook_marks_it_paid_idempotently()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();

        var before = await patient.GetAsync(Patient(id));
        Assert.Equal(300m, before["priceEgp"].GetDecimal());
        Assert.Equal(300m, before["totalEgp"].GetDecimal());
        Assert.Equal("Unpaid", before["paymentStatus"].GetString());

        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(200, pay.StatusCode);
        var paymentId = pay["paymentId"].GetGuid();
        Assert.EndsWith($"/api/v1/payments/mock-checkout/{paymentId}", pay["checkoutUrl"].GetString());
        Assert.Equal("Pending", (await patient.GetAsync(Patient(id)))["paymentStatus"].GetString());

        var first = await WebhookAsync(paymentId, true);
        var paidAt = (await PaymentAsync(paymentId)).PaidAt;
        Factory.Clock.Advance(TimeSpan.FromMinutes(5));
        var replay = await WebhookAsync(paymentId, true);
        var late = await WebhookAsync(paymentId, false); // out-of-order failure never un-pays

        Assert.Equal(200, first.StatusCode);
        Assert.Equal(200, replay.StatusCode);
        Assert.Equal(200, late.StatusCode);
        var payment = await PaymentAsync(paymentId);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(paidAt, payment.PaidAt);
        Assert.Single(await World.WithDbAsync(d => d.Payments.Where(p => p.AppointmentId == id).ToListAsync()));
        Assert.Equal("Paid", (await patient.GetAsync(Patient(id)))["paymentStatus"].GetString());
        Assert.Equal("Paid", (await patient.GetAsync($"/api/v1/payments/{paymentId}"))["status"].GetString());

        // Paying an already paid appointment is refused.
        var again = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(409, again.StatusCode);
        Assert.Equal("PAYMENT_NOT_ALLOWED", again.Code);
    }

    [Fact]
    public async Task Pay_validates_the_return_url_and_the_appointment_state()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();

        Assert.Equal(400, (await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = "https://evil.test/x" })).StatusCode);
        Assert.Equal(400, (await patient.PostAsync($"{Patient(id)}/pay", new { })).StatusCode);

        await patient.PostAsync($"{Patient(id)}/cancel");
        var cancelled = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal("PAYMENT_NOT_ALLOWED", cancelled.Code);

        var other = await World.NewPatientAsync();
        Assert.Equal(404, (await other.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl })).StatusCode);
    }

    [Fact]
    public async Task Mock_checkout_page_is_served_and_failed_payments_can_be_retried()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        var paymentId = pay["paymentId"].GetGuid();

        var page = await Factory.CreateClient().GetAsync($"/api/v1/payments/mock-checkout/{paymentId}");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("text/html", page.Content.Headers.ContentType!.ToString());
        Assert.Contains("/api/v1/payments/webhook/mock", html);

        await WebhookAsync(paymentId, false);
        Assert.Equal(PaymentStatus.Failed, (await PaymentAsync(paymentId)).Status);
        Assert.Equal("Unpaid", (await patient.GetAsync(Patient(id)))["paymentStatus"].GetString());

        var retry = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(200, retry.StatusCode);
        Assert.Equal(404, (await World.Anonymous().GetAsync($"/api/v1/payments/mock-checkout/{Guid.NewGuid()}")).StatusCode);
    }

    [Fact]
    public async Task Webhook_for_another_provider_is_404()
    {
        Enable(FeatureKeys.OnlinePayments);
        var response = await World.Anonymous().PostAsync("/api/v1/payments/webhook/paymob", new { });
        Assert.Equal(404, response.StatusCode);
    }

    [Fact]
    public async Task Patient_cancelling_a_paid_appointment_refunds_it()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var paymentId = (await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl }))["paymentId"].GetGuid();
        await WebhookAsync(paymentId, true);

        var cancelled = await patient.PostAsync($"{Patient(id)}/cancel");

        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal("Refunded", cancelled["paymentStatus"].GetString());
        var payment = await PaymentAsync(paymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.NotNull(payment.RefundedAt);
    }

    [Fact]
    public async Task Staff_mark_paid_cash_list_filter_and_manual_refund()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();

        var cash = await Staff.PostAsync($"{Dash(id)}/mark-paid");
        Assert.Equal(200, cash.StatusCode);
        Assert.Equal("Paid", cash["paymentStatus"].GetString());
        var again = await Staff.PostAsync($"{Dash(id)}/mark-paid");
        Assert.Equal("PAYMENT_NOT_ALLOWED", again.Code);

        var list = await Staff.GetAsync("/api/v1/dashboard/payments?status=Paid");
        var item = Assert.Single(list.Body.EnumerateArray());
        Assert.Equal("Cash", item.GetProperty("method").GetString());
        Assert.Empty((await Staff.GetAsync("/api/v1/dashboard/payments?status=Refunded")).Body.EnumerateArray());

        var refund = await Staff.PostAsync($"/api/v1/dashboard/payments/{item.GetProperty("id").GetGuid()}/refund");
        Assert.Equal(200, refund.StatusCode);
        Assert.Equal("Refunded", refund["status"].GetString());

        // another clinic's staff cannot see it
        var other = await World.LoginAsync(await World.NewStaffAsync(await World.NewClinicAsync("Other")));
        Assert.Empty((await other.GetAsync("/api/v1/dashboard/payments")).Body.EnumerateArray());
    }
}

public class PaymobWebhookTests(PostgresFixture db) : Phase2TestBase(db, new Dictionary<string, string?>
{
    ["Payments:Provider"] = "Paymob",
    ["Payments:ReturnUrlHosts:0"] = "shop.example.test",
    ["Payments:Paymob:ApiKey"] = "k",
    ["Payments:Paymob:IntegrationId"] = "4242",
    ["Payments:Paymob:IframeId"] = "777",
    ["Payments:Paymob:HmacSecret"] = Secret,
})
{
    private const string Secret = "integration-hmac-secret";

    private bool _failing;

    private FakeHandler? _handler;

    private FakeHandler _paymob => _handler ??= new FakeHandler((request, _) => Respond(request));

    private async Task<Guid> BookWithServiceAsync(Session patient)
    {
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id });
        Assert.Equal(201, response.StatusCode);
        return response.Id;
    }

    private (HttpStatusCode, string) Respond(HttpRequestMessage request) => _failing ? (HttpStatusCode.InternalServerError, "{}") : request.RequestUri!.AbsolutePath switch
    {
        "/api/auth/tokens" => (HttpStatusCode.Created, """{"token":"t"}"""),
        "/api/ecommerce/orders" => (HttpStatusCode.Created, """{"id":5150}"""),
        "/api/acceptance/payment_keys" => (HttpStatusCode.Created, """{"token":"pk"}"""),
        "/api/acceptance/void_refund/refund" => (HttpStatusCode.Created, """{"id":9,"success":true}"""),
        _ => (HttpStatusCode.NotFound, "{}"),
    };

    protected override Action<IServiceCollection>? ConfigureServices => services =>
        services.AddHttpClient<IPaymentProvider, PaymobPaymentProvider>().ConfigurePrimaryHttpMessageHandler(() => _paymob);

    private static string TransactionJson(bool success) => $$$"""
        {"id":8800,"pending":false,"amount_cents":30000,"success":{{{(success ? "true" : "false")}}},"is_auth":false,"is_capture":false,
         "is_standalone_payment":true,"is_voided":false,"is_refunded":false,"is_3d_secure":true,"integration_id":4242,
         "has_parent_transaction":false,"order":{"id":5150},"created_at":"2026-11-14T10:00:00","currency":"EGP",
         "error_occured":false,"owner":55,"source_data":{"type":"card","pan":"2346","sub_type":"Visa"}}
        """;

    private async Task<Resp> PostWebhookAsync(string transaction, string? hmac)
    {
        var url = "/api/v1/payments/webhook/paymob" + (hmac is null ? "" : "?hmac=" + hmac);
        return await World.Anonymous().PostAsync(url, JsonDocument.Parse("""{"type":"TRANSACTION","obj":""" + transaction + "}").RootElement);
    }

    private static string Sign(string transaction) => PaymobPaymentProvider.ComputeHmac(JsonDocument.Parse(transaction).RootElement, Secret);

    [Fact]
    public async Task Hmac_verified_webhook_is_idempotent_and_a_bad_signature_is_rejected_without_changes()
    {
        Enable(FeatureKeys.OnlinePayments);
        var patient = await World.NewPatientAsync();
        var id = await BookWithServiceAsync(patient);
        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = "https://shop.example.test/back" });
        Assert.Equal(200, pay.StatusCode);
        Assert.StartsWith("https://accept.paymob.com/api/acceptance/iframes/777?payment_token=pk", pay["checkoutUrl"].GetString());
        var paymentId = pay["paymentId"].GetGuid();
        var success = TransactionJson(true);

        var wrong = await PostWebhookAsync(success, "00" + Sign(success)[2..]);
        var missing = await PostWebhookAsync(success, null);
        var tamperedAmount = await PostWebhookAsync(success.Replace("30000", "100"), Sign(success));
        Assert.All([wrong, missing, tamperedAmount], r => Assert.Equal(401, r.StatusCode));
        Assert.Equal(PaymentStatus.Pending, (await World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId))).Status);

        var ok = await PostWebhookAsync(success, Sign(success));
        var replay = await PostWebhookAsync(success, Sign(success));
        var lateFailure = TransactionJson(false);
        var late = await PostWebhookAsync(lateFailure, Sign(lateFailure));

        Assert.All([ok, replay, late], r => Assert.Equal(200, r.StatusCode));
        var payment = await World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId));
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal("8800", payment.ProviderTransactionId);
        Assert.Equal("5150", payment.ProviderRef);

        // cancelling refunds through Paymob using the stored transaction id
        var cancelled = await patient.PostAsync($"{Patient(id)}/cancel");
        Assert.Equal("Refunded", cancelled["paymentStatus"].GetString());
        Assert.Contains(_paymob.Calls, c => c.Url.EndsWith("/api/acceptance/void_refund/refund") && c.Body.Contains("8800"));
    }

    [Fact]
    public async Task Provider_failure_is_a_502_and_the_attempt_is_marked_failed()
    {
        Enable(FeatureKeys.OnlinePayments);
        var patient = await World.NewPatientAsync();
        var id = await BookWithServiceAsync(patient);
        _failing = true;

        var response = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = "https://shop.example.test/back" });

        Assert.Equal(502, response.StatusCode);
        Assert.Equal("PROVIDER_ERROR", response.Code);
        Assert.Equal(PaymentStatus.Failed, (await World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.AppointmentId == id))).Status);
    }
}
