using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Dashboard;

// Resource scope for dashboard callers: a Doctor reaches only their own doctor row, ClinicStaff every doctor of their clinic.
// Anything outside the scope is reported as NOT_FOUND so existence is never leaked.
public sealed class DashboardScope(MoonlightDbContext db, CurrentUser current)
{
    public Guid ClinicId => current.ClinicId ?? throw new ApiException(ErrorCodes.Forbidden);

    public bool IsDoctor => current.Role == UserRole.Doctor;

    public IQueryable<Doctor> Doctors()
    {
        if (IsDoctor)
        {
            var doctorId = current.DoctorId ?? throw new ApiException(ErrorCodes.Forbidden);
            return db.Doctors.Where(d => d.Id == doctorId);
        }

        var clinicId = ClinicId;
        return db.Doctors.Where(d => d.ClinicId == clinicId);
    }

    public IQueryable<Slot> Slots()
    {
        if (IsDoctor)
        {
            var doctorId = current.DoctorId ?? throw new ApiException(ErrorCodes.Forbidden);
            return db.Slots.Where(s => s.DoctorId == doctorId);
        }

        var clinicId = ClinicId;
        return db.Slots.Where(s => s.ClinicId == clinicId);
    }

    public IQueryable<Appointment> Appointments()
    {
        if (IsDoctor)
        {
            var doctorId = current.DoctorId ?? throw new ApiException(ErrorCodes.Forbidden);
            return db.Appointments.Where(a => a.Slot!.DoctorId == doctorId);
        }

        var clinicId = ClinicId;
        return db.Appointments.Where(a => a.Slot!.ClinicId == clinicId);
    }

    public async Task<Doctor> RequireDoctorAsync(Guid doctorId, CancellationToken ct) =>
        await Doctors().Include(d => d.Clinic).FirstOrDefaultAsync(d => d.Id == doctorId, ct) ?? throw ApiException.NotFound();

    public async Task<Clinic> RequireClinicAsync(CancellationToken ct)
    {
        var clinicId = ClinicId;
        return await db.Clinics.FirstOrDefaultAsync(c => c.Id == clinicId, ct) ?? throw ApiException.NotFound();
    }
}
