using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Promotions;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Payments;

namespace Moonlight.Api.Features.Payments;

public sealed record PayRequestDto(string? ReturnUrl);

public sealed record PayResultDto(Guid PaymentId, string CheckoutUrl);

public sealed record PaymentDto(Guid Id, Guid AppointmentId, decimal AmountEgp, PaymentStatus Status);

public sealed record DashboardPaymentDto(
    Guid Id,
    Guid AppointmentId,
    decimal AmountEgp,
    PaymentStatus Status,
    PaymentMethod Method,
    string Provider,
    DateTime CreatedAt,
    DateTime? PaidAt,
    DateTime? RefundedAt,
    DateTime StartsAt,
    PatientRefDto Patient,
    DashboardDoctorRefDto Doctor);

public sealed record WebhookResultDto(bool Handled);

// Online payments (spec section 6): checkout creation, signature-verified idempotent webhooks, cash and refunds.
public sealed class PaymentService(
    MoonlightDbContext db,
    CurrentUser current,
    DashboardScope scope,
    AppointmentMapper mapper,
    IPaymentProvider provider,
    IOptions<PaymentsOptions> options,
    ClinicConfig clinic,
    Language lang,
    TimeProvider clock,
    ILogger<PaymentService> logger)
{
    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private const string LiveCheckoutIndex = "ux_payments_live_checkout";

    public string ProviderName => provider.Name;

    // ---- Patient ------------------------------------------------------------------------------------------------

    public async Task<PayResultDto> PayAsync(Guid appointmentId, PayRequestDto request, CancellationToken ct)
    {
        if (provider is UnconfiguredPaymentProvider)
        {
            throw new ApiException(ErrorCodes.PaymentNotAllowed, "Online payments are not configured.");
        }

        new ValidationErrors().RequireText(request.ReturnUrl, "returnUrl", 500).ThrowIfAny();
        var returnUrl = request.ReturnUrl!.Trim();
        if (!IsAllowedReturnUrl(returnUrl))
        {
            throw ApiException.Validation("returnUrl", "returnUrl is not on the allow-list.");
        }

        var userId = current.Id;
        Payment payment;
        Appointment appointment;
        // One live checkout per appointment: the appointment's payment lock serializes concurrent pay calls
        // (a partial unique index on Pending payments is the backstop).
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAppointmentAsync(appointmentId, ct);
            appointment = await AppointmentMapper.WithDetails(db.Appointments)
                .FirstOrDefaultAsync(a => a.Id == appointmentId && a.PatientUserId == userId, ct) ?? throw ApiException.NotFound();
            var total = TotalOf(appointment);
            // With deposits on, the first (and only) online checkout charges the fixed deposit; the rest is settled at the clinic.
            var deposit = DepositPolicy.AmountFor(appointment.Slot?.Doctor?.Clinic, clinic);
            var charge = deposit > 0 ? Math.Min(deposit, total) : total;
            var kind = charge < total ? PaymentKind.Deposit : PaymentKind.Full;
            var payable = appointment.Status == AppointmentStatus.Confirmed
                && appointment.Slot!.StartsAt > Now
                && total > 0
                && !appointment.Payments.Any(p => p.Status is PaymentStatus.Paid or PaymentStatus.Refunding);
            if (!payable)
            {
                throw new ApiException(ErrorCodes.PaymentNotAllowed);
            }

            // An abandoned checkout is replaced by a fresh one. (If the old one is still paid later, the webhook refunds it.)
            foreach (var stale in appointment.Payments.Where(p => p.Status == PaymentStatus.Pending))
            {
                stale.Status = PaymentStatus.Cancelled;
            }

            payment = new Payment
            {
                AppointmentId = appointment.Id,
                AmountEgp = charge,
                Method = PaymentMethod.Online,
                Kind = kind,
                Provider = provider.Name,
                ReturnUrl = returnUrl,
                CreatedAt = Now,
            };
            db.Payments.Add(payment);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation(LiveCheckoutIndex))
            {
                throw new ApiException(ErrorCodes.PaymentNotAllowed);
            }

            await tx.CommitAsync(ct);
        }

        var customer = new PaymentCustomer(appointment.FamilyMember?.FullName ?? appointment.Patient!.FullName ?? "NA", appointment.Patient!.Phone);
        try
        {
            var checkout = await provider.CreateCheckoutAsync(payment, returnUrl, customer, ct);
            payment.ProviderRef = checkout.ProviderRef;
            await db.SaveChangesAsync(ct);
            return new PayResultDto(payment.Id, checkout.CheckoutUrl);
        }
        catch (ApiException)
        {
            payment.Status = PaymentStatus.Failed;
            await db.SaveChangesAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<PaymentDto> GetAsync(Guid id, CancellationToken ct)
    {
        var userId = current.Id;
        var payment = await db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == id && p.Appointment!.PatientUserId == userId, ct) ?? throw ApiException.NotFound();
        return new PaymentDto(payment.Id, payment.AppointmentId, payment.AmountEgp, payment.Status);
    }

    // ---- Webhook ------------------------------------------------------------------------------------------------

    public async Task<WebhookResultDto> HandleWebhookAsync(string providerName, WebhookRequest request, CancellationToken ct)
    {
        if (!string.Equals(providerName, provider.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.NotFound();
        }

        var webhook = await provider.ParseWebhookAsync(request, ct);
        if (webhook.Kind == WebhookKind.Ignored || string.IsNullOrEmpty(webhook.ProviderRef))
        {
            return new WebhookResultDto(false);
        }

        var payment = await db.Payments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Provider == provider.Name && p.ProviderRef == webhook.ProviderRef, ct);
        if (payment is null)
        {
            logger.LogWarning("Payment webhook for unknown reference {Reference}.", webhook.ProviderRef);
            return new WebhookResultDto(false);
        }

        var now = Now;
        if (webhook.Kind == WebhookKind.Refund)
        {
            if (webhook.Succeeded)
            {
                await db.Payments.Where(p => p.Id == payment.Id && (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Refunding))
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Refunded).SetProperty(p => p.RefundedAt, now), ct);
            }

            return new WebhookResultDto(true);
        }

        if (webhook.Succeeded)
        {
            if ((webhook.AmountCents is { } cents && cents != PaymobPaymentProvider.ToCents(payment.AmountEgp))
                || (webhook.Currency is { } currency && !string.Equals(currency, "EGP", StringComparison.OrdinalIgnoreCase)))
            {
                logger.LogError("Payment {PaymentId}: the provider reported a different amount or currency than expected; not applied, check it in the provider dashboard.", payment.Id);
                return new WebhookResultDto(false);
            }

            await ApplySuccessAsync(payment.Id, payment.AppointmentId, webhook.TransactionId, ct);
        }
        else
        {
            await db.Payments.Where(p => p.Id == payment.Id && p.Status == PaymentStatus.Pending)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Failed), ct);
        }

        return new WebhookResultDto(true);
    }

    // A captured payment is always recorded, even when its checkout was cancelled or replaced locally. If the appointment is
    // still valid (not cancelled and not paid otherwise) it becomes Paid; otherwise it is recorded and refunded automatically.
    private async Task ApplySuccessAsync(Guid paymentId, Guid appointmentId, string? transactionId, CancellationToken ct)
    {
        var refundNeeded = false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAppointmentAsync(appointmentId, ct);
            var payment = await db.Payments.FirstAsync(p => p.Id == paymentId, ct);
            if (payment.Status is PaymentStatus.Paid or PaymentStatus.Refunding or PaymentStatus.Refunded)
            {
                return; // replay
            }

            var appointmentStatus = await db.Appointments.AsNoTracking().Where(a => a.Id == appointmentId).Select(a => a.Status).FirstAsync(ct);
            var otherPaid = await db.Payments.AnyAsync(p => p.AppointmentId == appointmentId && p.Id != paymentId
                && (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Refunding), ct);
            refundNeeded = appointmentStatus == AppointmentStatus.Cancelled || otherPaid;
            if (!refundNeeded)
            {
                var others = await db.Payments.Where(p => p.AppointmentId == appointmentId && p.Id != paymentId && p.Status == PaymentStatus.Pending).ToListAsync(ct);
                foreach (var other in others)
                {
                    other.Status = PaymentStatus.Cancelled;
                }
            }

            payment.Status = refundNeeded ? PaymentStatus.Refunding : PaymentStatus.Paid;
            payment.PaidAt = Now;
            payment.ProviderTransactionId = transactionId;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (refundNeeded)
        {
            logger.LogWarning("Payment {PaymentId} was captured for a cancelled or already paid appointment; refunding it.", paymentId);
            await FinishRefundAsync(paymentId);
        }
    }

    // Where the provider's browser redirect lands: the client's returnUrl with the outcome (the client should still GET the payment).
    public async Task<string?> ReturnRedirectAsync(Guid paymentId, CancellationToken ct)
    {
        var payment = await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId, ct);
        return payment?.ReturnUrl is null ? null : WithOutcome(payment.ReturnUrl, payment);
    }

    public static string WithOutcome(string returnUrl, Payment payment)
    {
        var status = payment.Status switch
        {
            PaymentStatus.Paid or PaymentStatus.Refunding => "paid",
            PaymentStatus.Pending => "pending",
            _ => "failed",
        };
        var separator = returnUrl.Contains('?') ? "&" : "?";
        return $"{returnUrl}{separator}paymentId={payment.Id}&status={status}";
    }

    public async Task<Payment?> FindForCheckoutAsync(Guid paymentId, CancellationToken ct) =>
        await db.Payments.AsNoTracking().FirstOrDefaultAsync(p => p.Id == paymentId && p.Provider == provider.Name, ct);

    // ---- Dashboard ----------------------------------------------------------------------------------------------

    public async Task<List<DashboardPaymentDto>> DashboardListAsync(DateOnly? from, DateOnly? to, PaymentStatus? status, Guid? doctorId, CancellationToken ct)
    {
        var appointmentIds = scope.Appointments().Select(a => a.Id);
        var query = db.Payments.AsNoTracking()
            .Include(p => p.Appointment!).ThenInclude(a => a.Slot!).ThenInclude(s => s.Doctor)
            .Include(p => p.Appointment!).ThenInclude(a => a.Patient)
            .Where(p => appointmentIds.Contains(p.AppointmentId));
        if (doctorId is { } d)
        {
            query = query.Where(p => p.Appointment!.Slot!.DoctorId == d);
        }

        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        if (from is not null || to is not null)
        {
            var zone = ClinicTime.FindZone((await scope.RequireClinicAsync(ct)).Timezone);
            if (from is { } f)
            {
                var start = ClinicTime.StartOfDayUtc(f, zone);
                query = query.Where(p => p.CreatedAt >= start);
            }

            if (to is { } t)
            {
                var end = ClinicTime.StartOfDayUtc(t.AddDays(1), zone);
                query = query.Where(p => p.CreatedAt < end);
            }
        }

        var rows = await query.OrderByDescending(p => p.CreatedAt).ThenBy(p => p.Id).Take(1000).ToListAsync(ct);
        return rows.Select(ToDashboard).ToList();
    }

    // Cash at the desk: records a Paid cash payment for the appointment.
    public async Task<DashboardAppointmentDto> MarkPaidAsync(Guid appointmentId, CancellationToken ct)
    {
        var visible = await scope.Appointments().AnyAsync(a => a.Id == appointmentId, ct);
        if (!visible)
        {
            throw ApiException.NotFound();
        }

        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await LockAppointmentAsync(appointmentId, ct);
            var appointment = await AppointmentMapper.WithDetails(scope.Appointments()).FirstOrDefaultAsync(a => a.Id == appointmentId, ct)
                ?? throw ApiException.NotFound();
            var total = TotalOf(appointment);
            // A deposit already paid online is not a blocker: staff record the rest in cash.
            var live = appointment.Payments.Where(p => p.Status is PaymentStatus.Paid or PaymentStatus.Refunding).ToList();
            var depositPaid = live.Where(p => p.Kind == PaymentKind.Deposit).Sum(p => p.AmountEgp);
            var balance = total - depositPaid;
            if (appointment.Status == AppointmentStatus.Cancelled || total <= 0 || live.Any(p => p.Kind != PaymentKind.Deposit) || balance <= 0)
            {
                throw new ApiException(ErrorCodes.PaymentNotAllowed);
            }

            foreach (var pending in appointment.Payments.Where(p => p.Status == PaymentStatus.Pending))
            {
                pending.Status = PaymentStatus.Cancelled;
            }

            var now = Now;
            db.Payments.Add(new Payment
            {
                AppointmentId = appointment.Id,
                AmountEgp = balance,
                Status = PaymentStatus.Paid,
                Method = PaymentMethod.Cash,
                Provider = "cash",
                CreatedAt = now,
                PaidAt = now,
            });
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        var reloaded = await AppointmentMapper.WithDetails(db.Appointments.AsNoTracking()).FirstAsync(a => a.Id == appointmentId, ct);
        return mapper.ToDashboardDto(reloaded);
    }

    public async Task<DashboardPaymentDto> RefundAsync(Guid paymentId, CancellationToken ct)
    {
        var appointmentIds = scope.Appointments().Select(a => a.Id);
        var payment = await db.Payments
            .Include(p => p.Appointment!).ThenInclude(a => a.Slot!).ThenInclude(s => s.Doctor)
            .Include(p => p.Appointment!).ThenInclude(a => a.Patient)
            .FirstOrDefaultAsync(p => p.Id == paymentId && appointmentIds.Contains(p.AppointmentId), ct) ?? throw ApiException.NotFound();

        // Claim first: only the request that moves Paid to Refunding talks to the provider.
        if (!await ClaimRefundAsync(paymentId, ct))
        {
            await db.Entry(payment).ReloadAsync(ct);
            throw new ApiException(ErrorCodes.InvalidTransition, $"Only a Paid payment can be refunded (this one is {payment.Status}).");
        }

        var ok = await FinishRefundAsync(paymentId);
        if (!ok)
        {
            throw new ApiException(ErrorCodes.ProviderError, "The provider did not accept the refund.");
        }

        await db.Entry(payment).ReloadAsync(ct);
        return ToDashboard(payment);
    }

    /// <summary>A cancelled appointment: pending checkouts are cancelled and a paid online payment is refunded. Never throws.</summary>
    public async Task RefundOnCancelAsync(Guid appointmentId, CancellationToken ct)
    {
        try
        {
            List<Guid> toRefund;
            await using (var tx = await db.Database.BeginTransactionAsync(ct))
            {
                await LockAppointmentAsync(appointmentId, ct);
                await db.Payments.Where(p => p.AppointmentId == appointmentId && p.Status == PaymentStatus.Pending)
                    .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Cancelled), ct);
                toRefund = await db.Payments.AsNoTracking()
                    .Where(p => p.AppointmentId == appointmentId && p.Status == PaymentStatus.Paid && p.Method == PaymentMethod.Online) // cash is returned at the desk
                    .Select(p => p.Id).ToListAsync(ct);
                await tx.CommitAsync(ct);
            }

            foreach (var id in toRefund)
            {
                if (await ClaimRefundAsync(id, ct) && !await FinishRefundAsync(id))
                {
                    logger.LogWarning("Refund of payment {PaymentId} was not accepted; it stays Paid for a manual refund.", id);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Automatic refund for appointment {AppointmentId} failed; refund it from the dashboard.", appointmentId);
        }
    }

    private async Task<bool> ClaimRefundAsync(Guid paymentId, CancellationToken ct) =>
        await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.Paid)
            .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Refunding), ct) == 1;

    // Runs after a successful claim (status Refunding): refunds through the provider (online only) and settles the row.
    // A refusal or failure puts the payment back to Paid so it can be retried by hand.
    private async Task<bool> FinishRefundAsync(Guid paymentId)
    {
        var ok = false;
        try
        {
            var payment = await db.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId);
            ok = payment.Method != PaymentMethod.Online || (await RequestRefundAsync(payment, CancellationToken.None)).Succeeded;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Refund of payment {PaymentId} failed.", paymentId);
        }

        var now = Now;
        if (ok)
        {
            await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.Refunding)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Refunded).SetProperty(p => p.RefundedAt, now));
        }
        else
        {
            await db.Payments.Where(p => p.Id == paymentId && p.Status == PaymentStatus.Refunding)
                .ExecuteUpdateAsync(u => u.SetProperty(p => p.Status, PaymentStatus.Paid));
        }

        return ok;
    }

    private Task LockAppointmentAsync(Guid appointmentId, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({"payment:" + appointmentId}, 0))", ct);

    private async Task<RefundResult> RequestRefundAsync(Payment payment, CancellationToken ct)
    {
        if (!string.Equals(payment.Provider, provider.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiException(ErrorCodes.ProviderError, $"The payment was made with '{payment.Provider}', which is not the configured provider.");
        }

        return await provider.RefundAsync(payment, ct);
    }

    // ---- Helpers ------------------------------------------------------------------------------------------------

    public bool IsAllowedReturnUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        if (uri.Scheme is "http" or "https")
        {
            var domain = clinic.Website.Domain?.Trim() ?? "";
            var website = domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? new[] { domain, domain[4..] } : [domain, "www." + domain];
            var hosts = options.Value.ReturnUrlHosts.Concat(domain.Length == 0 ? [] : website).Where(h => !string.IsNullOrWhiteSpace(h));
            return hosts.Any(h => string.Equals(h.Trim(), uri.Host, StringComparison.OrdinalIgnoreCase));
        }

        return string.Equals(uri.Scheme, clinic.Mobile.AndroidApplicationId, StringComparison.OrdinalIgnoreCase)
            || string.Equals(uri.Scheme, clinic.Mobile.IosBundleId, StringComparison.OrdinalIgnoreCase);
    }

    // The amount due: price snapshot (or current price) minus the coupon discount.
    private static decimal TotalOf(Appointment a)
    {
        var price = a.PriceEgp ?? CouponService.PriceOf(a.Slot!.Doctor!, a.Service) ?? 0m;
        return Math.Max(0, price - a.DiscountEgp);
    }

    private DashboardPaymentDto ToDashboard(Payment p)
    {
        var appointment = p.Appointment!;
        var doctor = appointment.Slot!.Doctor!;
        return new DashboardPaymentDto(
            p.Id,
            p.AppointmentId,
            p.AmountEgp,
            p.Status,
            p.Method,
            p.Provider,
            p.CreatedAt,
            p.PaidAt,
            p.RefundedAt,
            appointment.Slot.StartsAt,
            new PatientRefDto(appointment.Patient!.Id, appointment.Patient.FullName, appointment.Patient.Phone),
            new DashboardDoctorRefDto(doctor.Id, lang.Pick(doctor.NameAr, doctor.NameEn)));
    }
}
