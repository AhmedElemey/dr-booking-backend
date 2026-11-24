using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;

namespace Moonlight.Tests;

// The clinic-admin flag: the platform super admin grants it on staff accounts, and login reports it
// so sensitive corrections (status correction with refunds) can be limited to clinic admins later.
public class AdminClinicAdminTests(PostgresFixture db) : Phase2TestBase(db)
{
    private async Task<Session> AdminAsync() => await World.LoginAsync(await World.NewAdminAsync());

    [Fact]
    public async Task The_super_admin_can_create_staff_as_clinic_admin_and_the_flag_defaults_off()
    {
        var admin = await AdminAsync();

        var flagged = await admin.PostAsync($"/api/v1/admin/clinics/{Clinic.Id}/users", new { phone = "+201000000301", fullName = "Manager", role = "ClinicStaff", isAdmin = true });
        Assert.Equal(201, flagged.StatusCode);
        Assert.True(flagged["isAdmin"].GetBoolean());
        Assert.True(await World.WithDbAsync(d => d.ClinicStaff.Where(s => s.User!.Phone == "+201000000301").Select(s => s.IsAdmin).FirstAsync()));

        var plain = await admin.PostAsync($"/api/v1/admin/clinics/{Clinic.Id}/users", new { phone = "+201000000302", fullName = "Reception", role = "ClinicStaff" });
        Assert.Equal(201, plain.StatusCode);
        Assert.False(plain["isAdmin"].GetBoolean());
    }

    [Fact]
    public async Task The_admin_flag_is_rejected_for_doctors_on_create_and_toggle()
    {
        var admin = await AdminAsync();

        var create = await admin.PostAsync($"/api/v1/admin/clinics/{Clinic.Id}/users", new { phone = "+201000000303", fullName = "Doc", role = "Doctor", nameAr = "د", nameEn = "D", specialtyId = ReferenceData.GeneralDentistryId, isAdmin = true });
        Assert.Equal(400, create.StatusCode);

        var toggle = await admin.PatchAsync($"/api/v1/admin/users/{Doctor.UserId}/admin", new { isAdmin = true });
        Assert.Equal(400, toggle.StatusCode);
    }

    [Fact]
    public async Task The_admin_flag_toggles_and_login_reports_it()
    {
        var admin = await AdminAsync();
        var staffUser = await World.NewStaffAsync(Clinic);

        var on = await admin.PatchAsync($"/api/v1/admin/users/{staffUser.Id}/admin", new { isAdmin = true });
        Assert.Equal(200, on.StatusCode);
        Assert.True(on["isAdmin"].GetBoolean());
        Assert.True(await World.WithDbAsync(d => d.ClinicStaff.Where(s => s.UserId == staffUser.Id).Select(s => s.IsAdmin).FirstAsync()));

        var login = await World.Anonymous().PostAsync("/api/v1/auth/otp/verify", new { phone = staffUser.Phone, code = MoonlightApiFactory.FixedOtp });
        Assert.Equal(200, login.StatusCode);
        Assert.True(login["user"].GetProperty("isClinicAdmin").GetBoolean());

        var off = await admin.PatchAsync($"/api/v1/admin/users/{staffUser.Id}/admin", new { isAdmin = false });
        Assert.False(off["isAdmin"].GetBoolean());
        var relogin = await World.Anonymous().PostAsync("/api/v1/auth/otp/verify", new { phone = staffUser.Phone, code = MoonlightApiFactory.FixedOtp });
        Assert.False(relogin["user"].GetProperty("isClinicAdmin").GetBoolean());
    }

    [Fact]
    public async Task The_admin_toggle_is_platform_admin_only_and_404s_for_unknown_users()
    {
        var admin = await AdminAsync();
        Assert.Equal(404, (await admin.PatchAsync($"/api/v1/admin/users/{Guid.NewGuid()}/admin", new { isAdmin = true })).StatusCode);
        Assert.Equal(403, (await Staff.PatchAsync($"/api/v1/admin/users/{Doctor.UserId}/admin", new { isAdmin = true })).StatusCode);
        var patient = await World.NewPatientAsync();
        Assert.Equal(403, (await patient.PatchAsync($"/api/v1/admin/users/{Doctor.UserId}/admin", new { isAdmin = true })).StatusCode);
    }
}
