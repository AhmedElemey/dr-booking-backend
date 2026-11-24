using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public class NoShowCorrectionTests(PostgresFixture db) : Phase2TestBase(db)
{
    private async Task<(Guid Id, Session Patient, Slot Slot)> NoShowVisitAsync(Session? patient = null)
    {
        patient ??= await World.NewPatientAsync();
        var slot = await SlotAsync(TimeSpan.FromHours(2));
        var id = await World.BookAsync(patient, slot);
        Factory.Clock.Advance(TimeSpan.FromHours(3));
        var done = await Staff.PostAsync($"{Dash(id)}/no-show");
        Assert.Equal(200, done.StatusCode);
        return (id, patient, slot);
    }

    private Task<List<AppointmentStatusCorrection>> CorrectionsAsync(Guid id) =>
        World.WithDbAsync(d => d.AppointmentStatusCorrections.AsNoTracking().Where(c => c.AppointmentId == id).OrderBy(c => c.CreatedAt).ToListAsync());

    [Fact]
    public async Task A_clinic_admin_corrects_a_completed_visit_to_no_show_with_an_audit_row()
    {
        var (id, _, _) = await CompletedVisitAsync();

        var response = await ClinicAdmin.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow", reason = "marked by mistake" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("NoShow", response["status"].GetString());
        var appointment = await AppointmentAsync(id);
        Assert.Equal(AppointmentStatus.NoShow, appointment.Status);
        Assert.Null(appointment.CompletedAt);
        var correction = Assert.Single(await CorrectionsAsync(id));
        Assert.Equal(AppointmentStatus.Completed, correction.FromStatus);
        Assert.Equal(AppointmentStatus.NoShow, correction.ToStatus);
        Assert.Equal(ClinicAdmin.User!.Id, correction.ChangedByUserId);
        Assert.Equal("marked by mistake", correction.Reason);
    }

    [Fact]
    public async Task A_corrected_no_show_is_completed_and_the_paid_payment_is_not_refunded()
    {
        var (id, _, _) = await NoShowVisitAsync();
        var paymentId = await World.WithDbAsync(async d =>
        {
            var payment = new Payment
            {
                AppointmentId = id,
                AmountEgp = 150m,
                Status = PaymentStatus.Paid,
                Method = PaymentMethod.Online,
                Provider = "mock",
                ProviderTransactionId = "txn-" + Guid.NewGuid().ToString("N"),
                CreatedAt = Now,
                PaidAt = Now,
            };
            d.Payments.Add(payment);
            await d.SaveChangesAsync();
            return payment.Id;
        });

        var response = await ClinicAdmin.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "Completed" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("Completed", response["status"].GetString());
        var payment = await World.WithDbAsync(d => d.Payments.AsNoTracking().FirstAsync(p => p.Id == paymentId));
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Null(payment.RefundedAt);
    }

    [Fact]
    public async Task Correction_is_only_allowed_within_24_hours_of_closing_the_visit()
    {
        var (id, _, _) = await CompletedVisitAsync();
        Factory.Clock.Advance(TimeSpan.FromHours(25));

        var response = await ClinicAdmin.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });

        Assert.Equal(409, response.StatusCode);
        Assert.Equal("INVALID_TRANSITION", response.Code);
        Assert.Equal(AppointmentStatus.Completed, (await AppointmentAsync(id)).Status);
        Assert.Empty(await CorrectionsAsync(id));
    }

    [Fact]
    public async Task The_24_hour_window_runs_from_closing_the_visit_not_from_later_updates()
    {
        var (id, _, _) = await CompletedVisitAsync();
        Factory.Clock.Advance(TimeSpan.FromHours(20));
        await World.WithDbAsync(async d =>
        {
            var row = await d.Appointments.FirstAsync(a => a.Id == id);
            row.UpdatedAt = Now;
            await d.SaveChangesAsync();
            return true;
        });
        Factory.Clock.Advance(TimeSpan.FromHours(5));

        var response = await ClinicAdmin.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });

        Assert.Equal(409, response.StatusCode);
        Assert.Equal(AppointmentStatus.Completed, (await AppointmentAsync(id)).Status);
    }

    [Fact]
    public async Task Reception_staff_who_are_not_clinic_admins_cannot_correct()
    {
        var (id, _, _) = await CompletedVisitAsync();

        var response = await Staff.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });

        Assert.Equal(403, response.StatusCode);
        Assert.Equal(AppointmentStatus.Completed, (await AppointmentAsync(id)).Status);
        Assert.Empty(await CorrectionsAsync(id));
    }

    [Fact]
    public async Task A_confirmed_visit_cannot_be_corrected_and_the_target_must_be_terminal()
    {
        var slot = await SlotAsync(TimeSpan.FromHours(2));
        var patient = await World.NewPatientAsync();
        var id = await World.BookAsync(patient, slot);

        var notTerminal = await ClinicAdmin.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });
        Assert.Equal(409, notTerminal.StatusCode);
        Assert.Equal("INVALID_TRANSITION", notTerminal.Code);

        var badTarget = await ClinicAdmin.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "Cancelled" });
        Assert.Equal(400, badTarget.StatusCode);
        Assert.Equal("VALIDATION_FAILED", badTarget.Code);

        Assert.Equal(AppointmentStatus.Confirmed, (await AppointmentAsync(id)).Status);
        Assert.Empty(await CorrectionsAsync(id));
    }

    [Fact]
    public async Task The_patient_no_show_count_drops_after_a_correction()
    {
        Enable(FeatureKeys.PatientRecords);
        var patient = await World.NewPatientAsync();
        await NoShowVisitAsync(patient);
        var (second, _, _) = await NoShowVisitAsync(patient);

        var before = await Staff.GetAsync($"/api/v1/dashboard/patients/{patient.User!.Id}");
        Assert.Equal(200, before.StatusCode);
        Assert.Equal(2, before["noShowCount"].GetInt32());

        var corrected = await ClinicAdmin.PostAsync($"{Dash(second)}/correct-status", new { toStatus = "Completed" });
        Assert.Equal(200, corrected.StatusCode);

        var after = await Staff.GetAsync($"/api/v1/dashboard/patients/{patient.User.Id}");
        Assert.Equal(1, after["noShowCount"].GetInt32());

        var list = await Staff.GetAsync("/api/v1/dashboard/patients");
        var row = Assert.Single(list.Body.EnumerateArray(), r => r.GetProperty("id").GetGuid() == patient.User.Id);
        Assert.Equal(1, row.GetProperty("noShowCount").GetInt32());
    }

    [Fact]
    public async Task A_doctor_can_correct_their_own_visit()
    {
        var (id, _, _) = await CompletedVisitAsync();

        var response = await DoctorSession.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow", reason = "patient never arrived" });

        Assert.Equal(200, response.StatusCode);
        Assert.Equal("NoShow", response["status"].GetString());
        var correction = Assert.Single(await CorrectionsAsync(id));
        Assert.Equal(DoctorSession.User!.Id, correction.ChangedByUserId);
    }

    [Fact]
    public async Task A_doctor_cannot_correct_another_doctors_visit()
    {
        var other = await World.NewDoctorAsync(Clinic, "Other");
        var (id, _, _) = await CompletedVisitAsync(doctor: other);

        var response = await DoctorSession.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });

        Assert.Equal(404, response.StatusCode);
        Assert.Empty(await CorrectionsAsync(id));
    }

    [Fact]
    public async Task Staff_of_another_clinic_cannot_correct()
    {
        var clinicB = await World.NewClinicAsync("B");
        var staffB = await World.LoginAsync(await World.NewStaffAsync(clinicB, isAdmin: true));
        var (id, _, _) = await CompletedVisitAsync();

        var response = await staffB.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });

        Assert.Equal(404, response.StatusCode);
        Assert.Empty(await CorrectionsAsync(id));
    }

    [Fact]
    public async Task A_patient_cannot_correct_a_status()
    {
        var (id, patient, _) = await CompletedVisitAsync();

        var response = await patient.PostAsync($"{Dash(id)}/correct-status", new { toStatus = "NoShow" });

        Assert.Equal(403, response.StatusCode);
        Assert.Empty(await CorrectionsAsync(id));
    }
}
