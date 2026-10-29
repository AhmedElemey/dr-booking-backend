using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Auth;

public static class UserScope
{
    // Doctors and staff carry clinicId (and doctors doctorId) in their token; everyone else carries neither.
    public static async Task<(Guid? ClinicId, Guid? DoctorId)> LoadAsync(this MoonlightDbContext db, User user, CancellationToken ct)
    {
        switch (user.Role)
        {
            case UserRole.Doctor:
                var doctor = await db.Doctors.Where(d => d.UserId == user.Id).Select(d => new { d.Id, d.ClinicId }).FirstOrDefaultAsync(ct);
                return (doctor?.ClinicId, doctor?.Id);
            case UserRole.ClinicStaff:
                var clinicId = await db.ClinicStaff.Where(s => s.UserId == user.Id).Select(s => (Guid?)s.ClinicId).FirstOrDefaultAsync(ct);
                return (clinicId, null);
            default:
                return (null, null);
        }
    }

    public static async Task<UserDto> ToDtoAsync(this MoonlightDbContext db, User user, CancellationToken ct)
    {
        var (clinicId, doctorId) = await db.LoadAsync(user, ct);
        return new UserDto(user.Id, user.Phone, user.FullName, user.Role, user.PreferredLanguage, clinicId, doctorId);
    }
}
