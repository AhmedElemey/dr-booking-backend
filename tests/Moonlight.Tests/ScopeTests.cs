using Moonlight.Api.Domain;

namespace Moonlight.Tests;

// Role and scope enforcement: out-of-scope resources are 404, wrong roles 403 (spec section 2).
public class ScopeTests(PostgresFixture db) : IClassFixture<PostgresFixture>, IAsyncLifetime
{
    private readonly MoonlightApiFactory _factory = new(db.ConnectionString);
    private TestWorld _world = null!;
    private Clinic _clinicA = null!;
    private Clinic _clinicB = null!;
    private Doctor _doctorA1 = null!;
    private Doctor _doctorA2 = null!;
    private Doctor _doctorB = null!;
    private Session _staffA = null!;
    private Session _doctorA1Session = null!;
    private Slot _slotA2 = null!;
    private Slot _slotB = null!;

    public async Task InitializeAsync()
    {
        _world = new TestWorld(_factory);
        _clinicA = await _world.NewClinicAsync("A");
        _clinicB = await _world.NewClinicAsync("B");
        _doctorA1 = await _world.NewDoctorAsync(_clinicA, "Doc A1");
        _doctorA2 = await _world.NewDoctorAsync(_clinicA, "Doc A2");
        _doctorB = await _world.NewDoctorAsync(_clinicB, "Doc B");
        _staffA = await _world.LoginAsync(await _world.NewStaffAsync(_clinicA));
        _doctorA1Session = await _world.LoginAsync((await _world.WithDbAsync(d => d.Users.FindAsync(_doctorA1.UserId).AsTask()))!);
        _slotA2 = await _world.NewSlotAsync(_doctorA2, TimeSpan.FromHours(5));
        _slotB = await _world.NewSlotAsync(_doctorB, TimeSpan.FromHours(5));
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task Doctor_sees_only_themselves_and_staff_see_their_clinic()
    {
        var doctorList = await _doctorA1Session.GetAsync("/api/v1/dashboard/doctors");
        Assert.Equal([_doctorA1.Id], doctorList.Body.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()));

        var staffList = await _staffA.GetAsync("/api/v1/dashboard/doctors");
        Assert.Equal(
            new[] { _doctorA1.Id, _doctorA2.Id }.Order(),
            staffList.Body.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).Order());
    }

    [Fact]
    public async Task Doctor_cannot_touch_another_doctor_in_the_same_clinic()
    {
        var other = $"/api/v1/dashboard/doctors/{_doctorA2.Id}";

        Assert.Equal(404, (await _doctorA1Session.GetAsync(other)).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.PutAsync(other, new { nameAr = "x", nameEn = "x", specialtyId = ReferenceData.GeneralDentistryId })).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.GetAsync(other + "/services")).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.PostAsync(other + "/services", new { nameAr = "x", nameEn = "x", durationMinutes = 30 })).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.GetAsync(other + "/slots")).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.PostAsync(other + "/slots", new { startsAt = _factory.Clock.SlotStart(TimeSpan.FromHours(9)), endsAt = _factory.Clock.SlotStart(TimeSpan.FromHours(10)) })).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.PostAsync($"{other}/slots/{_slotA2.Id}/block")).StatusCode);
        Assert.Equal(404, (await _doctorA1Session.DeleteAsync($"{other}/slots/{_slotA2.Id}")).StatusCode);
        Assert.Equal("NOT_FOUND", (await _doctorA1Session.GetAsync(other)).Code);
    }

    [Fact]
    public async Task Staff_cannot_touch_another_clinic()
    {
        var other = $"/api/v1/dashboard/doctors/{_doctorB.Id}";

        Assert.Equal(404, (await _staffA.GetAsync(other)).StatusCode);
        Assert.Equal(404, (await _staffA.GetAsync(other + "/slots")).StatusCode);
        Assert.Equal(404, (await _staffA.DeleteAsync($"{other}/slots?from=2026-11-01&to=2026-11-02")).StatusCode);
        Assert.Equal(404, (await _staffA.PostAsync($"{other}/slots/{_slotB.Id}/block")).StatusCode);
        Assert.Equal(404, (await _staffA.GetAsync($"/api/v1/dashboard/appointments?doctorId={_doctorB.Id}")).StatusCode);
        Assert.Equal(404, (await _staffA.PostAsync("/api/v1/dashboard/appointments", new { slotId = _slotB.Id, patientPhone = TestWorld.NewPhone(), patientName = "P" })).StatusCode);
    }

    [Fact]
    public async Task Doctor_cannot_update_clinic_info_but_staff_can_and_cross_clinic_data_is_untouched()
    {
        var update = new { nameAr = "جديد", nameEn = "New Name", addressAr = "ع", addressEn = "Addr", city = "giza", phone = "+20221111111", latitude = 30.0, longitude = 31.0 };

        Assert.Equal(403, (await _doctorA1Session.PutAsync("/api/v1/dashboard/clinic", update)).StatusCode);
        var ok = await _staffA.PutAsync("/api/v1/dashboard/clinic", update);
        Assert.Equal(200, ok.StatusCode);
        Assert.Equal("Giza", ok["city"].GetString());
        Assert.Equal("New Name", (await _doctorA1Session.GetAsync("/api/v1/dashboard/clinic"))["nameEn"].GetString());

        var bad = await _staffA.PutAsync("/api/v1/dashboard/clinic", update with { city = "Atlantis" });
        Assert.Equal("VALIDATION_FAILED", bad.Code);
        Assert.Equal("B", await _world.WithDbAsync(async d => (await d.Clinics.FindAsync(_clinicB.Id))!.NameEn));
    }

    [Fact]
    public async Task Admin_manages_clinics_and_accounts_and_others_cannot()
    {
        var admin = await _world.LoginAsync(await _world.NewAdminAsync());
        var clinic = await admin.PostAsync("/api/v1/admin/clinics", new
        {
            nameAr = "عيادة",
            nameEn = "New Clinic",
            addressAr = "ع",
            addressEn = "A",
            city = "Cairo",
            phone = "+20221111111",
        });
        Assert.Equal(201, clinic.StatusCode);
        Assert.Equal("Africa/Cairo", clinic["timezone"].GetString());
        var clinicId = clinic.Id;

        var list = await admin.GetAsync("/api/v1/admin/clinics?pageSize=100");
        Assert.True(list["totalCount"].GetInt32() >= 3);

        var doctorPhone = TestWorld.NewPhone();
        var doctor = await admin.PostAsync($"/api/v1/admin/clinics/{clinicId}/users", new
        {
            phone = doctorPhone,
            fullName = "Dr New",
            role = "Doctor",
            nameAr = "د. جديد",
            nameEn = "Dr New",
            specialtyId = ReferenceData.CosmeticId,
        });
        Assert.Equal(201, doctor.StatusCode);
        Assert.Equal(clinicId, doctor["clinicId"].GetGuid());
        var published = await _world.WithDbAsync(d => d.Doctors.FindAsync(doctor["doctorId"].GetGuid()).AsTask());
        Assert.False(published!.IsPublished);
        Assert.Equal("dr-new", published.Slug);

        var staff = await admin.PostAsync($"/api/v1/admin/clinics/{clinicId}/users", new { phone = TestWorld.NewPhone(), fullName = "Receptionist", role = "ClinicStaff" });
        Assert.Equal(201, staff.StatusCode);

        // The new doctor logs in with the same OTP flow and gets doctor claims.
        var me = await (await _world.LoginAsync(doctorPhone)).GetAsync("/api/v1/me");
        Assert.Equal("Doctor", me["role"].GetString());
        Assert.Equal(doctor["doctorId"].GetGuid(), me["doctorId"].GetGuid());

        // Hiding the clinic hides it everywhere.
        var hidden = await admin.PatchAsync($"/api/v1/admin/clinics/{_clinicA.Id}/active", new { isActive = false });
        Assert.False(hidden["isActive"].GetBoolean());

        Assert.Equal(403, (await _staffA.GetAsync("/api/v1/admin/clinics")).StatusCode);
        Assert.Equal(403, (await _staffA.PostAsync("/api/v1/admin/clinics", new { })).StatusCode);
        Assert.Equal(404, (await admin.PatchAsync($"/api/v1/admin/clinics/{Guid.NewGuid()}/active", new { isActive = true })).StatusCode);
    }

    [Fact]
    public async Task Creating_an_account_for_a_phone_already_in_use_is_phone_in_use()
    {
        var admin = await _world.LoginAsync(await _world.NewAdminAsync());
        var patient = await _world.NewPatientAsync();

        var response = await admin.PostAsync($"/api/v1/admin/clinics/{_clinicA.Id}/users", new { phone = patient.User!.Phone, fullName = "X", role = "ClinicStaff" });

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("PHONE_IN_USE", response.Code);
    }

    [Fact]
    public async Task Admin_users_endpoint_validates_input()
    {
        var admin = await _world.LoginAsync(await _world.NewAdminAsync());

        var response = await admin.PostAsync($"/api/v1/admin/clinics/{_clinicA.Id}/users", new { phone = "bad", role = "Patient" });

        Assert.Equal("VALIDATION_FAILED", response.Code);
        Assert.True(response["errors"].TryGetProperty("role", out _));
        Assert.True(response["errors"].TryGetProperty("phone", out _));
    }

    [Fact]
    public async Task Doctor_profile_and_services_can_be_managed_and_services_are_soft_deleted()
    {
        var path = $"/api/v1/dashboard/doctors/{_doctorA1.Id}";

        var updated = await _doctorA1Session.PutAsync(path, new
        {
            nameAr = "د. أ",
            nameEn = "Doc A1 Updated",
            bioEn = "Bio",
            yearsExperience = 10,
            consultationFeeEgp = 250.5,
            specialtyId = ReferenceData.PediatricId,
            isPublished = true,
        });
        Assert.Equal(200, updated.StatusCode);
        Assert.True(updated["isPublished"].GetBoolean());
        Assert.Equal(ReferenceData.PediatricId, updated["specialtyId"].GetGuid());
        Assert.Equal("VALIDATION_FAILED", (await _doctorA1Session.PutAsync(path, new { nameAr = "x", nameEn = "x", specialtyId = Guid.NewGuid() })).Code);

        var created = await _doctorA1Session.PostAsync(path + "/services", new { nameAr = "حشو", nameEn = "Filling", durationMinutes = 60, priceEgp = 700 });
        Assert.Equal(201, created.StatusCode);
        Assert.Equal("VALIDATION_FAILED", (await _doctorA1Session.PostAsync(path + "/services", new { nameAr = "x", nameEn = "x", durationMinutes = 10 })).Code);

        var edited = await _doctorA1Session.PutAsync($"{path}/services/{created.Id}", new { nameAr = "حشو", nameEn = "Filling+", durationMinutes = 45, priceEgp = 650 });
        Assert.Equal("Filling+", edited["nameEn"].GetString());

        Assert.Equal(204, (await _doctorA1Session.DeleteAsync($"{path}/services/{created.Id}")).StatusCode);
        var all = await _doctorA1Session.GetAsync(path + "/services");
        Assert.False(Assert.Single(all.Body.EnumerateArray()).GetProperty("isActive").GetBoolean());
        Assert.Empty((await _world.Anonymous().GetAsync($"/api/v1/doctors/{_doctorA1.Id}"))["services"].EnumerateArray());
    }

    [Fact]
    public async Task Photo_upload_accepts_images_and_rejects_other_files()
    {
        var path = $"/api/v1/dashboard/doctors/{_doctorA1.Id}/photo";
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0];

        using var good = new MultipartFormDataContent { { new ByteArrayContent(png), "file", "me.png" } };
        var ok = await _doctorA1Session.Client.PutAsync(path, good);
        Assert.Equal(200, (int)ok.StatusCode);
        var url = System.Text.Json.JsonDocument.Parse(await ok.Content.ReadAsStringAsync()).RootElement.GetProperty("photoUrl").GetString()!;
        Assert.EndsWith(".png", url);
        Assert.Equal(200, (int)(await _doctorA1Session.Client.GetAsync(new Uri(url).AbsolutePath)).StatusCode);

        using var bad = new MultipartFormDataContent { { new ByteArrayContent("not an image"u8.ToArray()), "file", "me.png" } };
        Assert.Equal(400, (int)(await _doctorA1Session.Client.PutAsync(path, bad)).StatusCode);
    }
}
