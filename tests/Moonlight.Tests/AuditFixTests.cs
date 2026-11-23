using System.Text.Json;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Infrastructure.Payments;

namespace Moonlight.Tests;

// A mock-like provider whose refunds are slow and counted, to prove that only one refund reaches the provider.
public sealed class CountingPaymentProvider : IPaymentProvider
{
    private int _refundCalls;

    public string Name => "mock";

    public int RefundCalls => Volatile.Read(ref _refundCalls);

    public bool RefundSucceeds { get; set; } = true;

    public Task<CheckoutResult> CreateCheckoutAsync(Payment payment, string returnUrl, PaymentCustomer customer, CancellationToken ct = default) =>
        Task.FromResult(new CheckoutResult("http://checkout.invalid/" + payment.Id, payment.Id.ToString()));

    public Task<WebhookEvent> ParseWebhookAsync(WebhookRequest request, CancellationToken ct = default)
    {
        using var document = JsonDocument.Parse(request.Body);
        var providerRef = document.RootElement.GetProperty("providerRef").GetString()!;
        var success = document.RootElement.GetProperty("success").GetBoolean();
        return Task.FromResult(new WebhookEvent(WebhookKind.Payment, providerRef, "tx-" + providerRef, success, request.Body));
    }

    public async Task<RefundResult> RefundAsync(Payment payment, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _refundCalls);
        await Task.Delay(400, CancellationToken.None);
        return new RefundResult(RefundSucceeds, "refund-" + payment.Id);
    }
}

public class PaymentAuditTests(PostgresFixture db) : Phase2TestBase(db, new Dictionary<string, string?> { ["Payments:ReturnUrlHosts:0"] = "shop.example.test" })
{
    private const string ReturnUrl = "https://shop.example.test/ar/appointments/1";

    private readonly CountingPaymentProvider _provider = new();

    protected override Action<IServiceCollection>? ConfigureServices => services =>
    {
        services.RemoveAll<IPaymentProvider>();
        services.AddSingleton<IPaymentProvider>(_provider);
    };

    private async Task<(Guid Appointment, Session Patient)> BookedAsync()
    {
        var patient = await World.NewPatientAsync();
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id });
        Assert.Equal(201, response.StatusCode);
        return (response.Id, patient);
    }

    private async Task<Guid> PayAsync(Session patient, Guid appointment)
    {
        var pay = await patient.PostAsync($"{Patient(appointment)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(200, pay.StatusCode);
        return pay["paymentId"].GetGuid();
    }

    private Task<Resp> WebhookAsync(Guid paymentId, bool success = true) =>
        World.Anonymous().PostAsync("/api/v1/payments/webhook/mock", new { providerRef = paymentId.ToString(), success });

    private async Task<Payment> PaymentAsync(Guid id) => await World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.Id == id));

    [Fact]
    public async Task A_payment_completed_after_the_appointment_was_cancelled_is_recorded_and_refunded()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var paymentId = await PayAsync(patient, id);
        Assert.Equal(200, (await patient.PostAsync($"{Patient(id)}/cancel")).StatusCode);
        Assert.Equal(PaymentStatus.Cancelled, (await PaymentAsync(paymentId)).Status);

        var webhook = await WebhookAsync(paymentId);

        Assert.Equal(200, webhook.StatusCode);
        var payment = await PaymentAsync(paymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.NotNull(payment.PaidAt);
        Assert.NotNull(payment.RefundedAt);
        Assert.Equal(1, _provider.RefundCalls);
    }

    [Fact]
    public async Task A_superseded_checkout_that_is_paid_wins_and_the_other_one_is_refunded_if_paid_too()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var first = await PayAsync(patient, id);
        var second = await PayAsync(patient, id);
        Assert.Equal(PaymentStatus.Cancelled, (await PaymentAsync(first)).Status);

        await WebhookAsync(first);

        Assert.Equal(PaymentStatus.Paid, (await PaymentAsync(first)).Status);
        Assert.Equal(PaymentStatus.Cancelled, (await PaymentAsync(second)).Status);
        Assert.Equal("Paid", (await patient.GetAsync(Patient(id)))["paymentStatus"].GetString());

        await WebhookAsync(second); // the patient also finished the replaced checkout

        Assert.Equal(PaymentStatus.Refunded, (await PaymentAsync(second)).Status);
        Assert.Equal(PaymentStatus.Paid, (await PaymentAsync(first)).Status);
        Assert.Equal(1, _provider.RefundCalls);
    }

    [Fact]
    public async Task A_payment_captured_after_a_cash_payment_is_refunded_and_the_cash_stays()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var paymentId = await PayAsync(patient, id);
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(id)}/mark-paid")).StatusCode);

        await WebhookAsync(paymentId);

        Assert.Equal(PaymentStatus.Refunded, (await PaymentAsync(paymentId)).Status);
        var payments = await World.WithDbAsync(d => d.Payments.AsNoTracking().Where(p => p.AppointmentId == id).ToListAsync());
        Assert.Single(payments, p => p.Status == PaymentStatus.Paid && p.Method == PaymentMethod.Cash);
    }

    [Fact]
    public async Task Concurrent_pay_calls_leave_exactly_one_live_checkout()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();

        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl })));

        Assert.All(results, r => Assert.True(r.StatusCode is 200 or 409, $"unexpected {r.StatusCode}"));
        Assert.Contains(results, r => r.StatusCode == 200);
        var payments = await World.WithDbAsync(d => d.Payments.AsNoTracking().Where(p => p.AppointmentId == id).ToListAsync());
        Assert.Single(payments, p => p.Status == PaymentStatus.Pending);
    }

    [Fact]
    public async Task The_database_allows_only_one_pending_payment_per_appointment()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        await PayAsync(patient, id);

        await Assert.ThrowsAsync<DbUpdateException>(() => SetAsync(async d =>
        {
            d.Payments.Add(new Payment { AppointmentId = id, AmountEgp = 300, Provider = "mock", CreatedAt = Now });
            await d.SaveChangesAsync();
        }));
    }

    [Fact]
    public async Task Concurrent_refunds_reach_the_provider_once()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var paymentId = await PayAsync(patient, id);
        await WebhookAsync(paymentId);

        var results = await Task.WhenAll(
            Staff.PostAsync($"/api/v1/dashboard/payments/{paymentId}/refund"),
            Staff.PostAsync($"/api/v1/dashboard/payments/{paymentId}/refund"),
            patient.PostAsync($"{Patient(id)}/cancel"));

        Assert.Equal(1, _provider.RefundCalls);
        Assert.Equal(PaymentStatus.Refunded, (await PaymentAsync(paymentId)).Status);
        var staffResults = results.Take(2).ToArray();
        Assert.All(staffResults, r =>
        {
            Assert.True(r.StatusCode is 200 or 409, $"unexpected staff response {r.StatusCode}");
            if (r.StatusCode == 200)
            {
                Assert.Equal("Refunded", r.Body.GetProperty("status").GetString());
            }
        });
        Assert.InRange(staffResults.Count(r => r.StatusCode == 200), 0, 1);
        Assert.Equal(200, results[2].StatusCode);
        Assert.Equal("Cancelled", results[2].Body.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_refused_refund_puts_the_payment_back_to_paid()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var paymentId = await PayAsync(patient, id);
        await WebhookAsync(paymentId);
        _provider.RefundSucceeds = false;

        var refused = await Staff.PostAsync($"/api/v1/dashboard/payments/{paymentId}/refund");

        Assert.Equal(502, refused.StatusCode);
        Assert.Equal(PaymentStatus.Paid, (await PaymentAsync(paymentId)).Status);
        _provider.RefundSucceeds = true;
        Assert.Equal(200, (await Staff.PostAsync($"/api/v1/dashboard/payments/{paymentId}/refund")).StatusCode);
    }

    [Fact]
    public async Task The_website_domain_and_its_www_variant_are_valid_return_hosts()
    {
        Enable(FeatureKeys.OnlinePayments);
        var domain = Factory.Services.GetRequiredService<ClinicConfig>().Website.Domain;
        var bare = domain.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? domain[4..] : domain;
        var (id, patient) = await BookedAsync();

        Assert.Equal(200, (await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = $"https://{bare}/x" })).StatusCode);
        Assert.Equal(200, (await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = $"https://www.{bare}/x" })).StatusCode);
        Assert.Equal(400, (await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = $"https://evil.{bare}/x" })).StatusCode);
    }
}

// Mock payments and Mock WhatsApp are not usable outside Development unless explicitly allowed.
public class MockProviderGuardTests(PostgresFixture db) : Phase2TestBase(db, new Dictionary<string, string?>
{
    ["Payments:AllowMock"] = "false",
    ["WhatsApp:AllowMock"] = "false",
})
{
    [Fact]
    public async Task Payments_answer_not_configured_and_the_mock_endpoints_are_404()
    {
        Enable(FeatureKeys.OnlinePayments);
        var patient = await World.NewPatientAsync();
        var id = (await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id })).Id;

        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = "https://example.test/x" });

        Assert.Equal(409, pay.StatusCode);
        Assert.Equal("PAYMENT_NOT_ALLOWED", pay.Code);
        Assert.Contains("not configured", pay.Body.GetProperty("detail").GetString());
        Assert.Equal(404, (await World.Anonymous().GetAsync($"/api/v1/payments/mock-checkout/{Guid.NewGuid()}")).StatusCode);
        var webhook = await World.Anonymous().PostAsync("/api/v1/payments/webhook/mock", new { providerRef = Guid.NewGuid().ToString(), success = true });
        Assert.Equal(404, webhook.StatusCode);
        Assert.Empty(await World.WithDbAsync(d => d.Payments.ToListAsync()));
    }

    [Fact]
    public async Task Whatsapp_sends_are_recorded_as_failed_not_configured()
    {
        Enable(FeatureKeys.WhatsappReminders);
        var patient = await World.NewPatientAsync();
        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));

        var row = await World.WithDbAsync(d => d.OutboundMessages.AsNoTracking().FirstAsync(m => m.UserId == patient.User!.Id && m.Channel == MessageChannel.WhatsApp));

        Assert.Equal(MessageStatus.Failed, row.Status);
        Assert.Equal("not configured", row.Error);
    }

    [Fact]
    public async Task A_production_host_with_default_providers_still_starts()
    {
        await using var production = new MoonlightApiFactory(ConnectionString, "Production", new Dictionary<string, string?>
        {
            ["Payments:AllowMock"] = "false",
            ["WhatsApp:AllowMock"] = "false",
        });

        var health = await production.CreateClient().GetAsync("/health");

        Assert.True(health.IsSuccessStatusCode);
        Assert.Equal("none", production.Services.GetRequiredService<IPaymentProvider>().Name);
    }

    [Fact]
    public void Phones_are_masked_for_logs()
    {
        Assert.Equal("+2010****78", PhoneNumber.Mask("+201012345678"));
        Assert.DoesNotContain("1234", PhoneNumber.Mask("+201012345678"));
        Assert.Equal("****", PhoneNumber.Mask(null));
    }
}

public class RecordAttachmentTests(PostgresFixture db) : Phase2TestBase(db, new Dictionary<string, string?>
{
    ["Storage:RecordsPath"] = Path.Combine(Path.GetTempPath(), "moonlight-records-" + Guid.NewGuid().ToString("N")),
    ["Storage:PublicBaseUrl"] = "https://api.example.test",
})
{
    private static readonly byte[] Pdf = "%PDF-1.4\n%test\n"u8.ToArray();

    private string RecordsDir => Path.Combine(Factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>()["Storage:RecordsPath"]!);

    private async Task<(Session Patient, Guid UserId, Guid Entry)> EntryAsync(bool visible, Session? author = null)
    {
        Enable(FeatureKeys.PatientRecords);
        var patient = await World.NewPatientAsync();
        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        var entry = await (author ?? Staff).PostAsync($"/api/v1/dashboard/patients/{patient.User!.Id}/records", new { kind = "Attachment", title = "X-ray", visibleToPatient = visible });
        Assert.Equal(201, entry.StatusCode);
        return (patient, patient.User.Id, entry.Id);
    }

    private async Task<Resp> UploadAsync(Guid entry, byte[]? bytes = null, string? host = null)
    {
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent(bytes ?? Pdf), "file", "scan.pdf");
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/dashboard/records/{entry}/attachment") { Content = form };
        if (host is not null)
        {
            request.Headers.Host = host;
        }

        using var response = await Staff.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        return new Resp(response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private int FileCount() => Directory.Exists(RecordsDir) ? Directory.GetFiles(RecordsDir).Length : 0;

    [Fact]
    public async Task Attachments_are_private_authorized_and_use_the_configured_base_url()
    {
        var (patient, _, entry) = await EntryAsync(visible: false);

        var upload = await UploadAsync(entry, host: "evil.test");

        Assert.Equal(200, upload.StatusCode);
        var url = upload["attachmentUrl"].GetString()!;
        Assert.Equal($"https://api.example.test/api/v1/dashboard/records/{entry}/attachment", url); // never the Host header
        Assert.Equal(1, FileCount());

        // nothing is reachable through the public uploads path, and the endpoint needs a token
        Assert.Equal(404, (int)(await Factory.CreateClient().GetAsync($"/uploads/records/{Path.GetFileName(Directory.GetFiles(RecordsDir)[0])}")).StatusCode);
        Assert.Equal(401, (int)(await Factory.CreateClient().GetAsync($"/api/v1/dashboard/records/{entry}/attachment")).StatusCode);

        var staff = await Staff.Client.GetAsync($"/api/v1/dashboard/records/{entry}/attachment");
        Assert.Equal(200, (int)staff.StatusCode);
        Assert.Equal("application/pdf", staff.Content.Headers.ContentType!.MediaType);
        Assert.Equal(Pdf, await staff.Content.ReadAsByteArrayAsync());
        Assert.Equal(200, (int)(await DoctorSession.Client.GetAsync($"/api/v1/dashboard/records/{entry}/attachment")).StatusCode);

        // hidden from the patient until the clinic shares it; never to others
        Assert.Equal(404, (await patient.GetAsync($"/api/v1/me/records/{entry}/attachment")).StatusCode);
        var other = await World.NewPatientAsync();
        Assert.Equal(404, (int)(await other.Client.GetAsync($"/api/v1/me/records/{entry}/attachment")).StatusCode);
        var otherClinicStaff = await World.LoginAsync(await World.NewStaffAsync(await World.NewClinicAsync("Other")));
        Assert.Equal(404, (int)(await otherClinicStaff.Client.GetAsync($"/api/v1/dashboard/records/{entry}/attachment")).StatusCode);

        var share = await Staff.PutAsync($"/api/v1/dashboard/records/{entry}", new { kind = "Attachment", title = "X-ray", visibleToPatient = true });
        Assert.Equal(200, share.StatusCode);
        var mine = await patient.Client.GetAsync($"/api/v1/me/records/{entry}/attachment");
        Assert.Equal(200, (int)mine.StatusCode);
        Assert.Equal(Pdf, await mine.Content.ReadAsByteArrayAsync());
        Assert.Equal($"https://api.example.test/api/v1/me/records/{entry}/attachment", Assert.Single((await patient.GetAsync("/api/v1/me/records"))["entries"].EnumerateArray()).GetProperty("attachmentUrl").GetString());
    }

    [Fact]
    public async Task Replacing_an_attachment_and_deleting_the_entry_remove_the_files()
    {
        var (_, _, entry) = await EntryAsync(visible: true);
        await UploadAsync(entry);
        Assert.Equal(1, FileCount());

        await UploadAsync(entry, "%PDF-1.4\n%second\n"u8.ToArray());
        Assert.Equal(1, FileCount());

        Assert.Equal(204, (await Staff.DeleteAsync($"/api/v1/dashboard/records/{entry}")).StatusCode);
        Assert.Equal(0, FileCount());
    }
}

public class FamilySoftDeleteTests(PostgresFixture db) : Phase2TestBase(db)
{
    [Fact]
    public async Task Deleting_a_member_keeps_the_clinic_records_and_the_appointments_pointing_at_them()
    {
        Enable(FeatureKeys.FamilyAccounts, FeatureKeys.PatientRecords, FeatureKeys.WaitingList);
        var patient = await World.NewPatientAsync();
        var member = await NewMemberAsync(patient);
        var booked = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, familyMemberId = member.Id });
        Assert.Equal(201, booked.StatusCode);
        var userId = patient.User!.Id;
        var diagnosis = await Staff.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { familyMemberId = member.Id, kind = "Diagnosis", title = "Caries", visibleToPatient = true });
        Assert.Equal(201, diagnosis.StatusCode);
        await Staff.PutAsync($"/api/v1/dashboard/patients/{userId}/medical-info", new { familyMemberId = member.Id, bloodType = "A-" });
        Assert.Equal(201, (await patient.PostAsync("/api/v1/waiting-list", new { doctorId = Doctor.Id, date = Today(Now).AddDays(5).ToString("yyyy-MM-dd"), familyMemberId = member.Id })).StatusCode);

        var delete = await patient.DeleteAsync($"/api/v1/me/family/{member.Id}");

        Assert.Equal(204, delete.StatusCode);
        Assert.Empty((await patient.GetAsync("/api/v1/me/family")).Body.EnumerateArray());
        Assert.Equal(404, (await patient.DeleteAsync($"/api/v1/me/family/{member.Id}")).StatusCode);
        Assert.Equal(404, (await patient.PutAsync($"/api/v1/me/family/{member.Id}", new { fullName = "X", relation = "Child" })).StatusCode);
        Assert.Equal(booked.Id, (await World.WithDbAsync(d => d.Appointments.AsNoTracking().FirstAsync(a => a.Id == booked.Id))).Id);
        var appointment = await World.WithDbAsync(d => d.Appointments.AsNoTracking().FirstAsync(a => a.Id == booked.Id));
        Assert.Equal(member.Id, appointment.FamilyMemberId);
        Assert.Equal(member.Id, (await patient.GetAsync(Patient(booked.Id)))["familyMember"].GetProperty("id").GetGuid());
        Assert.Equal(1, await World.WithDbAsync(d => d.PatientRecordEntries.CountAsync(r => r.FamilyMemberId == member.Id)));
        Assert.Equal(1, await World.WithDbAsync(d => d.PatientMedicalInfos.CountAsync(m => m.FamilyMemberId == member.Id)));
        Assert.NotNull((await World.WithDbAsync(d => d.FamilyMembers.AsNoTracking().FirstAsync(f => f.Id == member.Id))).DeletedAt);
        Assert.Equal(1, await World.WithDbAsync(d => d.WaitingListEntries.CountAsync(w => w.FamilyMemberId == member.Id && w.Status == WaitingListStatus.Cancelled)));

        // a deleted member cannot be booked for any more
        var again = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(4))).Id, familyMemberId = member.Id });
        Assert.True(again.StatusCode is 400 or 404 or 422, $"got {again.StatusCode}");
        // and the database itself refuses a hard delete
        await Assert.ThrowsAsync<Npgsql.PostgresException>(() => SetAsync(d => d.FamilyMembers.Where(f => f.Id == member.Id).ExecuteDeleteAsync()));
    }

    [Fact]
    public async Task Waiting_list_entries_report_the_family_member()
    {
        Enable(FeatureKeys.FamilyAccounts, FeatureKeys.WaitingList);
        var patient = await World.NewPatientAsync();
        var member = await NewMemberAsync(patient, "Omar");
        var date = Today(Now).AddDays(5).ToString("yyyy-MM-dd");
        await patient.PostAsync("/api/v1/waiting-list", new { doctorId = Doctor.Id, date });
        await patient.PostAsync("/api/v1/waiting-list", new { doctorId = Doctor.Id, date, familyMemberId = member.Id });

        var entries = (await patient.GetAsync("/api/v1/waiting-list")).Body.EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        Assert.Single(entries, e => e.GetProperty("familyMember").ValueKind == JsonValueKind.Null);
        var child = Assert.Single(entries, e => e.GetProperty("familyMember").ValueKind == JsonValueKind.Object).GetProperty("familyMember");
        Assert.Equal(member.Id, child.GetProperty("id").GetGuid());
        Assert.Equal("Omar", child.GetProperty("fullName").GetString());
        Assert.Equal("Child", child.GetProperty("relation").GetString());
    }

    [Fact]
    public async Task Only_one_active_waiting_entry_per_person_even_for_the_owner()
    {
        Enable(FeatureKeys.WaitingList);
        var patient = await World.NewPatientAsync();
        var body = new { doctorId = Doctor.Id, date = Today(Now).AddDays(5).ToString("yyyy-MM-dd") };

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => patient.PostAsync("/api/v1/waiting-list", body)));

        Assert.Equal(1, results.Count(r => r.StatusCode == 201));
        Assert.All(results.Where(r => r.StatusCode != 201), r => Assert.Equal("ALREADY_WAITING", r.Code));
        Assert.Equal(1, await World.WithDbAsync(d => d.WaitingListEntries.CountAsync(w => w.PatientUserId == patient.User!.Id)));
    }
}

public class RoleAndRaceAuditTests(PostgresFixture db) : Phase2TestBase(db)
{
    [Fact]
    public async Task Doctors_can_list_templates_but_only_staff_can_change_them()
    {
        Enable(FeatureKeys.WorkingHoursTemplates);
        var body = new { name = "A", slotMinutes = 30, rules = new[] { new { dayOfWeek = "Monday", start = "09:00", end = "12:00" } } };

        Assert.Equal(200, (await DoctorSession.GetAsync("/api/v1/dashboard/working-hours-templates")).StatusCode);
        Assert.Equal(403, (await DoctorSession.PostAsync("/api/v1/dashboard/working-hours-templates", body)).StatusCode);
        var created = await Staff.PostAsync("/api/v1/dashboard/working-hours-templates", body);
        Assert.Equal(201, created.StatusCode);
        Assert.Equal(403, (await DoctorSession.PutAsync($"/api/v1/dashboard/working-hours-templates/{created.Id}", body)).StatusCode);
        Assert.Equal(403, (await DoctorSession.DeleteAsync($"/api/v1/dashboard/working-hours-templates/{created.Id}")).StatusCode);
    }

    [Fact]
    public async Task Doctors_may_reply_to_reviews_but_only_staff_hide_and_publish()
    {
        Enable(FeatureKeys.Reviews);
        var (id, patient, _) = await CompletedVisitAsync();
        await patient.PostAsync($"{Patient(id)}/review", new { rating = 2, comment = "Slow" });
        var reviewId = Assert.Single((await Staff.GetAsync("/api/v1/dashboard/reviews"))["items"].EnumerateArray()).GetProperty("id").GetGuid();

        Assert.Equal(403, (await DoctorSession.PostAsync($"/api/v1/dashboard/reviews/{reviewId}/hide")).StatusCode);
        Assert.Equal(200, (await DoctorSession.PutAsync($"/api/v1/dashboard/reviews/{reviewId}/reply", new { reply = "Sorry" })).StatusCode);
        Assert.Equal(200, (await Staff.PostAsync($"/api/v1/dashboard/reviews/{reviewId}/hide")).StatusCode);
        Assert.Equal(403, (await DoctorSession.PostAsync($"/api/v1/dashboard/reviews/{reviewId}/publish")).StatusCode);
    }

    [Fact]
    public async Task A_per_patient_coupon_limit_holds_under_concurrent_bookings()
    {
        Enable(FeatureKeys.Promotions);
        await NewCouponAsync("ONCE10");
        var patient = await World.NewPatientAsync();
        var first = await SlotAsync(TimeSpan.FromDays(2));
        var second = await SlotAsync(TimeSpan.FromDays(4));

        var results = await Task.WhenAll(
            patient.PostAsync("/api/v1/appointments", new { slotId = first.Id, serviceId = Service.Id, couponCode = "ONCE10" }),
            patient.PostAsync("/api/v1/appointments", new { slotId = second.Id, serviceId = Service.Id, couponCode = "ONCE10" }));

        Assert.Equal(1, results.Count(r => r.StatusCode == 201));
        Assert.Equal("COUPON_INVALID", results.Single(r => r.StatusCode != 201).Code);
        Assert.Equal(1, await World.WithDbAsync(d => d.CouponRedemptions.CountAsync(r => r.PatientUserId == patient.User!.Id)));
        Assert.Equal(1, (await World.WithDbAsync(d => d.Coupons.AsNoTracking().FirstAsync(c => c.Code == "ONCE10"))).RedemptionsCount);
    }

    [Fact]
    public async Task Cancelling_a_booking_releases_the_coupon_and_still_succeeds_when_the_waiting_list_notification_fails()
    {
        Enable(FeatureKeys.Promotions, FeatureKeys.WaitingList);
        await NewCouponAsync("CANCEL10");
        var patient = await World.NewPatientAsync();
        var id = (await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id, couponCode = "CANCEL10" })).Id;

        var cancelled = await patient.PostAsync($"{Patient(id)}/cancel");

        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal(0, (await World.WithDbAsync(d => d.Coupons.AsNoTracking().FirstAsync(c => c.Code == "CANCEL10"))).RedemptionsCount);
        Assert.Equal(0, await World.WithDbAsync(d => d.CouponRedemptions.CountAsync(r => r.AppointmentId == id)));
    }

    [Fact]
    public async Task The_outbox_write_never_flushes_the_callers_pending_changes()
    {
        Enable(FeatureKeys.SmsNotifications);
        var patient = await World.NewPatientAsync();
        using var scope = Factory.Services.CreateScope();
        var context = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
        var user = await context.Users.FirstAsync(u => u.Id == patient.User!.Id);
        var pending = new User { Phone = TestWorld.NewPhone(), Role = UserRole.Patient, CreatedAt = Now };
        context.Users.Add(pending);

        await scope.ServiceProvider.GetRequiredService<MessageDispatcher>().SendAsync(
            user, Clinic.Id, new OutboundContent("appointment.reminder", "T", "B", new Dictionary<string, string>(), []), pushEnabled: false, CancellationToken.None);

        Assert.Equal(1, await World.WithDbAsync(d => d.OutboundMessages.CountAsync(m => m.UserId == user.Id)));
        Assert.Equal(0, await World.WithDbAsync(d => d.Users.CountAsync(u => u.Id == pending.Id)));
    }

    [Fact]
    public async Task Maintenance_jobs_run_even_when_reminders_are_switched_off()
    {
        await using var factory = new MoonlightApiFactory(ConnectionString, settings: new Dictionary<string, string?> { ["Reminders:Enabled"] = "false", ["Jobs:Enabled"] = "true" });
        var hosted = factory.Services.GetServices<IHostedService>().ToList();
        Assert.Contains(hosted, s => s is MaintenanceBackgroundService);
        Assert.DoesNotContain(hosted, s => s is ReminderBackgroundService);

        await using var off = new MoonlightApiFactory(ConnectionString, settings: new Dictionary<string, string?> { ["Jobs:Enabled"] = "false" });
        Assert.DoesNotContain(off.Services.GetServices<IHostedService>(), s => s is MaintenanceBackgroundService);
    }

    [Fact]
    public async Task Offers_and_the_holiday_year_follow_the_clinic_local_date_around_midnight()
    {
        Enable(FeatureKeys.Promotions, FeatureKeys.HolidayCalendar);
        // 22:30 UTC on 31 Dec is 00:30 on 1 Jan in Cairo.
        var target = new DateTime(Now.Year, 12, 31, 22, 30, 0, DateTimeKind.Utc);
        if (target <= Now)
        {
            target = target.AddYears(1);
        }

        var newYear = new DateOnly(target.Year + 1, 1, 1);
        await SetAsync(async d =>
        {
            d.Coupons.Add(new Coupon
            {
                ClinicId = Clinic.Id,
                Code = "NEWYEAR",
                TitleAr = "ع",
                TitleEn = "New year",
                DiscountType = CouponDiscountType.Percent,
                Value = 10,
                ValidFrom = newYear,
                ValidTo = newYear.AddDays(30),
                IsPublic = true,
                CreatedAt = Now,
            });
            d.ClinicHolidays.Add(new ClinicHoliday { ClinicId = Clinic.Id, Date = newYear, NameAr = "ع", NameEn = "New year", Source = HolidaySource.Manual });
            await d.SaveChangesAsync();
        });
        var before = await World.NewPatientAsync();
        Assert.DoesNotContain(
            (await before.GetAsync("/api/v1/promotions")).Body.EnumerateArray(), o => o.GetProperty("code").GetString() == "NEWYEAR");

        Factory.Clock.Advance(target - Now);

        var patient = await World.NewPatientAsync();
        Assert.Contains((await patient.GetAsync("/api/v1/promotions")).Body.EnumerateArray(), o => o.GetProperty("code").GetString() == "NEWYEAR");
        var holidays = await Staff.GetAsync("/api/v1/dashboard/holidays");
        Assert.Contains(holidays.Body.EnumerateArray(), h => h.GetProperty("date").GetString() == newYear.ToString("yyyy-MM-dd"));
    }
}
