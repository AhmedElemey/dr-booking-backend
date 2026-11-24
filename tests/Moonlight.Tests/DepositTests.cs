using System.Text.Json.Nodes;

using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

// Deposits: a clinic config with depositAmountEgp > 0 makes the first online checkout charge the fixed
// deposit (capped at the visit total, Kind=Deposit); the rest of the price is settled at the clinic.
public class DepositTests(PostgresFixture db) : Phase2TestBase(db, new Dictionary<string, string?>
{
    ["Payments:ReturnUrlHosts:0"] = "shop.example.test",
    ["ClinicConfig:Path"] = DepositConfigPath,
})
{
    private const string ReturnUrl = "https://shop.example.test/ar/appointments/1";

    private static readonly string DepositConfigPath = WriteConfig();

    private static string WriteConfig()
    {
        var root = FindRepoRoot();
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "clinic.config.json")))!.AsObject();
        json["depositAmountEgp"] = 100;
        json["logo"] = Path.Combine(root, json["logo"]!.GetValue<string>());
        var dir = Path.Combine(Path.GetTempPath(), "moonlight-test-deposits");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "clinic.config.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "clinic.config.json")) && File.Exists(Path.Combine(dir.FullName, "Moonlight.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private async Task<(Guid Appointment, Session Patient)> BookedAsync(Guid? serviceId = null)
    {
        var patient = await World.NewPatientAsync();
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = serviceId ?? Service.Id });
        Assert.Equal(201, response.StatusCode);
        return (response.Id, patient);
    }

    private Task<Resp> WebhookAsync(Guid paymentId, bool success) =>
        World.Anonymous().PostAsync("/api/v1/payments/webhook/mock", new { providerRef = paymentId.ToString(), success });

    private Task<Payment> PaymentAsync(Guid id) => World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.Id == id));

    [Fact]
    public async Task Pay_charges_the_configured_deposit_not_the_full_price()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();

        var before = await patient.GetAsync(Patient(id));
        Assert.Equal(300m, before["totalEgp"].GetDecimal());
        Assert.Equal(100m, before["depositRequiredEgp"].GetDecimal());
        Assert.Equal(0m, before["depositPaidEgp"].GetDecimal());
        Assert.Equal("Unpaid", before["paymentStatus"].GetString());

        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(200, pay.StatusCode);
        var paymentId = pay["paymentId"].GetGuid();
        var payment = await PaymentAsync(paymentId);
        Assert.Equal(100m, payment.AmountEgp);
        Assert.Equal(PaymentKind.Deposit, payment.Kind);

        Assert.Equal(200, (await WebhookAsync(paymentId, true)).StatusCode);
        var after = await patient.GetAsync(Patient(id));
        Assert.Equal("DepositPaid", after["paymentStatus"].GetString());
        Assert.Equal(100m, after["depositPaidEgp"].GetDecimal());

        // Staff see the same deposit fields.
        var staffView = await Staff.GetAsync(Dash(id));
        Assert.Equal(100m, staffView["depositRequiredEgp"].GetDecimal());
        Assert.Equal(100m, staffView["depositPaidEgp"].GetDecimal());
        Assert.Equal("DepositPaid", staffView["paymentStatus"].GetString());

        // The remainder is settled at the clinic: no second online checkout.
        var again = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(409, again.StatusCode);
    }

    [Fact]
    public async Task Deposit_is_capped_at_the_visit_total()
    {
        Enable(FeatureKeys.OnlinePayments);
        var cheap = await World.WithDbAsync(async d =>
        {
            var service = new Service { DoctorId = Doctor.Id, NameAr = "استشارة", NameEn = "Consult", DurationMinutes = 30, PriceEgp = 50, IsActive = true };
            d.Services.Add(service);
            await d.SaveChangesAsync();
            return service;
        });
        var (id, patient) = await BookedAsync(cheap.Id);

        var before = await patient.GetAsync(Patient(id));
        Assert.Equal(50m, before["depositRequiredEgp"].GetDecimal());

        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(200, pay.StatusCode);
        var payment = await PaymentAsync(pay["paymentId"].GetGuid());
        Assert.Equal(50m, payment.AmountEgp);
        Assert.Equal(PaymentKind.Full, payment.Kind);
    }

    [Fact]
    public async Task Patient_cancelling_a_deposit_paid_appointment_refunds_the_deposit()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        var paymentId = pay["paymentId"].GetGuid();
        Assert.Equal(200, (await WebhookAsync(paymentId, true)).StatusCode);

        var cancelled = await patient.PostAsync($"{Patient(id)}/cancel");
        Assert.Equal(200, cancelled.StatusCode);
        Assert.Equal("Refunded", cancelled["paymentStatus"].GetString());
        var payment = await PaymentAsync(paymentId);
        Assert.Equal(PaymentStatus.Refunded, payment.Status);
        Assert.NotNull(payment.RefundedAt);
    }

    [Fact]
    public async Task Staff_mark_paid_records_the_clinic_balance_after_a_deposit()
    {
        Enable(FeatureKeys.OnlinePayments);
        var service = await World.WithDbAsync(async d =>
        {
            var s = new Service { DoctorId = Doctor.Id, NameAr = "كشف", NameEn = "Visit", DurationMinutes = 30, PriceEgp = 270, IsActive = true };
            d.Services.Add(s);
            await d.SaveChangesAsync();
            return s;
        });
        var (id, patient) = await BookedAsync(service.Id);
        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(200, (await WebhookAsync(pay["paymentId"].GetGuid(), true)).StatusCode);
        Assert.Equal("DepositPaid", (await Staff.GetAsync(Dash(id)))["paymentStatus"].GetString());

        var marked = await Staff.PostAsync($"{Dash(id)}/mark-paid");
        Assert.Equal(200, marked.StatusCode);
        Assert.Equal("Paid", marked["paymentStatus"].GetString());
        var cash = await World.WithDbAsync(d => d.Payments.AsNoTracking().Where(p => p.AppointmentId == id && p.Method == PaymentMethod.Cash).ToListAsync());
        var only = Assert.Single(cash);
        Assert.Equal(170m, only.AmountEgp);

        // Nothing is left to collect.
        Assert.Equal(409, (await Staff.PostAsync($"{Dash(id)}/mark-paid")).StatusCode);
    }

    [Fact]
    public async Task Clinic_deposit_overrides_the_config_default()
    {
        Enable(FeatureKeys.OnlinePayments);
        await World.WithDbAsync(async d =>
        {
            (await d.Clinics.FirstAsync(c => c.Id == Clinic.Id)).DepositAmountEgp = 150;
            await d.SaveChangesAsync();
            return 0;
        });
        var (id, patient) = await BookedAsync();
        Assert.Equal(150m, (await patient.GetAsync(Patient(id)))["depositRequiredEgp"].GetDecimal());
        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        Assert.Equal(150m, (await PaymentAsync(pay["paymentId"].GetGuid())).AmountEgp);
    }

    [Fact]
    public async Task Clinic_without_its_own_deposit_uses_the_config_default()
    {
        Enable(FeatureKeys.OnlinePayments);
        var (id, patient) = await BookedAsync();
        Assert.Equal(100m, (await patient.GetAsync(Patient(id)))["depositRequiredEgp"].GetDecimal());
    }

    [Fact]
    public async Task Clinic_deposit_of_zero_means_no_deposit()
    {
        Enable(FeatureKeys.OnlinePayments);
        await World.WithDbAsync(async d =>
        {
            (await d.Clinics.FirstAsync(c => c.Id == Clinic.Id)).DepositAmountEgp = 0;
            await d.SaveChangesAsync();
            return 0;
        });
        var (id, patient) = await BookedAsync();
        Assert.Equal(0m, (await patient.GetAsync(Patient(id)))["depositRequiredEgp"].GetDecimal());
        var pay = await patient.PostAsync($"{Patient(id)}/pay", new { returnUrl = ReturnUrl });
        var payment = await PaymentAsync(pay["paymentId"].GetGuid());
        Assert.Equal(300m, payment.AmountEgp);
        Assert.Equal(PaymentKind.Full, payment.Kind);
    }
}
