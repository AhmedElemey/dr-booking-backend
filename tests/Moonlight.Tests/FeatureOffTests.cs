using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

// Every endpoint of a Phase 2 feature answers 403 FEATURE_DISABLED while its switch is off (the template default).
public class FeatureOffTests(PostgresFixture db) : Phase2TestBase(db)
{
    public enum Who { Anonymous, Patient, Staff }

    private static readonly Guid Id = Guid.Parse("11111111-1111-1111-1111-111111111111");

    public static IEnumerable<object[]> Endpoints()
    {
        object[] E(string feature, string method, string url, Who who) => [feature, method, url, who];

        // holidayCalendar
        yield return E(FeatureKeys.HolidayCalendar, "GET", $"/api/v1/clinics/{Id}/holidays", Who.Anonymous);
        yield return E(FeatureKeys.HolidayCalendar, "GET", "/api/v1/dashboard/holidays", Who.Staff);
        yield return E(FeatureKeys.HolidayCalendar, "POST", "/api/v1/dashboard/holidays", Who.Staff);
        yield return E(FeatureKeys.HolidayCalendar, "DELETE", $"/api/v1/dashboard/holidays/{Id}", Who.Staff);
        yield return E(FeatureKeys.HolidayCalendar, "POST", "/api/v1/dashboard/holidays/import-egypt", Who.Staff);
        yield return E(FeatureKeys.HolidayCalendar, "GET", $"/api/v1/dashboard/holidays/{Id}/conflicts", Who.Staff);

        // workingHoursTemplates
        yield return E(FeatureKeys.WorkingHoursTemplates, "GET", "/api/v1/dashboard/working-hours-templates", Who.Staff);
        yield return E(FeatureKeys.WorkingHoursTemplates, "POST", "/api/v1/dashboard/working-hours-templates", Who.Staff);
        yield return E(FeatureKeys.WorkingHoursTemplates, "PUT", $"/api/v1/dashboard/working-hours-templates/{Id}", Who.Staff);
        yield return E(FeatureKeys.WorkingHoursTemplates, "DELETE", $"/api/v1/dashboard/working-hours-templates/{Id}", Who.Staff);
        yield return E(FeatureKeys.WorkingHoursTemplates, "POST", $"/api/v1/dashboard/doctors/{Id}/slots/apply-template", Who.Staff);

        // waitingList
        yield return E(FeatureKeys.WaitingList, "POST", "/api/v1/waiting-list", Who.Patient);
        yield return E(FeatureKeys.WaitingList, "GET", "/api/v1/waiting-list", Who.Patient);
        yield return E(FeatureKeys.WaitingList, "DELETE", $"/api/v1/waiting-list/{Id}", Who.Patient);
        yield return E(FeatureKeys.WaitingList, "GET", "/api/v1/dashboard/waiting-list", Who.Staff);
        yield return E(FeatureKeys.WaitingList, "POST", $"/api/v1/dashboard/waiting-list/{Id}/notify", Who.Staff);

        // familyAccounts
        yield return E(FeatureKeys.FamilyAccounts, "GET", "/api/v1/me/family", Who.Patient);
        yield return E(FeatureKeys.FamilyAccounts, "POST", "/api/v1/me/family", Who.Patient);
        yield return E(FeatureKeys.FamilyAccounts, "PUT", $"/api/v1/me/family/{Id}", Who.Patient);
        yield return E(FeatureKeys.FamilyAccounts, "DELETE", $"/api/v1/me/family/{Id}", Who.Patient);

        // whatsappReminders
        yield return E(FeatureKeys.WhatsappReminders, "GET", "/api/v1/dashboard/messages", Who.Staff);

        // onlinePayments
        yield return E(FeatureKeys.OnlinePayments, "POST", $"/api/v1/appointments/{Id}/pay", Who.Patient);
        yield return E(FeatureKeys.OnlinePayments, "GET", $"/api/v1/payments/{Id}", Who.Patient);
        yield return E(FeatureKeys.OnlinePayments, "POST", "/api/v1/payments/webhook/mock", Who.Anonymous);
        yield return E(FeatureKeys.OnlinePayments, "GET", $"/api/v1/payments/mock-checkout/{Id}", Who.Anonymous);
        yield return E(FeatureKeys.OnlinePayments, "GET", "/api/v1/dashboard/payments", Who.Staff);
        yield return E(FeatureKeys.OnlinePayments, "POST", $"/api/v1/dashboard/appointments/{Id}/mark-paid", Who.Staff);
        yield return E(FeatureKeys.OnlinePayments, "POST", $"/api/v1/dashboard/payments/{Id}/refund", Who.Staff);

        // insurance
        yield return E(FeatureKeys.Insurance, "GET", "/api/v1/insurance-providers", Who.Anonymous);
        yield return E(FeatureKeys.Insurance, "GET", "/api/v1/dashboard/insurance-providers", Who.Staff);
        yield return E(FeatureKeys.Insurance, "POST", "/api/v1/dashboard/insurance-providers", Who.Staff);
        yield return E(FeatureKeys.Insurance, "PUT", $"/api/v1/dashboard/insurance-providers/{Id}", Who.Staff);
        yield return E(FeatureKeys.Insurance, "DELETE", $"/api/v1/dashboard/insurance-providers/{Id}", Who.Staff);
        yield return E(FeatureKeys.Insurance, "PUT", $"/api/v1/dashboard/doctors/{Id}/insurance-providers", Who.Staff);
        yield return E(FeatureKeys.Insurance, "POST", $"/api/v1/dashboard/appointments/{Id}/insurance", Who.Staff);

        // patientRecords
        yield return E(FeatureKeys.PatientRecords, "GET", "/api/v1/dashboard/patients", Who.Staff);
        yield return E(FeatureKeys.PatientRecords, "GET", $"/api/v1/dashboard/patients/{Id}", Who.Staff);
        yield return E(FeatureKeys.PatientRecords, "PUT", $"/api/v1/dashboard/patients/{Id}/medical-info", Who.Staff);
        yield return E(FeatureKeys.PatientRecords, "POST", $"/api/v1/dashboard/patients/{Id}/records", Who.Staff);
        yield return E(FeatureKeys.PatientRecords, "PUT", $"/api/v1/dashboard/records/{Id}", Who.Staff);
        yield return E(FeatureKeys.PatientRecords, "DELETE", $"/api/v1/dashboard/records/{Id}", Who.Staff);
        yield return E(FeatureKeys.PatientRecords, "GET", "/api/v1/me/records", Who.Patient);

        // reviews
        yield return E(FeatureKeys.Reviews, "POST", $"/api/v1/appointments/{Id}/review", Who.Patient);
        yield return E(FeatureKeys.Reviews, "GET", $"/api/v1/doctors/{Id}/reviews", Who.Anonymous);
        yield return E(FeatureKeys.Reviews, "GET", "/api/v1/dashboard/reviews", Who.Staff);
        yield return E(FeatureKeys.Reviews, "POST", $"/api/v1/dashboard/reviews/{Id}/hide", Who.Staff);
        yield return E(FeatureKeys.Reviews, "POST", $"/api/v1/dashboard/reviews/{Id}/publish", Who.Staff);
        yield return E(FeatureKeys.Reviews, "PUT", $"/api/v1/dashboard/reviews/{Id}/reply", Who.Staff);

        // promotions
        yield return E(FeatureKeys.Promotions, "GET", "/api/v1/promotions", Who.Anonymous);
        yield return E(FeatureKeys.Promotions, "POST", "/api/v1/coupons/validate", Who.Patient);
        yield return E(FeatureKeys.Promotions, "GET", "/api/v1/dashboard/coupons", Who.Staff);
        yield return E(FeatureKeys.Promotions, "POST", "/api/v1/dashboard/coupons", Who.Staff);
        yield return E(FeatureKeys.Promotions, "GET", $"/api/v1/dashboard/coupons/{Id}", Who.Staff);
        yield return E(FeatureKeys.Promotions, "PUT", $"/api/v1/dashboard/coupons/{Id}", Who.Staff);
        yield return E(FeatureKeys.Promotions, "DELETE", $"/api/v1/dashboard/coupons/{Id}", Who.Staff);
        yield return E(FeatureKeys.Promotions, "GET", $"/api/v1/dashboard/coupons/{Id}/redemptions", Who.Staff);

        // recallCampaigns
        yield return E(FeatureKeys.RecallCampaigns, "GET", "/api/v1/dashboard/recall-campaigns", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "POST", "/api/v1/dashboard/recall-campaigns", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "GET", $"/api/v1/dashboard/recall-campaigns/{Id}", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "PUT", $"/api/v1/dashboard/recall-campaigns/{Id}", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "DELETE", $"/api/v1/dashboard/recall-campaigns/{Id}", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "POST", $"/api/v1/dashboard/recall-campaigns/{Id}/preview", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "POST", $"/api/v1/dashboard/recall-campaigns/{Id}/send-now", Who.Staff);
        yield return E(FeatureKeys.RecallCampaigns, "GET", $"/api/v1/dashboard/recall-campaigns/{Id}/sends", Who.Staff);

        // reports
        yield return E(FeatureKeys.Reports, "GET", "/api/v1/dashboard/reports/bookings", Who.Staff);
        yield return E(FeatureKeys.Reports, "GET", "/api/v1/dashboard/reports/revenue", Who.Staff);

        // videoConsultation
        yield return E(FeatureKeys.VideoConsultation, "GET", $"/api/v1/appointments/{Id}/video", Who.Patient);
        yield return E(FeatureKeys.VideoConsultation, "GET", $"/api/v1/dashboard/appointments/{Id}/video", Who.Staff);
    }

    [Theory]
    [MemberData(nameof(Endpoints))]
    public async Task Endpoint_answers_403_FEATURE_DISABLED_while_the_feature_is_off(string feature, string method, string url, Who who)
    {
        var session = who switch
        {
            Who.Anonymous => World.Anonymous(),
            Who.Staff => Staff,
            _ => await World.NewPatientAsync(),
        };

        var response = await session.SendAsync(new HttpMethod(method), url, method is "POST" or "PUT" ? new { } : null);

        Assert.Equal(403, response.StatusCode);
        Assert.Equal("FEATURE_DISABLED", response.Code);
        Assert.Equal(feature, response["feature"].GetString());
    }

    [Fact]
    public async Task Feature_only_booking_fields_are_ignored_while_features_are_off()
    {
        var patient = await World.NewPatientAsync();
        var slot = await SlotAsync(TimeSpan.FromDays(2));

        var response = await patient.PostAsync("/api/v1/appointments", new
        {
            slotId = slot.Id,
            familyMemberId = Guid.NewGuid(),
            couponCode = "NOPE",
            mode = "Video",
            insurance = new { providerId = Guid.NewGuid(), memberNumber = "1" },
        });

        Assert.Equal(201, response.StatusCode);
        Assert.Equal("InPerson", response["mode"].GetString());
        Assert.Equal(JsonValueKindNull, response["familyMember"].ValueKind);
        Assert.Equal(0m, response["discountEgp"].GetDecimal());
    }

    private const System.Text.Json.JsonValueKind JsonValueKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Public_doctor_dtos_show_empty_phase2_fields_while_features_are_off()
    {
        var response = await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}");

        Assert.Equal(200, response.StatusCode);
        Assert.Empty(response["insuranceProviders"].EnumerateArray());
        Assert.Equal(JsonValueKindNull, response["rating"].ValueKind);
        Assert.False(response["offersVideo"].GetBoolean());
        Assert.Empty((await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}/slots"))["closedDates"].EnumerateArray());
    }
}
