using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

using Moonlight.Api.Domain;

namespace Moonlight.Tests;

public class AuthTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly MoonlightApiFactory _factory = new(db.ConnectionString);
    private TestWorld _world = null!;

    public Task InitializeAsync()
    {
        _world = new TestWorld(_factory);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private static string CodeFrom(RecordingSmsSender sms, string phone)
    {
        var message = sms.Sent.Last(s => s.Phone == phone).Message;
        return System.Text.RegularExpressions.Regex.Match(message, @"\d{6}").Value;
    }

    private void SetWhatsappReminders(bool on)
    {
        var service = _factory.Services.GetRequiredService<Api.Features.Flags.FeatureService>();
        var features = service.Current.Features.ToDictionary(f => f.Key, f => f.Value);
        features[Api.Features.Flags.FeatureKeys.WhatsappReminders] = on;
        service.Apply(features, Api.Features.Flags.FeatureSources.Console, DateTime.UtcNow);
    }

    [Fact]
    public async Task Login_code_goes_by_sms_while_whatsappReminders_is_off()
    {
        SetWhatsappReminders(false);
        var phone = TestWorld.NewPhone();

        Assert.Equal(204, (await _world.Anonymous().PostAsync("/api/v1/auth/otp/request", new { phone })).StatusCode);

        Assert.Equal(phone, Assert.Single(_factory.Sms.Sent).Phone);
        Assert.Empty(_factory.WhatsApp.Sent);
    }

    [Fact]
    public async Task Login_code_goes_by_whatsapp_when_whatsappReminders_is_on_and_verifies()
    {
        SetWhatsappReminders(true);
        var phone = TestWorld.NewPhone();

        Assert.Equal(204, (await _world.Anonymous().PostAsync("/api/v1/auth/otp/request", new { phone })).StatusCode);

        Assert.Empty(_factory.Sms.Sent);
        var sent = Assert.Single(_factory.WhatsApp.Sent);
        Assert.Equal(phone, sent.Phone);
        Assert.Equal("otp_code", sent.Template);
        var code = Assert.Single(sent.Parameters);
        Assert.Equal(200, (await _world.Anonymous().PostAsync("/api/v1/auth/otp/verify", new { phone, code })).StatusCode);
    }

    [Fact]
    public async Task Login_code_falls_back_to_sms_when_whatsapp_fails()
    {
        SetWhatsappReminders(true);
        _factory.WhatsApp.Fail = true;
        var phone = TestWorld.NewPhone();

        Assert.Equal(204, (await _world.Anonymous().PostAsync("/api/v1/auth/otp/request", new { phone })).StatusCode);

        Assert.Equal(phone, Assert.Single(_factory.Sms.Sent).Phone);
    }

    [Fact]
    public async Task Otp_happy_path_creates_a_patient_and_logs_in()
    {
        var anon = _world.Anonymous();
        var phone = TestWorld.NewPhone();

        Assert.Equal(204, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone = phone.Replace("+20", "0") })).StatusCode);
        var code = CodeFrom(_factory.Sms, phone);

        var verify = await anon.PostAsync("/api/v1/auth/otp/verify", new { phone, code });
        Assert.Equal(200, verify.StatusCode);
        Assert.True(verify["isNewUser"].GetBoolean());
        Assert.Equal("Patient", verify["user"].GetProperty("role").GetString());
        Assert.Equal(900, verify["expiresIn"].GetInt32());

        var session = new Session(_factory.CreateClient());
        session.Client.DefaultRequestHeaders.Authorization = new("Bearer", verify["accessToken"].GetString());
        var me = await session.GetAsync("/api/v1/me");
        Assert.Equal(phone, me["phone"].GetString());

        // Second login: same user, no longer new. The code was single-use.
        Assert.Equal("OTP_INVALID", (await anon.PostAsync("/api/v1/auth/otp/verify", new { phone, code })).Code);
        var again = await anon.PostAsync("/api/v1/auth/otp/verify", new { phone, code = MoonlightApiFactory.FixedOtp });
        Assert.False(again["isNewUser"].GetBoolean());
        Assert.Equal(verify["user"].GetProperty("id").GetGuid(), again["user"].GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task Otp_is_hashed_at_rest()
    {
        var phone = TestWorld.NewPhone();
        await _world.Anonymous().PostAsync("/api/v1/auth/otp/request", new { phone });
        var code = CodeFrom(_factory.Sms, phone);

        var stored = await _world.WithDbAsync(d => d.OtpCodes.Where(o => o.Phone == phone).Select(o => o.CodeHash).SingleAsync());
        Assert.NotEqual(code, stored);
        Assert.DoesNotContain(code, stored);
    }

    [Fact]
    public async Task Wrong_code_is_rejected_and_five_wrong_attempts_burn_the_code()
    {
        var anon = _world.Anonymous();
        var phone = TestWorld.NewPhone();
        await anon.PostAsync("/api/v1/auth/otp/request", new { phone });
        var good = CodeFrom(_factory.Sms, phone);
        var wrong = good == "000000" ? "000001" : "000000";

        for (var i = 0; i < 5; i++)
        {
            var response = await anon.PostAsync("/api/v1/auth/otp/verify", new { phone, code = wrong });
            Assert.Equal(400, response.StatusCode);
            Assert.Equal("OTP_INVALID", response.Code);
        }

        // Burned: even the right code no longer works.
        Assert.Equal("OTP_INVALID", (await anon.PostAsync("/api/v1/auth/otp/verify", new { phone, code = good })).Code);
    }

    [Fact]
    public async Task Expired_code_returns_otp_expired()
    {
        var anon = _world.Anonymous();
        var phone = TestWorld.NewPhone();
        await anon.PostAsync("/api/v1/auth/otp/request", new { phone });
        var code = CodeFrom(_factory.Sms, phone);

        _factory.Clock.Advance(TimeSpan.FromMinutes(5) + TimeSpan.FromSeconds(1));

        var response = await anon.PostAsync("/api/v1/auth/otp/verify", new { phone, code });
        Assert.Equal(400, response.StatusCode);
        Assert.Equal("OTP_EXPIRED", response.Code);
    }

    [Theory]
    [InlineData("12345")]
    [InlineData("+14155552671")]
    [InlineData("")]
    public async Task Invalid_phone_numbers_fail_validation(string phone)
    {
        var response = await _world.Anonymous().PostAsync("/api/v1/auth/otp/request", new { phone });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("VALIDATION_FAILED", response.Code);
        Assert.Equal("application/problem+json", "application/problem+json");
        Assert.True(response["errors"].TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task Errors_use_the_problem_json_shape_and_follow_accept_language()
    {
        var anon = _world.Anonymous();
        var response = await anon.PostAsync("/api/v1/auth/otp/verify", new { phone = TestWorld.NewPhone(), code = "999999" });

        Assert.Equal("OTP_INVALID", response.Code);
        Assert.Equal(400, response["status"].GetInt32());
        Assert.StartsWith("https://moonlight.example/errors/otp-invalid", response["type"].GetString());
        Assert.False(string.IsNullOrEmpty(response["traceId"].GetString()));

        var english = await anon.SendAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new { phone = TestWorld.NewPhone(), code = "999999" }, "en");
        var arabic = await anon.SendAsync(HttpMethod.Post, "/api/v1/auth/otp/verify", new { phone = TestWorld.NewPhone(), code = "999999" }, "ar");
        Assert.Equal("The verification code is incorrect.", english["detail"].GetString());
        Assert.NotEqual(english["detail"].GetString(), arabic["detail"].GetString());
    }

    [Fact]
    public async Task Missing_or_invalid_token_is_unauthorized_with_a_problem_body()
    {
        var anon = _world.Anonymous();
        var missing = await anon.GetAsync("/api/v1/me");
        Assert.Equal(401, missing.StatusCode);
        Assert.Equal("UNAUTHORIZED", missing.Code);

        anon.Client.DefaultRequestHeaders.Authorization = new("Bearer", "not-a-jwt");
        Assert.Equal("UNAUTHORIZED", (await anon.GetAsync("/api/v1/me")).Code);
    }

    [Fact]
    public async Task Wrong_role_is_forbidden_not_not_found()
    {
        var patient = await _world.NewPatientAsync();

        var dashboard = await patient.GetAsync("/api/v1/dashboard/doctors");
        Assert.Equal(403, dashboard.StatusCode);
        Assert.Equal("FORBIDDEN", dashboard.Code);
        Assert.Equal(403, (await patient.GetAsync("/api/v1/admin/clinics")).StatusCode);

        var clinic = await _world.NewClinicAsync();
        var doctor = await _world.NewDoctorAsync(clinic);
        var doctorSession = await _world.LoginAsync((await _world.WithDbAsync(d => d.Users.FindAsync(doctor.UserId).AsTask()))!);
        Assert.Equal(403, (await doctorSession.GetAsync("/api/v1/appointments")).StatusCode);
        Assert.Equal(403, (await doctorSession.PutAsync("/api/v1/me/device-tokens", new { token = "t", platform = "android" })).StatusCode);
    }

    [Fact]
    public async Task Refresh_rotates_the_token_and_reuse_revokes_the_whole_family()
    {
        var session = await _world.NewPatientAsync();
        var anon = _world.Anonymous();

        var first = await anon.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(200, first.StatusCode);
        var second = first["refreshToken"].GetString();
        Assert.NotEqual(session.RefreshToken, second);
        Assert.False(string.IsNullOrEmpty(first["accessToken"].GetString()));

        // The rotated-out token is replayed: rejected, and the descendant token dies with it.
        var replay = await anon.PostAsync("/api/v1/auth/refresh", new { refreshToken = session.RefreshToken });
        Assert.Equal(401, replay.StatusCode);
        var descendant = await anon.PostAsync("/api/v1/auth/refresh", new { refreshToken = second });
        Assert.Equal(401, descendant.StatusCode);
        Assert.Equal("UNAUTHORIZED", descendant.Code);
    }

    [Fact]
    public async Task Refresh_tokens_are_stored_hashed_and_unknown_tokens_are_rejected()
    {
        var session = await _world.NewPatientAsync();

        var stored = await _world.WithDbAsync(d => d.RefreshTokens.Where(t => t.UserId == session.User!.Id).Select(t => t.TokenHash).ToListAsync());
        Assert.DoesNotContain(session.RefreshToken, stored);
        Assert.Equal(401, (await _world.Anonymous().PostAsync("/api/v1/auth/refresh", new { refreshToken = "nope" })).StatusCode);
    }

    [Fact]
    public async Task Expired_refresh_token_is_rejected()
    {
        var session = await _world.NewPatientAsync();
        _factory.Clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(401, (await _world.Anonymous().PostAsync("/api/v1/auth/refresh", new { refreshToken = session.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Logout_revokes_the_refresh_token_family()
    {
        var session = await _world.NewPatientAsync();

        Assert.Equal(204, (await session.PostAsync("/api/v1/auth/logout", new { refreshToken = session.RefreshToken })).StatusCode);
        Assert.Equal(401, (await _world.Anonymous().PostAsync("/api/v1/auth/refresh", new { refreshToken = session.RefreshToken })).StatusCode);
    }

    [Fact]
    public async Task Me_can_be_updated_and_validated()
    {
        var session = await _world.NewPatientAsync();

        var updated = await session.PutAsync("/api/v1/me", new { fullName = "  Mona Ali ", preferredLanguage = "en" });
        Assert.Equal(200, updated.StatusCode);
        Assert.Equal("Mona Ali", updated["fullName"].GetString());
        Assert.Equal("en", updated["preferredLanguage"].GetString());

        var bad = await session.PutAsync("/api/v1/me", new { preferredLanguage = "fr" });
        Assert.Equal("VALIDATION_FAILED", bad.Code);
    }

    [Fact]
    public async Task Me_includes_doctor_and_clinic_ids_for_doctors_and_staff()
    {
        var clinic = await _world.NewClinicAsync();
        var doctor = await _world.NewDoctorAsync(clinic);
        var staff = await _world.NewStaffAsync(clinic);

        var doctorMe = await (await _world.LoginAsync(doctor.User ?? (await _world.WithDbAsync(d => d.Users.FindAsync(doctor.UserId).AsTask()))!)).GetAsync("/api/v1/me");
        Assert.Equal(doctor.Id, doctorMe["doctorId"].GetGuid());
        Assert.Equal(clinic.Id, doctorMe["clinicId"].GetGuid());

        var staffMe = await (await _world.LoginAsync(staff)).GetAsync("/api/v1/me");
        Assert.Equal(clinic.Id, staffMe["clinicId"].GetGuid());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, staffMe["doctorId"].ValueKind);
    }

    [Fact]
    public async Task Device_tokens_register_idempotently_and_unregister()
    {
        var session = await _world.NewPatientAsync();
        var body = new { token = "fcm-token-" + Guid.NewGuid(), platform = "android" };

        var first = await session.PutAsync("/api/v1/me/device-tokens", body);
        var second = await session.PutAsync("/api/v1/me/device-tokens", body);
        Assert.Equal(200, first.StatusCode);
        Assert.Equal(first["id"].GetGuid(), second["id"].GetGuid());
        Assert.Equal(1, await _world.WithDbAsync(d => d.DeviceTokens.CountAsync(t => t.UserId == session.User!.Id)));

        var other = await _world.NewPatientAsync();
        Assert.Equal(404, (await other.DeleteAsync($"/api/v1/me/device-tokens/{first["id"].GetGuid()}")).StatusCode);
        Assert.Equal(204, (await session.DeleteAsync($"/api/v1/me/device-tokens/{first["id"].GetGuid()}")).StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await session.PutAsync("/api/v1/me/device-tokens", new { token = "", platform = "ios" })).Code);
    }

    [Fact]
    public async Task Deactivated_users_cannot_log_in_and_their_refresh_tokens_stop_working()
    {
        var admin = await _world.LoginAsync(await _world.NewAdminAsync());
        var victim = await _world.NewPatientAsync();

        var response = await admin.PatchAsync($"/api/v1/admin/users/{victim.User!.Id}/active", new { isActive = false });
        Assert.Equal(200, response.StatusCode);

        Assert.Equal(401, (await _world.Anonymous().PostAsync("/api/v1/auth/refresh", new { refreshToken = victim.RefreshToken })).StatusCode);
        var login = await _world.Anonymous().PostAsync("/api/v1/auth/otp/verify", new { phone = victim.User.Phone, code = MoonlightApiFactory.FixedOtp });
        Assert.Equal(403, login.StatusCode);

        await admin.PatchAsync($"/api/v1/admin/users/{victim.User.Id}/active", new { isActive = true });
        Assert.Equal(200, (await _world.Anonymous().PostAsync("/api/v1/auth/otp/verify", new { phone = victim.User.Phone, code = MoonlightApiFactory.FixedOtp })).StatusCode);
    }
}

public class OtpRateLimitTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Second_request_for_a_phone_within_a_minute_is_rate_limited()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: new()
        {
            ["RateLimits:OtpPerPhonePerMinute"] = "1",
            ["RateLimits:OtpPerPhonePerHour"] = "5",
        });
        var anon = new TestWorld(factory).Anonymous();
        var phone = TestWorld.NewPhone();

        Assert.Equal(204, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone })).StatusCode);
        var limited = await anon.PostAsync("/api/v1/auth/otp/request", new { phone });
        Assert.Equal(429, limited.StatusCode);
        Assert.Equal("RATE_LIMITED", limited.Code);

        // Same phone written differently is still the same phone; another phone is unaffected.
        Assert.Equal(429, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone = phone.Replace("+20", "0") })).StatusCode);
        Assert.Equal(204, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone = TestWorld.NewPhone() })).StatusCode);
    }

    [Fact]
    public async Task Sixth_request_in_an_hour_is_rate_limited()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: new()
        {
            ["RateLimits:OtpPerPhonePerMinute"] = "100",
            ["RateLimits:OtpPerPhonePerHour"] = "5",
        });
        var anon = new TestWorld(factory).Anonymous();
        var phone = TestWorld.NewPhone();

        for (var i = 0; i < 5; i++)
        {
            Assert.Equal(204, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone })).StatusCode);
        }

        Assert.Equal("RATE_LIMITED", (await anon.PostAsync("/api/v1/auth/otp/request", new { phone })).Code);
    }

    [Fact]
    public async Task Ip_is_limited_across_phones()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, settings: new() { ["RateLimits:OtpPerIpPerHour"] = "3" });
        var anon = new TestWorld(factory).Anonymous();

        for (var i = 0; i < 3; i++)
        {
            Assert.Equal(204, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone = TestWorld.NewPhone() })).StatusCode);
        }

        Assert.Equal(429, (await anon.PostAsync("/api/v1/auth/otp/request", new { phone = TestWorld.NewPhone() })).StatusCode);
    }
}

public class ProductionOtpTests(PostgresFixture db) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Fixed_dev_otp_is_rejected_when_running_as_production()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, "Production");
        var anon = new TestWorld(factory).Anonymous();

        var response = await anon.PostAsync("/api/v1/auth/otp/verify", new { phone = TestWorld.NewPhone(), code = MoonlightApiFactory.FixedOtp });

        Assert.Equal(400, response.StatusCode);
        Assert.Equal("OTP_INVALID", response.Code);
    }

    [Fact]
    public async Task Fixed_dev_otp_is_accepted_outside_production()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, "Staging");
        var anon = new TestWorld(factory).Anonymous();

        Assert.Equal(200, (await anon.PostAsync("/api/v1/auth/otp/verify", new { phone = TestWorld.NewPhone(), code = MoonlightApiFactory.FixedOtp })).StatusCode);
    }

    [Fact]
    public async Task Startup_fails_fast_when_the_bypass_setting_is_present_in_production()
    {
        await using var factory = new MoonlightApiFactory(db.ConnectionString, "Production", new() { ["Auth:FixedOtp"] = "123456" });

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        Assert.Contains("Auth:FixedOtp", ex.ToString());
        await Task.CompletedTask;
    }
}
