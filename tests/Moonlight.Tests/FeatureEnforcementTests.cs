using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public class FeatureEnforcementTests(PostgresFixture db) : AppointmentTestBase(db)
{
    private void Set(string key, bool on)
    {
        var service = Factory.Services.GetRequiredService<FeatureService>();
        var features = service.Current.Features.ToDictionary(f => f.Key, f => f.Value);
        features[key] = on;
        service.Apply(features, FeatureSources.Console, DateTime.UtcNow);
    }

    private void AssertDisabled(Resp response, string feature)
    {
        Assert.Equal(403, response.StatusCode);
        Assert.Equal("FEATURE_DISABLED", response.Code);
        Assert.Equal(feature, response["feature"].GetString());
        Assert.Equal(403, response["status"].GetInt32());
    }

    // guestBrowsing

    [Fact]
    public async Task Guest_browsing_on_lets_anonymous_callers_search_and_view_doctors_and_slots()
    {
        var anonymous = World.Anonymous();

        Assert.Equal(200, (await anonymous.GetAsync("/api/v1/doctors")).StatusCode);
        Assert.Equal(200, (await anonymous.GetAsync($"/api/v1/doctors/{Doctor.Id}")).StatusCode);
        Assert.Equal(200, (await anonymous.GetAsync($"/api/v1/doctors/{Doctor.Id}/slots")).StatusCode);
    }

    [Fact]
    public async Task Guest_browsing_off_answers_401_to_anonymous_callers_on_search_profile_and_slots()
    {
        Set(FeatureKeys.GuestBrowsing, false);
        var anonymous = World.Anonymous();

        foreach (var url in new[] { "/api/v1/doctors", $"/api/v1/doctors/{Doctor.Id}", $"/api/v1/doctors/{Doctor.Id}/slots" })
        {
            var response = await anonymous.GetAsync(url);
            Assert.Equal(401, response.StatusCode);
            Assert.Equal("UNAUTHORIZED", response.Code);
        }

        // Reference data and other anonymous endpoints stay open.
        Assert.Equal(200, (await anonymous.GetAsync("/api/v1/specialties")).StatusCode);
        Assert.Equal(200, (await anonymous.GetAsync("/api/v1/features")).StatusCode);
    }

    [Fact]
    public async Task Guest_browsing_off_still_serves_signed_in_users()
    {
        Set(FeatureKeys.GuestBrowsing, false);
        var patient = await World.NewPatientAsync();

        Assert.Equal(200, (await patient.GetAsync("/api/v1/doctors")).StatusCode);
        Assert.Equal(200, (await patient.GetAsync($"/api/v1/doctors/{Doctor.Id}")).StatusCode);
        Assert.Equal(200, (await patient.GetAsync($"/api/v1/doctors/{Doctor.Id}/slots")).StatusCode);
    }

    // patientSelfService

    [Fact]
    public async Task Self_service_on_lets_patients_cancel_and_reschedule()
    {
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        var other = await SlotAsync(TimeSpan.FromDays(3));

        Assert.Equal(200, (await patient.PostAsync($"{Patient(id)}/reschedule", new { slotId = other.Id })).StatusCode);
        Assert.Equal(200, (await patient.PostAsync($"{Patient(id)}/cancel")).StatusCode);
    }

    [Theory]
    [InlineData("cancel")]
    [InlineData("reschedule")]
    public async Task Self_service_off_answers_403_feature_disabled_and_leaves_the_appointment_alone(string action)
    {
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        var other = await SlotAsync(TimeSpan.FromDays(3));
        Set(FeatureKeys.PatientSelfService, false);

        var response = await patient.PostAsync($"{Patient(id)}/{action}", new { slotId = other.Id });

        AssertDisabled(response, FeatureKeys.PatientSelfService);
        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task Feature_disabled_problems_are_localized()
    {
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        Set(FeatureKeys.PatientSelfService, false);

        var english = await patient.SendAsync(HttpMethod.Post, $"{Patient(id)}/cancel", new { }, "en");
        var arabic = await patient.SendAsync(HttpMethod.Post, $"{Patient(id)}/cancel", new { }, "ar");

        Assert.Equal("Feature unavailable", english["title"].GetString());
        Assert.Equal("الخدمة غير متاحة", arabic["title"].GetString());
        Assert.EndsWith("/feature-disabled", english["type"].GetString());
    }

    [Fact]
    public async Task Self_service_off_does_not_stop_booking_or_the_clinic_from_cancelling()
    {
        Set(FeatureKeys.PatientSelfService, false);
        var patient = await World.NewPatientAsync();

        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        var cancelled = await Staff.PostAsync($"{Dash(id)}/cancel", new { reason = "doctor unavailable" });

        Assert.Equal(200, cancelled.StatusCode);
    }

    // pushReminders and smsNotifications

    [Fact]
    public async Task Push_reminders_on_sends_confirmation_and_reminder_pushes()
    {
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);

        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        await using var scope = Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<Api.Features.Notifications.INotificationService>().AppointmentReminderAsync(id);

        Assert.Equal(3, Factory.Push.Sent.Count);
    }

    [Fact]
    public async Task Push_reminders_off_skips_confirmation_and_reminder_pushes_but_not_clinic_cancellations()
    {
        Set(FeatureKeys.PushReminders, false);
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);

        var id = await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        await using var scope = Factory.Services.CreateAsyncScope();
        var notifications = scope.ServiceProvider.GetRequiredService<Api.Features.Notifications.INotificationService>();
        await notifications.AppointmentReminderAsync(id);
        Assert.Empty(Factory.Push.Sent);
        Assert.Empty(Factory.Sms.Sent);

        await Staff.PostAsync($"{Dash(id)}/cancel", new { reason = "closed" });
        Assert.Equal("appointment.cancelled", Assert.Single(Factory.Push.Sent).Message.Data["type"]);
    }

    [Fact]
    public async Task The_sms_feature_on_sends_sms_to_patients_without_a_device_even_when_the_sms_setting_is_off()
    {
        var withoutDevice = await World.NewPatientAsync();
        await World.BookAsync(withoutDevice, await SlotAsync(TimeSpan.FromDays(1)));
        Assert.Empty(Factory.Sms.Sent);

        Set(FeatureKeys.SmsNotifications, true);
        var second = await World.NewPatientAsync();
        await World.BookAsync(second, await SlotAsync(TimeSpan.FromDays(2)));

        Assert.Equal(2, Factory.Sms.Sent.Count);
        Assert.All(Factory.Sms.Sent, sms => Assert.Equal(second.User!.Phone, sms.Phone));
    }

    [Fact]
    public async Task With_pushes_off_and_sms_on_a_patient_with_a_device_gets_an_sms_instead()
    {
        Set(FeatureKeys.PushReminders, false);
        Set(FeatureKeys.SmsNotifications, true);
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);

        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));

        Assert.Empty(Factory.Push.Sent);
        Assert.Equal(2, Factory.Sms.Sent.Count);
        Assert.All(Factory.Sms.Sent, sms => Assert.Equal(patient.User!.Phone, sms.Phone));
    }

    [Fact]
    public async Task The_otp_sms_uses_the_configured_clinic_name_and_is_not_gated_by_the_sms_feature()
    {
        var phone = TestWorld.NewPhone();

        var response = await World.Anonymous().PostAsync("/api/v1/auth/otp/request", new { phone });

        Assert.Equal(204, response.StatusCode);
        var message = Assert.Single(Factory.Sms.Sent).Message;
        var clinic = Factory.Services.GetRequiredService<ClinicConfig>();
        Assert.Contains(clinic.Name.Ar, message);
        Assert.Contains($"Your {clinic.Name.En} code", message);
    }
}

// Needs an empty database: with multiBranch off the first clinic is allowed and a second is not.
public sealed class MultiBranchTests : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly PostgresFixture _own = new();
    private MoonlightApiFactory _factory = null!;
    private TestWorld _world = null!;

    public async Task InitializeAsync()
    {
        await _own.InitializeAsync();
        _factory = new MoonlightApiFactory(_own.ConnectionString);
        _world = new TestWorld(_factory);
    }

    public async Task DisposeAsync()
    {
        await _factory.DisposeAsync();
        await _own.DisposeAsync();
    }

    private static object NewClinic(string name) =>
        new { nameAr = "عيادة", nameEn = name, addressAr = "ع", addressEn = "A", city = "Cairo", phone = "+20221111111" };

    [Fact]
    public async Task Multi_branch_off_allows_the_first_clinic_and_rejects_a_second()
    {
        var service = _factory.Services.GetRequiredService<FeatureService>();
        service.Apply(new Dictionary<string, bool> { [FeatureKeys.MultiBranch] = false }, FeatureSources.Console, DateTime.UtcNow);
        var admin = await _world.LoginAsync(await _world.NewAdminAsync());

        Assert.Equal(201, (await admin.PostAsync("/api/v1/admin/clinics", NewClinic("First"))).StatusCode);

        var second = await admin.PostAsync("/api/v1/admin/clinics", NewClinic("Second"));
        Assert.Equal(403, second.StatusCode);
        Assert.Equal("FEATURE_DISABLED", second.Code);
        Assert.Equal(FeatureKeys.MultiBranch, second["feature"].GetString());
    }

    [Fact]
    public async Task Multi_branch_on_allows_any_number_of_clinics()
    {
        var admin = await _world.LoginAsync(await _world.NewAdminAsync());
        await _world.NewClinicAsync("Existing");

        Assert.Equal(201, (await admin.PostAsync("/api/v1/admin/clinics", NewClinic("One"))).StatusCode);
        Assert.Equal(201, (await admin.PostAsync("/api/v1/admin/clinics", NewClinic("Two"))).StatusCode);
    }
}
