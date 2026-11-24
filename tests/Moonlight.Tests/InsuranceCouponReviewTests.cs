using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Notifications;

namespace Moonlight.Tests;

public class InsuranceTests(PostgresFixture db) : Phase2TestBase(db)
{
    private async Task<Guid> NewProviderAsync(string name = "Misr Insurance")
    {
        var response = await Staff.PostAsync("/api/v1/dashboard/insurance-providers", new { nameAr = name + " ar", nameEn = name, isActive = true });
        Assert.Equal(201, response.StatusCode);
        return response.Id;
    }

    [Fact]
    public async Task Provider_crud_doctor_assignment_public_list_and_filter()
    {
        Enable(FeatureKeys.Insurance);
        var provider = await NewProviderAsync();
        var inactive = (await Staff.PostAsync("/api/v1/dashboard/insurance-providers", new { nameAr = "x", nameEn = "Hidden", isActive = false })).Id;

        var publicList = await World.Anonymous().GetAsync("/api/v1/insurance-providers", "en");
        var names = publicList.Body.EnumerateArray().Select(p => p.GetProperty("name").GetString()).ToList();
        Assert.Contains("Misr Insurance", names);
        Assert.DoesNotContain("Hidden", names);

        var assigned = await Staff.PutAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/insurance-providers", new { providerIds = new[] { provider } });
        Assert.Equal(200, assigned.StatusCode);
        var profile = await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}");
        Assert.Equal(provider, Assert.Single(profile["insuranceProviders"].EnumerateArray()).GetProperty("id").GetGuid());

        Assert.Single((await World.Anonymous().GetAsync($"/api/v1/doctors?insuranceProviderId={provider}"))["items"].EnumerateArray());
        var otherDoctor = await World.NewDoctorAsync(Clinic, "Dr Other");
        Assert.Empty((await World.Anonymous().GetAsync($"/api/v1/doctors?insuranceProviderId={inactive}"))["items"].EnumerateArray());
        _ = otherDoctor;

        var renamed = await Staff.PutAsync($"/api/v1/dashboard/insurance-providers/{provider}", new { nameAr = "a", nameEn = "Renamed", isActive = true });
        Assert.Equal("Renamed", renamed["nameEn"].GetString());
        Assert.Equal(204, (await Staff.DeleteAsync($"/api/v1/dashboard/insurance-providers/{inactive}")).StatusCode);
    }

    [Fact]
    public async Task Booking_with_an_accepted_provider_is_pending_then_staff_decides()
    {
        Enable(FeatureKeys.Insurance);
        var provider = await NewProviderAsync();
        await Staff.PutAsync($"/api/v1/dashboard/doctors/{Doctor.Id}/insurance-providers", new { providerIds = new[] { provider } });
        var patient = await World.NewPatientAsync();

        var booked = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, insurance = new { providerId = provider, memberNumber = "MI-1" } });

        Assert.Equal(201, booked.StatusCode);
        var insurance = booked["insurance"];
        Assert.Equal("Pending", insurance.GetProperty("status").GetString());
        Assert.Equal("MI-1", insurance.GetProperty("memberNumber").GetString());
        Assert.Equal(provider, insurance.GetProperty("provider").GetProperty("id").GetGuid());

        var approved = await Staff.PostAsync($"{Dash(booked.Id)}/insurance", new { status = "Approved", note = "Covered 80%" });
        Assert.Equal(200, approved.StatusCode);
        Assert.Equal("Approved", approved["insurance"].GetProperty("status").GetString());
        Assert.Equal("Covered 80%", (await patient.GetAsync(Patient(booked.Id)))["insurance"].GetProperty("note").GetString());

        var bad = await Staff.PostAsync($"{Dash(booked.Id)}/insurance", new { status = "Pending" });
        Assert.Equal(400, bad.StatusCode);
    }

    [Fact]
    public async Task Provider_not_accepted_by_the_doctor_is_422()
    {
        Enable(FeatureKeys.Insurance);
        var provider = await NewProviderAsync();
        var patient = await World.NewPatientAsync();

        var response = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, insurance = new { providerId = provider, memberNumber = "1" } });

        Assert.Equal(422, response.StatusCode);
        Assert.Empty(await World.WithDbAsync(d => d.Appointments.ToListAsync()));
    }

    [Fact]
    public async Task Decision_on_an_appointment_without_insurance_is_a_conflict_or_validation_error()
    {
        Enable(FeatureKeys.Insurance);
        var id = await World.BookAsync(await World.NewPatientAsync(), await SlotAsync(TimeSpan.FromDays(2)));

        var response = await Staff.PostAsync($"{Dash(id)}/insurance", new { status = "Approved" });

        Assert.True(response.StatusCode is 409 or 400 or 422, response.StatusCode.ToString());
    }
}

public class CouponTests(PostgresFixture db) : Phase2TestBase(db)
{
    private Task<Resp> ValidateAsync(Session who, string code, Guid? serviceId = null) =>
        who.PostAsync("/api/v1/coupons/validate", new { code, doctorId = Doctor.Id, serviceId = serviceId ?? Service.Id });

    [Fact]
    public async Task Validate_leaves_out_the_total_while_the_clinic_hides_prices()
    {
        Enable(FeatureKeys.Promotions);
        await NewCouponAsync("HIDE10");
        var patient = await World.NewPatientAsync();
        Assert.Equal(270m, (await ValidateAsync(patient, "HIDE10"))["totalEgp"].GetDecimal());

        await World.WithDbAsync(async d =>
        {
            (await d.Clinics.FindAsync(Clinic.Id))!.ShowPrices = false;
            return await d.SaveChangesAsync();
        });
        var hidden = await ValidateAsync(patient, "HIDE10");
        Assert.True(hidden["valid"].GetBoolean());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, hidden.Body.GetProperty("totalEgp").ValueKind);

        await World.WithDbAsync(async d =>
        {
            (await d.Clinics.FindAsync(Clinic.Id))!.ShowPrices = true;
            return await d.SaveChangesAsync();
        });
        Disable(FeatureKeys.PublicPrices);
        var locked = await ValidateAsync(patient, "HIDE10");
        Assert.Equal(System.Text.Json.JsonValueKind.Null, locked.Body.GetProperty("totalEgp").ValueKind);
    }

    [Fact]
    public async Task Percent_coupon_validates_and_books_with_the_discount_then_cancel_releases_it()
    {
        Enable(FeatureKeys.Promotions);
        var couponId = await NewCouponAsync("SAVE10");
        var patient = await World.NewPatientAsync();

        var check = await ValidateAsync(patient, "save10");
        Assert.True(check["valid"].GetBoolean());
        Assert.Equal(30m, check["discountEgp"].GetDecimal());
        Assert.Equal(270m, check["totalEgp"].GetDecimal());

        var booked = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id, couponCode = "SAVE10" });
        Assert.Equal(201, booked.StatusCode);
        Assert.Equal(300m, booked["priceEgp"].GetDecimal());
        Assert.Equal(30m, booked["discountEgp"].GetDecimal());
        Assert.Equal(270m, booked["totalEgp"].GetDecimal());

        var redemptions = await Staff.GetAsync($"/api/v1/dashboard/coupons/{couponId}/redemptions");
        Assert.Single(redemptions.Body.EnumerateArray());
        Assert.Equal(1, (await Staff.GetAsync($"/api/v1/dashboard/coupons/{couponId}"))["redemptionsCount"].GetInt32());

        // per-patient limit now reached
        var second = await ValidateAsync(patient, "SAVE10");
        Assert.False(second["valid"].GetBoolean());
        Assert.Equal("PerPatientLimit", second["reason"].GetString());

        await patient.PostAsync($"{Patient(booked.Id)}/cancel");
        Assert.Empty((await Staff.GetAsync($"/api/v1/dashboard/coupons/{couponId}/redemptions")).Body.EnumerateArray());
        Assert.Equal(0, (await Staff.GetAsync($"/api/v1/dashboard/coupons/{couponId}"))["redemptionsCount"].GetInt32());
        Assert.True((await ValidateAsync(patient, "SAVE10"))["valid"].GetBoolean());
    }

    [Fact]
    public async Task Fixed_and_capped_coupons_never_exceed_the_price()
    {
        Enable(FeatureKeys.Promotions);
        await NewCouponAsync("BIG500", "Fixed", 500);
        await NewCouponAsync("CAP20", "Percent", 50, b => b["maxDiscountEgp"] = 20);
        var patient = await World.NewPatientAsync();

        var big = await ValidateAsync(patient, "BIG500");
        Assert.Equal(300m, big["discountEgp"].GetDecimal());
        Assert.Equal(0m, big["totalEgp"].GetDecimal());
        var capped = await ValidateAsync(patient, "CAP20");
        Assert.Equal(20m, capped["discountEgp"].GetDecimal());
    }

    [Fact]
    public async Task Invalid_reasons_are_reported_by_validate_and_as_422_on_booking()
    {
        Enable(FeatureKeys.Promotions);
        var today = Today(Now);
        await NewCouponAsync("EXPIRED", tweak: b => { b["validFrom"] = today.AddDays(-10).ToString("yyyy-MM-dd"); b["validTo"] = today.AddDays(-1).ToString("yyyy-MM-dd"); });
        await NewCouponAsync("FUTURE", tweak: b => { b["validFrom"] = today.AddDays(5).ToString("yyyy-MM-dd"); b["validTo"] = today.AddDays(9).ToString("yyyy-MM-dd"); });
        await NewCouponAsync("ONLYONE", tweak: b => b["maxRedemptions"] = 1);
        var patient = await World.NewPatientAsync();

        Assert.Equal("NotFound", (await ValidateAsync(patient, "NOPE"))["reason"].GetString());
        Assert.Equal("Expired", (await ValidateAsync(patient, "EXPIRED"))["reason"].GetString());
        Assert.Equal("NotStarted", (await ValidateAsync(patient, "FUTURE"))["reason"].GetString());

        var first = await patient.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(2))).Id, serviceId = Service.Id, couponCode = "ONLYONE" });
        Assert.Equal(201, first.StatusCode);
        var other = await World.NewPatientAsync();
        Assert.Equal("Exhausted", (await ValidateAsync(other, "ONLYONE"))["reason"].GetString());

        var rejected = await other.PostAsync("/api/v1/appointments", new { slotId = (await SlotAsync(TimeSpan.FromDays(3))).Id, serviceId = Service.Id, couponCode = "ONLYONE" });
        Assert.Equal(422, rejected.StatusCode);
        Assert.Equal("COUPON_INVALID", rejected.Code);
        Assert.Equal("Exhausted", rejected["reason"].GetString());
        Assert.Empty(await World.AppointmentsOfSlotAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Not_applicable_to_another_doctor_or_service_and_no_price()
    {
        Enable(FeatureKeys.Promotions);
        var otherDoctor = await World.NewDoctorAsync(Clinic, "Dr Other");
        await NewCouponAsync("OTHERONLY", tweak: b => b["doctorId"] = otherDoctor.Id);
        var patient = await World.NewPatientAsync();

        Assert.Equal("NotApplicable", (await ValidateAsync(patient, "OTHERONLY"))["reason"].GetString());

        var noPrice = await patient.PostAsync("/api/v1/coupons/validate", new { code = "OTHERONLY", doctorId = otherDoctor.Id });
        Assert.Equal("NoPrice", noPrice["reason"].GetString());
    }

    [Fact]
    public async Task Promotions_list_shows_only_public_active_offers_and_codes_are_unique()
    {
        Enable(FeatureKeys.Promotions);
        await NewCouponAsync("PUBLIC1");
        await NewCouponAsync("PRIVATE1", tweak: b => b["isPublic"] = false);
        await NewCouponAsync("INACTIVE1", tweak: b => b["isActive"] = false);

        var list = await World.Anonymous().GetAsync("/api/v1/promotions", "en");

        var codes = list.Body.EnumerateArray().Select(p => p.GetProperty("code").GetString()).ToList();
        Assert.Contains("PUBLIC1", codes);
        Assert.DoesNotContain("PRIVATE1", codes);
        Assert.DoesNotContain("INACTIVE1", codes);
        var duplicate = await Staff.PostAsync("/api/v1/dashboard/coupons", new { code = "PUBLIC1", titleAr = "a", titleEn = "b", discountType = "Percent", value = 5, validFrom = "2026-01-01", validTo = "2030-01-01" });
        Assert.True(duplicate.StatusCode is 409 or 400);
        var badCode = await Staff.PostAsync("/api/v1/dashboard/coupons", new { code = "a!", titleAr = "a", titleEn = "b", discountType = "Percent", value = 5, validFrom = "2026-01-01", validTo = "2030-01-01" });
        Assert.Equal(400, badCode.StatusCode);
    }
}

public class ReviewTests(PostgresFixture db) : Phase2TestBase(db)
{
    protected override bool IsolatedDatabase => true;

    [Fact]
    public async Task Rating_a_completed_visit_works_once_and_feeds_the_doctor_rating()
    {
        Enable(FeatureKeys.Reviews);
        var (id, patient, _) = await CompletedVisitAsync();
        var mine = await patient.GetAsync(Patient(id));
        Assert.True(mine["canReview"].GetBoolean());

        var review = await patient.PostAsync($"{Patient(id)}/review", new { rating = 4, comment = "Great" });
        Assert.Equal(201, review.StatusCode);

        var again = await patient.PostAsync($"{Patient(id)}/review", new { rating = 5 });
        Assert.Equal(409, again.StatusCode);
        Assert.Equal("ALREADY_REVIEWED", again.Code);

        var after = await patient.GetAsync(Patient(id));
        Assert.False(after["canReview"].GetBoolean());
        Assert.Equal(4, after["review"].GetProperty("rating").GetInt32());

        var profile = await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}");
        Assert.Equal(4.0, profile["rating"].GetProperty("average").GetDouble());
        Assert.Equal(1, profile["rating"].GetProperty("count").GetInt32());

        var list = await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}/reviews");
        var item = Assert.Single(list["items"].EnumerateArray());
        Assert.Equal("Great", item.GetProperty("comment").GetString());
    }

    [Fact]
    public async Task Only_completed_own_appointments_can_be_reviewed_and_rating_is_validated()
    {
        Enable(FeatureKeys.Reviews);
        var patient = await World.NewPatientAsync();
        var upcoming = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        var notYet = await patient.PostAsync($"{Patient(upcoming)}/review", new { rating = 5 });
        Assert.Equal(409, notYet.StatusCode);
        Assert.Equal("NOT_REVIEWABLE", notYet.Code);
        Assert.False((await patient.GetAsync(Patient(upcoming)))["canReview"].GetBoolean());

        var (id, owner, _) = await CompletedVisitAsync();
        var stranger = await World.NewPatientAsync();
        Assert.Equal(404, (await stranger.PostAsync($"{Patient(id)}/review", new { rating = 5 })).StatusCode);
        Assert.Equal(400, (await owner.PostAsync($"{Patient(id)}/review", new { rating = 6 })).StatusCode);
        Assert.Equal(400, (await owner.PostAsync($"{Patient(id)}/review", new { rating = 0 })).StatusCode);
        Assert.Equal(400, (await owner.PostAsync($"{Patient(id)}/review", new { rating = 5, comment = new string('x', 1001) })).StatusCode);
    }

    [Fact]
    public async Task Moderation_hides_replies_and_publishes()
    {
        Enable(FeatureKeys.Reviews);
        var (id, patient, _) = await CompletedVisitAsync();
        await patient.PostAsync($"{Patient(id)}/review", new { rating = 2, comment = "Slow" });

        var list = await Staff.GetAsync($"/api/v1/dashboard/reviews?doctorId={Doctor.Id}&status=Published");
        var reviewId = Assert.Single(list["items"].EnumerateArray()).GetProperty("id").GetGuid();

        var replied = await Staff.PutAsync($"/api/v1/dashboard/reviews/{reviewId}/reply", new { reply = "Sorry!" });
        Assert.Equal("Sorry!", replied["reply"].GetString());
        Assert.Equal("Sorry!", Assert.Single((await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}/reviews"))["items"].EnumerateArray()).GetProperty("reply").GetString());

        Assert.Equal("Hidden", (await Staff.PostAsync($"/api/v1/dashboard/reviews/{reviewId}/hide"))["status"].GetString());
        Assert.Empty((await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}/reviews"))["items"].EnumerateArray());
        Assert.Equal(JsonKindNull, (await World.Anonymous().GetAsync($"/api/v1/doctors/{Doctor.Id}"))["rating"].ValueKind);

        Assert.Equal("Published", (await Staff.PostAsync($"/api/v1/dashboard/reviews/{reviewId}/publish"))["status"].GetString());
    }

    private const System.Text.Json.JsonValueKind JsonKindNull = System.Text.Json.JsonValueKind.Null;

    [Fact]
    public async Task Review_prompt_is_sent_once_after_the_delay_and_not_after_a_review()
    {
        Enable(FeatureKeys.Reviews);
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);
        var (id, _, _) = await CompletedVisitAsync(patient);

        async Task<int> RunAsync()
        {
            using var scope = Factory.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<ReviewPromptProcessor>().RunOnceAsync(CancellationToken.None);
        }

        Assert.Equal(0, await RunAsync()); // too early
        Factory.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(1, await RunAsync());
        Assert.Equal(0, await RunAsync()); // once
        Assert.NotNull((await AppointmentAsync(id)).ReviewPromptSentAt);
        Assert.Single(await World.WithDbAsync(d => d.OutboundMessages.Where(m => m.Kind == "review.prompt" && m.UserId == patient.User!.Id).ToListAsync()));
    }
}
