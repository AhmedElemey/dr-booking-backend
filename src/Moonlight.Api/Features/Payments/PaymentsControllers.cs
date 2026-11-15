using System.Net.Mime;
using System.Text;
using System.Text.Json;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure.Payments;

namespace Moonlight.Api.Features.Payments;

[ApiController]
[RequireFeature(FeatureKeys.OnlinePayments)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
public class PaymentsController(PaymentService payments) : ControllerBase
{
    /// <summary>Start an online payment for a Confirmed, upcoming, unpaid appointment with an amount due. Answers the hosted checkout URL.</summary>
    /// <remarks><c>returnUrl</c> must be on the allow-list (<c>Payments:ReturnUrlHosts</c>, the clinic website domain, or the app scheme). Errors: PAYMENT_NOT_ALLOWED, PROVIDER_ERROR.</remarks>
    [HttpPost("api/v1/appointments/{id:guid}/pay")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(typeof(PayResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status502BadGateway)]
    public Task<PayResultDto> Pay(Guid id, PayRequestDto request, CancellationToken ct) => payments.PayAsync(id, request, ct);

    /// <summary>The caller's payment (poll this after returning from the checkout).</summary>
    [HttpGet("api/v1/payments/{id:guid}")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(typeof(PaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<PaymentDto> Get(Guid id, CancellationToken ct) => payments.GetAsync(id, ct);

    /// <summary>Provider callback. The signature is verified (Paymob: HMAC-SHA512 in the <c>hmac</c> query parameter) and the call is idempotent. 401 when the signature is wrong.</summary>
    [HttpPost("api/v1/payments/webhook/{provider}")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(WebhookResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<WebhookResultDto> Webhook(string provider, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        var query = Request.Query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        try
        {
            return await payments.HandleWebhookAsync(provider, new WebhookRequest(body, query), ct);
        }
        catch (WebhookSignatureException)
        {
            throw new ApiException(ErrorCodes.Unauthorized);
        }
    }

    /// <summary>Browser landing of the provider's response callback (configure it in the Paymob dashboard): redirects to the client's returnUrl with paymentId and status (paid, pending, failed).</summary>
    [HttpGet("api/v1/payments/return")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status302Found)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Return(string? merchant_order_id, CancellationToken ct)
    {
        if (!Guid.TryParse(merchant_order_id, out var paymentId) || await payments.ReturnRedirectAsync(paymentId, ct) is not { } url)
        {
            throw ApiException.NotFound();
        }

        return Redirect(url);
    }

    /// <summary>The Mock provider's checkout page: "Pay" and "Fail" buttons that call the mock webhook and redirect to returnUrl (404 for other providers).</summary>
    [HttpGet("api/v1/payments/mock-checkout/{paymentId:guid}")]
    [AllowAnonymous]
    [Produces("text/html")]
    [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MockCheckout(Guid paymentId, CancellationToken ct)
    {
        var payment = payments.ProviderName == "mock" ? await payments.FindForCheckoutAsync(paymentId, ct) : null;
        if (payment is null)
        {
            throw ApiException.NotFound();
        }

        return Content(MockCheckoutPage.Render(payment, Language.FromRequest(Request)), "text/html; charset=utf-8");
    }
}

[ApiController]
[Route("api/v1/dashboard")]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.OnlinePayments)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardPaymentsController(PaymentService payments) : ControllerBase
{
    /// <summary>Payments of the caller's scope (newest first, at most 1000). <c>from</c>/<c>to</c> are clinic-local dates of the payment's creation.</summary>
    [HttpGet("payments")]
    [ProducesResponseType(typeof(List<DashboardPaymentDto>), StatusCodes.Status200OK)]
    public Task<List<DashboardPaymentDto>> List(DateOnly? from, DateOnly? to, PaymentStatus? status, Guid? doctorId, CancellationToken ct) =>
        payments.DashboardListAsync(from, to, status, doctorId, ct);

    /// <summary>Record a cash payment for the appointment's amount due.</summary>
    [HttpPost("appointments/{id:guid}/mark-paid")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> MarkPaid(Guid id, CancellationToken ct) => payments.MarkPaidAsync(id, ct);

    /// <summary>Refund a Paid payment (online ones go through the provider; PROVIDER_ERROR when it fails). Staff only.</summary>
    [HttpPost("payments/{id:guid}/refund")]
    [Authorize(Roles = Roles.ClinicStaff)]
    [ProducesResponseType(typeof(DashboardPaymentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status502BadGateway)]
    public Task<DashboardPaymentDto> Refund(Guid id, CancellationToken ct) => payments.RefundAsync(id, ct);
}

internal static class MockCheckoutPage
{
    public static string Render(Payment payment, string language)
    {
        var ar = language != "en";
        var title = ar ? "الدفع التجريبي" : "Mock payment";
        var amount = ar ? $"المبلغ: {payment.AmountEgp:0.00} جنيه" : $"Amount: EGP {payment.AmountEgp:0.00}";
        var note = ar ? "صفحة دفع تجريبية للتطوير فقط." : "A development-only checkout page.";
        var pay = ar ? "ادفع" : "Pay";
        var fail = ar ? "فشل الدفع" : "Fail";
        var done = ar ? "تمت معالجة هذا الدفع بالفعل." : "This payment was already processed.";
        var state = JsonSerializer.Serialize(new { id = payment.Id, providerRef = payment.ProviderRef ?? payment.Id.ToString(), returnUrl = payment.ReturnUrl ?? "" })
            .Replace("<", "\\u003c", StringComparison.Ordinal);
        var pending = payment.Status == PaymentStatus.Pending;
        var buttons = pending ? $"<button id='pay'>{pay}</button><button id='fail'>{fail}</button>" : "";
        var message = pending ? note : done;
        var lang = ar ? "ar" : "en";
        var dir = ar ? "rtl" : "ltr";
        return $$"""
            <!doctype html>
            <html lang="{{lang}}" dir="{{dir}}">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{title}}</title>
            <style>
            body{font-family:system-ui,sans-serif;background:#f4f5fb;margin:0;display:grid;place-items:center;min-height:100vh}
            main{background:#fff;border-radius:16px;padding:32px;max-width:360px;width:calc(100% - 32px);box-shadow:0 8px 30px rgba(0,0,0,.08);text-align:center}
            button{font:inherit;font-weight:600;border:0;border-radius:10px;padding:12px 20px;margin:6px;cursor:pointer;min-width:120px}
            #pay{background:#0e9f9a;color:#fff}#fail{background:#e5e7eb;color:#111}
            p{color:#555}
            </style>
            </head>
            <body>
            <main>
            <h1>{{title}}</h1>
            <p>{{amount}}</p>
            <p>{{message}}</p>
            {{buttons}}
            </main>
            <script>
            const state = {{state}};
            async function finish(success) {
              await fetch('/api/v1/payments/webhook/mock', { method: 'POST', headers: { 'content-type': 'application/json' }, body: JSON.stringify({ providerRef: state.providerRef, success }) });
              const sep = state.returnUrl.includes('?') ? '&' : '?';
              window.location.href = state.returnUrl + sep + 'paymentId=' + state.id + '&status=' + (success ? 'paid' : 'failed');
            }
            document.getElementById('pay')?.addEventListener('click', () => finish(true));
            document.getElementById('fail')?.addEventListener('click', () => finish(false));
            </script>
            </body>
            </html>
            """;
    }
}
