using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Auth;

public static class UserScope
{
    // Doctors and staff carry clinicId (and doctors doctorId) in their token; everyone else carries neither.
    public static async Task<(Guid? ClinicId, Guid? DoctorId)> LoadAsync(this MoonlightDbContext db, User user, CancellationToken ct)
    {
        var (clinicId, doctorId, _) = await db.LoadScopeAsync(user, ct);
        return (clinicId, doctorId);
    }

    // The full scope triple: LoadScopeAsync adds whether a staff member is a clinic admin (ClinicStaff.IsAdmin).
    public static async Task<(Guid? ClinicId, Guid? DoctorId, bool IsClinicAdmin)> LoadScopeAsync(this MoonlightDbContext db, User user, CancellationToken ct)
    {
        switch (user.Role)
        {
            case UserRole.Doctor:
                var doctor = await db.Doctors.Where(d => d.UserId == user.Id).Select(d => new { d.Id, d.ClinicId }).FirstOrDefaultAsync(ct);
                return (doctor?.ClinicId, doctor?.Id, false);
            case UserRole.ClinicStaff:
                var staff = await db.ClinicStaff.Where(s => s.UserId == user.Id).Select(s => new { s.ClinicId, s.IsAdmin }).FirstOrDefaultAsync(ct);
                return (staff?.ClinicId, null, staff?.IsAdmin == true);
            default:
                return (null, null, false);
        }
    }

    public static async Task<UserDto> ToDtoAsync(this MoonlightDbContext db, User user, CancellationToken ct)
    {
        var (clinicId, doctorId, isClinicAdmin) = await db.LoadScopeAsync(user, ct);
        return new UserDto(user.Id, user.Phone, user.FullName, user.Role, user.PreferredLanguage, clinicId, doctorId, isClinicAdmin, user.WhatsAppOptIn, user.MarketingOptIn);
    }
}
