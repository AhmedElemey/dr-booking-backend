using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public class RecordsTests(PostgresFixture db) : Phase2TestBase(db)
{
    private async Task<(Session Patient, Guid Id)> PatientWithVisitAsync()
    {
        var patient = await World.NewPatientAsync();
        await patient.PutAsync("/api/v1/me", new { fullName = "Hala Samir" });
        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));
        return (patient, patient.User!.Id);
    }

    [Fact]
    public async Task Patient_list_detail_medical_info_and_entries()
    {
        Enable(FeatureKeys.PatientRecords);
        var (patient, userId) = await PatientWithVisitAsync();

        var list = await Staff.GetAsync("/api/v1/dashboard/patients?search=Hala");
        var row = Assert.Single(list.Body.EnumerateArray());
        Assert.Equal(userId, row.GetProperty("id").GetGuid());
        Assert.Equal(1, row.GetProperty("appointmentsCount").GetInt32());

        var info = await Staff.PutAsync($"/api/v1/dashboard/patients/{userId}/medical-info", new { bloodType = "O+", allergies = "Penicillin" });
        Assert.Equal(200, info.StatusCode);
        Assert.Equal(400, (await Staff.PutAsync($"/api/v1/dashboard/patients/{userId}/medical-info", new { bloodType = "Z" })).StatusCode);

        var hidden = await Staff.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { kind = "Note", title = "Internal", body = "private" });
        var shown = await Staff.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { kind = "Prescription", title = "Amoxicillin", visibleToPatient = true });
        Assert.Equal(201, hidden.StatusCode);
        Assert.Equal(201, shown.StatusCode);
        Assert.False(hidden["visibleToPatient"].GetBoolean());

        var detail = await Staff.GetAsync($"/api/v1/dashboard/patients/{userId}");
        Assert.Equal(2, detail["records"].GetArrayLength());
        Assert.Equal("O+", detail["medicalInfo"].GetProperty("bloodType").GetString());
        Assert.Equal(1, detail["appointments"].GetArrayLength());

        var mine = await patient.GetAsync("/api/v1/me/records");
        Assert.Equal("Amoxicillin", Assert.Single(mine["entries"].EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("Penicillin", mine["medicalInfo"].GetProperty("allergies").GetString());

        var updated = await Staff.PutAsync($"/api/v1/dashboard/records/{hidden.Id}", new { kind = "Note", title = "Internal 2", visibleToPatient = true });
        Assert.Equal("Internal 2", updated["title"].GetString());
        Assert.Equal(2, (await patient.GetAsync("/api/v1/me/records"))["entries"].GetArrayLength());
        Assert.Equal(204, (await Staff.DeleteAsync($"/api/v1/dashboard/records/{hidden.Id}")).StatusCode);
        Assert.Equal(1, (await patient.GetAsync("/api/v1/me/records"))["entries"].GetArrayLength());
    }

    [Fact]
    public async Task Staff_only_see_patients_of_their_clinic_and_only_author_or_staff_edit()
    {
        Enable(FeatureKeys.PatientRecords);
        var (patient, userId) = await PatientWithVisitAsync();
        var stranger = await World.LoginAsync(await World.NewStaffAsync(await World.NewClinicAsync("Other")));

        Assert.Equal(404, (await stranger.GetAsync($"/api/v1/dashboard/patients/{userId}")).StatusCode);
        Assert.Empty((await stranger.GetAsync("/api/v1/dashboard/patients")).Body.EnumerateArray());
        Assert.True((await stranger.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { kind = "Note", title = "x" })).StatusCode is 403 or 404);

        var byDoctor = await DoctorSession.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { kind = "Diagnosis", title = "Caries" });
        Assert.Equal(201, byDoctor.StatusCode);
        var colleague = await World.NewDoctorAsync(Clinic, "Dr Colleague");
        var colleagueSession = await World.LoginAsync((await World.WithDbAsync(d => d.Users.FindAsync(colleague.UserId).AsTask()))!);
        Assert.True((await colleagueSession.PutAsync($"/api/v1/dashboard/records/{byDoctor.Id}", new { kind = "Note", title = "hijack" })).StatusCode is 403 or 404);
        Assert.Equal(200, (await Staff.PutAsync($"/api/v1/dashboard/records/{byDoctor.Id}", new { kind = "Diagnosis", title = "Caries (staff edit)" })).StatusCode);
        Assert.Equal(200, (await DoctorSession.PutAsync($"/api/v1/dashboard/records/{byDoctor.Id}", new { kind = "Diagnosis", title = "Caries 2" })).StatusCode);
        Assert.Equal(403, (await patient.GetAsync("/api/v1/dashboard/patients")).StatusCode);
    }

    [Fact]
    public async Task Family_member_records_are_scoped_and_shown_to_the_owner()
    {
        Enable(FeatureKeys.PatientRecords, FeatureKeys.FamilyAccounts);
        var (patient, userId) = await PatientWithVisitAsync();
        var omar = await NewMemberAsync(patient);

        await Staff.PutAsync($"/api/v1/dashboard/patients/{userId}/medical-info", new { familyMemberId = omar.Id, bloodType = "A-" });
        await Staff.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { familyMemberId = omar.Id, kind = "Note", title = "Omar note", visibleToPatient = true });
        await Staff.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { kind = "Note", title = "Own note", visibleToPatient = true });

        var scoped = await Staff.GetAsync($"/api/v1/dashboard/patients/{userId}?familyMemberId={omar.Id}");
        Assert.Equal("Omar note", Assert.Single(scoped["records"].EnumerateArray()).GetProperty("title").GetString());
        Assert.Equal("A-", scoped["medicalInfo"].GetProperty("bloodType").GetString());

        var mine = await patient.GetAsync($"/api/v1/me/records?familyMemberId={omar.Id}");
        Assert.Equal("Omar note", Assert.Single(mine["entries"].EnumerateArray()).GetProperty("title").GetString());
    }

    [Fact]
    public async Task Attachment_upload_accepts_pdf_and_rejects_other_types()
    {
        Enable(FeatureKeys.PatientRecords);
        var (patient, userId) = await PatientWithVisitAsync();
        var entry = await Staff.PostAsync($"/api/v1/dashboard/patients/{userId}/records", new { kind = "Attachment", title = "X-ray", visibleToPatient = true });

        async Task<HttpResponseMessage> UploadAsync(string name, string type, byte[] bytes)
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            form.Add(file, "file", name);
            return await Staff.Client.PutAsync($"/api/v1/dashboard/records/{entry.Id}/attachment", form);
        }

        var ok = await UploadAsync("scan.pdf", "application/pdf", "%PDF-1.4\n%test\n"u8.ToArray());
        Assert.Equal(System.Net.HttpStatusCode.OK, ok.StatusCode);
        var bad = await UploadAsync("run.exe", "application/octet-stream", [1, 2, 3]);
        Assert.True((int)bad.StatusCode is 400 or 415 or 422);
        Assert.NotNull(Assert.Single((await patient.GetAsync("/api/v1/me/records"))["entries"].EnumerateArray()).GetProperty("attachmentUrl").GetString());
    }
}

public class MessagingTests(PostgresFixture db) : Phase2TestBase(db)
{
    private Task<int> CountAsync(Guid userId, MessageChannel channel) =>
        World.WithDbAsync(d => d.OutboundMessages.CountAsync(m => m.UserId == userId && m.Channel == channel));

    [Fact]
    public async Task User_profile_exposes_and_updates_the_opt_ins()
    {
        var patient = await World.NewPatientAsync();

        var me = await patient.GetAsync("/api/v1/me");
        Assert.True(me["whatsAppOptIn"].GetBoolean());
        Assert.True(me["marketingOptIn"].GetBoolean());

        var updated = await patient.PutAsync("/api/v1/me", new { whatsAppOptIn = false, marketingOptIn = false });
        Assert.False(updated["whatsAppOptIn"].GetBoolean());
        Assert.False((await patient.GetAsync("/api/v1/me"))["marketingOptIn"].GetBoolean());
    }

    [Fact]
    public async Task Whatsapp_goes_to_opted_in_users_only_while_the_feature_is_on_and_everything_is_logged()
    {
        var optedIn = await World.NewPatientAsync();
        var optedOut = await World.NewPatientAsync();
        await optedOut.PutAsync("/api/v1/me", new { whatsAppOptIn = false });
        await RegisterDeviceAsync(optedIn);

        await World.BookAsync(optedIn, await SlotAsync(TimeSpan.FromDays(2)));
        Assert.Equal(0, await CountAsync(optedIn.User!.Id, MessageChannel.WhatsApp)); // feature off
        Assert.Equal(1, await CountAsync(optedIn.User.Id, MessageChannel.Push));

        Enable(FeatureKeys.WhatsappReminders);
        await World.BookAsync(optedIn, await SlotAsync(TimeSpan.FromDays(3)));
        await World.BookAsync(optedOut, await SlotAsync(TimeSpan.FromDays(4)));

        Assert.Equal(1, await CountAsync(optedIn.User.Id, MessageChannel.WhatsApp));
        Assert.Equal(0, await CountAsync(optedOut.User!.Id, MessageChannel.WhatsApp));
        var row = await World.WithDbAsync(d => d.OutboundMessages.AsNoTracking().FirstAsync(m => m.UserId == optedIn.User.Id && m.Channel == MessageChannel.WhatsApp));
        Assert.Equal("appointment.confirmed", row.Kind);
        Assert.Equal(MessageStatus.Sent, row.Status);
        Assert.Equal(optedIn.User.Phone, row.To);
    }

    [Fact]
    public async Task Dashboard_outbox_is_filterable_and_scoped_to_the_clinic()
    {
        Enable(FeatureKeys.WhatsappReminders);
        var patient = await World.NewPatientAsync();
        await RegisterDeviceAsync(patient);
        await World.BookAsync(patient, await SlotAsync(TimeSpan.FromDays(2)));

        var all = await Staff.GetAsync("/api/v1/dashboard/messages");
        Assert.Equal(200, all.StatusCode);
        Assert.True(all["items"].GetArrayLength() >= 2);
        var whatsApp = await Staff.GetAsync("/api/v1/dashboard/messages?channel=WhatsApp");
        Assert.All(whatsApp["items"].EnumerateArray(), m => Assert.Equal("WhatsApp", m.GetProperty("channel").GetString()));

        var other = await World.LoginAsync(await World.NewStaffAsync(await World.NewClinicAsync("Other")));
        Assert.Empty((await other.GetAsync("/api/v1/dashboard/messages"))["items"].EnumerateArray());
        Assert.Equal(403, (await patient.GetAsync("/api/v1/dashboard/messages")).StatusCode);
    }
}
