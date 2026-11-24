using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Recall;

namespace Moonlight.Tests;

public class RecallTests(PostgresFixture db) : Phase2TestBase(db)
{
    protected override bool IsolatedDatabase => true;

    private static int _visit;

    private async Task<Guid> PastVisitAsync(Session patient, Doctor doctor, int monthsAgo, AppointmentStatus status = AppointmentStatus.Completed)
    {
        var start = Now.AddMonths(-monthsAgo);
        start = new DateTime(start.Year, start.Month, start.Day, 6, 0, 0, DateTimeKind.Utc).AddMinutes(30 * Interlocked.Increment(ref _visit));
        var slot = await World.NewSlotAtAsync(doctor, start, 30, SlotStatus.Reserved);
        return await World.WithDbAsync(async d =>
        {
            var a = new Appointment { SlotId = slot.Id, PatientUserId = patient.User!.Id, CreatedByUserId = patient.User.Id, Status = status, CompletedAt = slot.EndsAt, CreatedAt = start.AddDays(-3), UpdatedAt = slot.EndsAt };
            d.Appointments.Add(a);
            await d.SaveChangesAsync();
            return a.Id;
        });
    }

    private async Task<(Guid Campaign, Session Eligible)> ScenarioAsync(string mode = "OneOff")
    {
        var eligible = await World.NewPatientAsync();
        await PastVisitAsync(eligible, Doctor, 8);

        var hasFuture = await World.NewPatientAsync();
        await PastVisitAsync(hasFuture, Doctor, 8);
        await World.BookAsync(hasFuture, await SlotAsync(TimeSpan.FromDays(3)));

        var recent = await World.NewPatientAsync();
        await PastVisitAsync(recent, Doctor, 2);

        var optedOut = await World.NewPatientAsync();
        await PastVisitAsync(optedOut, Doctor, 9);
        await optedOut.PutAsync("/api/v1/me", new { marketingOptIn = false });

        var cancelledOnly = await World.NewPatientAsync();
        await PastVisitAsync(cancelledOnly, Doctor, 8, AppointmentStatus.Cancelled);

        var otherDoctor = await World.NewDoctorAsync(Clinic, "Dr Other");
        var otherVisit = await World.NewPatientAsync();
        await PastVisitAsync(otherVisit, otherDoctor, 8);

        var created = await Staff.PostAsync("/api/v1/dashboard/recall-campaigns", new
        {
            name = "Six month check",
            messageAr = "مرحبا {name}، حان موعد زيارتك مع {doctor} في {clinic} {link}",
            messageEn = "Hi {name}, time to visit {doctor} at {clinic} {link}",
            channel = "Sms",
            monthsSinceLastVisit = 6,
            doctorId = Doctor.Id,
            mode,
            isActive = true,
        });
        Assert.Equal(201, created.StatusCode);
        return (created.Id, eligible);
    }

    [Fact]
    public async Task Preview_counts_only_lapsed_opted_in_patients_without_a_future_booking()
    {
        Enable(FeatureKeys.RecallCampaigns);
        var (campaign, eligible) = await ScenarioAsync();

        var preview = await Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/preview");

        Assert.Equal(200, preview.StatusCode);
        Assert.Equal(1, preview["audienceCount"].GetInt32());
        Assert.Equal(eligible.User!.Phone, Assert.Single(preview["sample"].EnumerateArray()).GetProperty("phone").GetString());
    }

    [Fact]
    public async Task Send_now_delivers_with_placeholders_and_never_twice_within_30_days()
    {
        Enable(FeatureKeys.RecallCampaigns);
        var (campaign, eligible) = await ScenarioAsync();
        await eligible.PutAsync("/api/v1/me", new { fullName = "Nour", preferredLanguage = "en" });

        var run = await Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/send-now");

        Assert.Equal(200, run.StatusCode);
        Assert.Equal(1, run["sent"].GetInt32());
        Assert.Equal(0, run["failed"].GetInt32());
        var sms = Assert.Single(Factory.Sms.Sent);
        Assert.Equal(eligible.User!.Phone, sms.Phone);
        Assert.Contains("Hi Nour, time to visit Dr Test at Clinic", sms.Message);
        Assert.DoesNotContain("{", sms.Message);

        Factory.Clock.Advance(TimeSpan.FromMinutes(6)); // past the send-now cool-down
        var second = await Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/send-now");
        Assert.Equal(0, second["sent"].GetInt32());
        Assert.Single(Factory.Sms.Sent);

        var sends = await Staff.GetAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/sends");
        Assert.Single(sends["items"].EnumerateArray());
        Assert.Equal("Sent", sends["items"][0].GetProperty("status").GetString());

        // after 30 days the patient is eligible again
        Factory.Clock.Advance(TimeSpan.FromDays(31));
        // the eligible patient and the one whose future visit has meanwhile passed
        Assert.Equal(2, (await Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/preview"))["audienceCount"].GetInt32());
    }

    [Fact]
    public async Task Concurrent_send_now_calls_message_the_audience_once()
    {
        Enable(FeatureKeys.RecallCampaigns);
        var (campaign, _) = await ScenarioAsync();

        var results = await Task.WhenAll(
            Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/send-now"),
            Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/send-now"),
            Staff.PostAsync($"/api/v1/dashboard/recall-campaigns/{campaign}/send-now"));

        Assert.Equal(1, results.Count(r => r.StatusCode == 200));
        Assert.All(results.Where(r => r.StatusCode != 200), r => Assert.Equal("INVALID_TRANSITION", r.Code));
        Assert.Single(Factory.Sms.Sent);
    }

    [Fact]
    public async Task Recurring_campaigns_run_once_a_day_after_ten_clinic_time()
    {
        Enable(FeatureKeys.RecallCampaigns);
        await ScenarioAsync("Recurring");
        var local = TimeZoneInfo.ConvertTimeFromUtc(Now, Cairo);
        // Choose 10:30 today, or tomorrow when today's run hour has passed.
        // Advancing to 03:00 the next day would leave the campaign not yet due.
        var targetLocal = local.Date.AddDays(local.Hour >= 10 ? 1 : 0).AddHours(10.5);
        var targetUtc = TimeZoneInfo.ConvertTimeToUtc(targetLocal, Cairo);
        Factory.Clock.Advance(targetUtc - Now);

        async Task<int> RunAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<RecallService>().RunDueAsync(CancellationToken.None);
        }

        // Whatever the starting hour, the clock is now past 10:00 clinic time.
        Assert.True(TimeZoneInfo.ConvertTimeFromUtc(Now, Cairo).Hour >= 10);
        Assert.Equal(1, await RunAsync());
        Assert.Single(Factory.Sms.Sent);
        Assert.Equal(0, await RunAsync());

        Factory.Clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, await RunAsync()); // next day: the campaign runs again (audience is empty: already messaged within 30 days)
        Assert.Single(Factory.Sms.Sent);
    }

    [Fact]
    public async Task Campaign_crud_validation_and_clinic_scope()
    {
        Enable(FeatureKeys.RecallCampaigns);
        var (campaign, _) = await ScenarioAsync();

        var updated = await Staff.PutAsync($"/api/v1/dashboard/recall-campaigns/{campaign}", new { name = "Renamed", messageAr = "a {name}", messageEn = "b {name}", channel = "Auto", monthsSinceLastVisit = 12, mode = "OneOff", isActive = false });
        Assert.Equal("Renamed", updated["name"].GetString());
        Assert.Equal(400, (await Staff.PostAsync("/api/v1/dashboard/recall-campaigns", new { name = "", monthsSinceLastVisit = 0 })).StatusCode);

        var otherStaff = await World.LoginAsync(await World.NewStaffAsync(await World.NewClinicAsync("Other")));
        Assert.Equal(404, (await otherStaff.GetAsync($"/api/v1/dashboard/recall-campaigns/{campaign}")).StatusCode);
        Assert.Equal(204, (await Staff.DeleteAsync($"/api/v1/dashboard/recall-campaigns/{campaign}")).StatusCode);
    }
}

public class ReportTests(PostgresFixture db) : Phase2TestBase(db)
{
    protected override bool IsolatedDatabase => true;

    private Task SetStatusAsync(Guid id, AppointmentStatus status) => SetAsync(async d =>
        await d.Appointments.Where(a => a.Id == id).ExecuteUpdateAsync(u => u.SetProperty(a => a.Status, status).SetProperty(a => a.CompletedAt, Now)));

    private async Task<Guid> BookAsync(Session patient, int hour, string? coupon = null)
    {
        var slot = await LocalSlotAsync(1, hour);
        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id, serviceId = Service.Id, couponCode = coupon });
        Assert.Equal(201, response.StatusCode);
        Assert.Equal(200, (await Staff.PostAsync($"/api/v1/dashboard/appointments/{response.Id}/approve")).StatusCode);
        return response.Id;
    }

    [Fact]
    public async Task Booking_and_revenue_numbers_add_up()
    {
        Enable(FeatureKeys.Reports, FeatureKeys.OnlinePayments, FeatureKeys.Promotions);
        await NewCouponAsync("REPORT10");
        var from = Today(Now);
        var to = from.AddDays(2);
        var patient = await World.NewPatientAsync();

        var a = await BookAsync(patient, 10);          // completed, paid in cash
        var b = await BookAsync(await World.NewPatientAsync(), 11);   // cancelled
        var c = await BookAsync(await World.NewPatientAsync(), 12, "REPORT10"); // completed, 270 paid online
        var d = await BookAsync(await World.NewPatientAsync(), 13);   // no-show
        await Staff.PostAsync($"{Dash(a)}/mark-paid");
        var payment = await World.WithDbAsync(x => x.Payments.AsNoTracking().FirstAsync(p => p.AppointmentId == a));
        Assert.Equal(300m, payment.AmountEgp);
        var onlinePatient = (await World.WithDbAsync(x => x.Appointments.Include(p => p.Patient).FirstAsync(p => p.Id == c))).Patient!;
        var online = await (await World.LoginAsync(onlinePatient)).PostAsync($"{Patient(c)}/pay", new { returnUrl = "http://localhost/back" });
        if (online.StatusCode == 200)
        {
            await World.Anonymous().PostAsync("/api/v1/payments/webhook/mock", new { providerRef = online["paymentId"].GetString(), success = true });
        }
        else
        {
            // Localhost is not an allowed return host in tests: pay through the staff cash path instead.
            await Staff.PostAsync($"{Dash(c)}/mark-paid");
        }

        var cancelled = await Staff.PostAsync($"{Dash(b)}/cancel");
        Assert.Equal(200, cancelled.StatusCode);
        await SetStatusAsync(a, AppointmentStatus.Completed);
        await SetStatusAsync(c, AppointmentStatus.Completed);
        await SetStatusAsync(d, AppointmentStatus.NoShow);

        var query = $"from={from:yyyy-MM-dd}&to={to:yyyy-MM-dd}";
        var bookings = await Staff.GetAsync($"/api/v1/dashboard/reports/bookings?{query}&groupBy=Day");

        Assert.Equal(200, bookings.StatusCode);
        var totals = bookings["totals"];
        Assert.Equal(4, totals.GetProperty("booked").GetInt32());
        Assert.Equal(2, totals.GetProperty("completed").GetInt32());
        Assert.Equal(1, totals.GetProperty("cancelled").GetInt32());
        Assert.Equal(1, totals.GetProperty("noShow").GetInt32());
        Assert.Equal(0.25, totals.GetProperty("cancellationRate").GetDouble());
        Assert.Equal(3, bookings["series"].GetArrayLength());
        Assert.Equal(4, bookings["series"][1].GetProperty("booked").GetInt32());
        Assert.Equal(4, Assert.Single(bookings["byDoctor"].EnumerateArray()).GetProperty("booked").GetInt32());
        Assert.Equal(4, bookings["bySource"].GetProperty("patient").GetInt32());
        Assert.Equal(0, bookings["bySource"].GetProperty("staff").GetInt32());
        Assert.Single((await Staff.GetAsync($"/api/v1/dashboard/reports/bookings?{query}&groupBy=Month"))["series"].EnumerateArray().Where(s => s.GetProperty("booked").GetInt32() > 0));

        var revenue = await Staff.GetAsync($"/api/v1/dashboard/reports/revenue?{query}");
        var r = revenue["totals"];
        Assert.Equal(570m, r.GetProperty("expectedEgp").GetDecimal());
        Assert.Equal(570m, r.GetProperty("collectedEgp").GetDecimal());
        Assert.Equal(30m, r.GetProperty("discountsEgp").GetDecimal());
        Assert.Equal(0m, r.GetProperty("refundsEgp").GetDecimal());
        Assert.Equal(570m, r.GetProperty("onlineEgp").GetDecimal() + r.GetProperty("cashEgp").GetDecimal());

        var paymentOfC = await World.WithDbAsync(x => x.Payments.AsNoTracking().FirstAsync(p => p.AppointmentId == c));
        await Staff.PostAsync($"/api/v1/dashboard/payments/{paymentOfC.Id}/refund");
        var after = (await Staff.GetAsync($"/api/v1/dashboard/reports/revenue?{query}"))["totals"];
        // A later refund never rewrites what was collected: net = collected - refunds.
        Assert.Equal(570m, after.GetProperty("collectedEgp").GetDecimal());
        Assert.Equal(270m, after.GetProperty("refundsEgp").GetDecimal());

        var filtered = await Staff.GetAsync($"/api/v1/dashboard/reports/bookings?{query}&doctorId={Guid.NewGuid()}");
        Assert.Equal(0, filtered["totals"].GetProperty("booked").GetInt32());
    }

    [Fact]
    public async Task A_refund_in_a_later_period_does_not_rewrite_the_period_that_was_paid()
    {
        Enable(FeatureKeys.Reports, FeatureKeys.OnlinePayments);
        var paidDay = Today(Now);
        var a = await BookAsync(await World.NewPatientAsync(), 10);
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(a)}/mark-paid")).StatusCode);
        Factory.Clock.Advance(TimeSpan.FromDays(40));
        var payment = await World.WithDbAsync(x => x.Payments.AsNoTracking().FirstAsync(p => p.AppointmentId == a));
        Assert.Equal(200, (await Staff.PostAsync($"/api/v1/dashboard/payments/{payment.Id}/refund")).StatusCode);
        var refundDay = Today(Now);

        var paidPeriod = (await Staff.GetAsync($"/api/v1/dashboard/reports/revenue?from={paidDay:yyyy-MM-dd}&to={paidDay:yyyy-MM-dd}"))["totals"];
        var refundPeriod = (await Staff.GetAsync($"/api/v1/dashboard/reports/revenue?from={refundDay:yyyy-MM-dd}&to={refundDay:yyyy-MM-dd}"))["totals"];

        Assert.Equal(300m, paidPeriod.GetProperty("collectedEgp").GetDecimal());
        Assert.Equal(0m, paidPeriod.GetProperty("refundsEgp").GetDecimal());
        Assert.Equal(0m, refundPeriod.GetProperty("collectedEgp").GetDecimal());
        Assert.Equal(300m, refundPeriod.GetProperty("refundsEgp").GetDecimal());
    }

    [Fact]
    public async Task Csv_export_has_a_bom_and_the_range_is_limited_to_366_days()
    {
        Enable(FeatureKeys.Reports);
        var today = Today(Now);

        var csv = await Staff.Client.GetAsync($"/api/v1/dashboard/reports/bookings?from={today:yyyy-MM-dd}&to={today:yyyy-MM-dd}&format=csv");
        var bytes = await csv.Content.ReadAsByteArrayAsync();

        Assert.Equal(System.Net.HttpStatusCode.OK, csv.StatusCode);
        Assert.Equal("text/csv", csv.Content.Headers.ContentType!.MediaType);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]);
        Assert.StartsWith("period,booked,completed,cancelled,noShow", System.Text.Encoding.UTF8.GetString(bytes[3..]));

        var revenueCsv = await Staff.Client.GetAsync("/api/v1/dashboard/reports/revenue?format=csv");
        Assert.StartsWith("period,expectedEgp,collectedEgp", System.Text.Encoding.UTF8.GetString(await revenueCsv.Content.ReadAsByteArrayAsync(), 3, 33));

        var tooLong = await Staff.GetAsync($"/api/v1/dashboard/reports/bookings?from={today.AddDays(-400):yyyy-MM-dd}&to={today:yyyy-MM-dd}");
        Assert.Equal(400, tooLong.StatusCode);
        var inverted = await Staff.GetAsync($"/api/v1/dashboard/reports/bookings?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}");
        Assert.Equal(400, inverted.StatusCode);
        Assert.Equal(403, (await (await World.NewPatientAsync()).GetAsync("/api/v1/dashboard/reports/bookings")).StatusCode);
    }
}

public class VideoTests(PostgresFixture db) : Phase2TestBase(db)
{
    private async Task OffersVideoAsync() => await SetAsync(async d =>
        await d.Doctors.Where(x => x.Id == Doctor.Id).ExecuteUpdateAsync(u => u.SetProperty(x => x.OffersVideo, true)));

    [Fact]
    public async Task Video_mode_needs_the_feature_and_a_doctor_who_offers_it()
    {
        var patient = await World.NewPatientAsync();
        Enable(FeatureKeys.VideoConsultation);

        var refused = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, mode = "Video" });
        Assert.Equal(422, refused.StatusCode);

        await OffersVideoAsync();
        Assert.True((await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}"))["offersVideo"].GetBoolean());
        var booked = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(3))).Id, mode = "Video" });
        Assert.Equal(201, booked.StatusCode);
        Assert.Equal("Video", booked["mode"].GetString());
    }

    [Fact]
    public async Task Join_url_opens_15_minutes_before_and_closes_30_minutes_after_the_visit()
    {
        Enable(FeatureKeys.VideoConsultation);
        await OffersVideoAsync();
        var patient = await World.NewPatientAsync();
        var slot = await SlotAsync(TimeSpan.FromHours(2));
        var booked = await patient.PostAsync("/api/v1/appointments", new { slotId = slot.Id, mode = "Video" });
        var id = booked.Id;
        Assert.Equal(200, (await Staff.PostAsync($"{Dash(id)}/approve")).StatusCode);

        var early = await patient.GetAsync($"{Patient(id)}/video");
        Assert.Equal(409, early.StatusCode);
        Assert.Equal("VIDEO_NOT_OPEN", early.Code);

        Factory.Clock.Advance(slot.StartsAt - Now - TimeSpan.FromMinutes(16));
        Assert.Equal("VIDEO_NOT_OPEN", (await patient.GetAsync($"{Patient(id)}/video")).Code);

        Factory.Clock.Advance(TimeSpan.FromMinutes(2));
        var open = await patient.GetAsync($"{Patient(id)}/video");
        Assert.Equal(200, open.StatusCode);
        var patientUrl = open["joinUrl"].GetString()!;
        Assert.StartsWith("https://meet.jit.si/", patientUrl);
        Assert.Equal(slot.StartsAt.AddMinutes(-15), open["opensAt"].GetDateTime().ToUniversalTime());
        Assert.Equal(slot.EndsAt.AddMinutes(30), open["closesAt"].GetDateTime().ToUniversalTime());

        // same room for everyone; the host link is for staff only
        var host = await Staff.GetAsync($"{Dash(id)}/video");
        Assert.Equal(200, host.StatusCode);
        Assert.Contains(new Uri(patientUrl).AbsolutePath.Split('/')[1], host["joinUrl"].GetString());
        Assert.Equal(patientUrl, (await patient.GetAsync($"{Patient(id)}/video"))["joinUrl"].GetString());

        Factory.Clock.Advance(slot.EndsAt - Now + TimeSpan.FromMinutes(31));
        Assert.Equal("VIDEO_NOT_OPEN", (await patient.GetAsync($"{Patient(id)}/video")).Code);
    }

    [Fact]
    public async Task In_person_appointments_and_other_patients_have_no_video()
    {
        Enable(FeatureKeys.VideoConsultation);
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));

        Assert.True((await patient.GetAsync($"{Patient(id)}/video")).StatusCode is 404 or 409);
        Assert.Equal(404, (await (await World.NewPatientAsync()).GetAsync($"{Patient(id)}/video")).StatusCode);
    }

    [Fact]
    public async Task Video_confirmation_message_mentions_the_video_consultation()
    {
        Enable(FeatureKeys.VideoConsultation);
        await OffersVideoAsync();
        var patient = await World.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { preferredLanguage = "en" });
        await RegisterDeviceAsync(patient);

        await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, mode = "Video" });

        Assert.Contains("video consultation", Factory.Push.Sent.Last().Message.Body);
    }
}
