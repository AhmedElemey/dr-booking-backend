namespace Moonlight.Api.Domain;

// Role names as they appear in the JWT `role` claim and in [Authorize(Roles = ...)].
public static class Roles
{
    public const string Patient = nameof(UserRole.Patient);
    public const string Doctor = nameof(UserRole.Doctor);
    public const string ClinicStaff = nameof(UserRole.ClinicStaff);
    public const string PlatformAdmin = nameof(UserRole.PlatformAdmin);
    public const string Dashboard = Doctor + "," + ClinicStaff;
}
