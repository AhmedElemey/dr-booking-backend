using System.Security.Claims;

using Moonlight.Api.Domain;

namespace Moonlight.Api.Common;

// The authenticated caller, read from the JWT claims (sub, role, clinicId?, doctorId?).
public sealed class CurrentUser(IHttpContextAccessor accessor)
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated == true;

    public Guid Id => ParseGuid("sub") ?? throw new ApiException(ErrorCodes.Unauthorized);

    public UserRole Role => Enum.TryParse<UserRole>(Principal?.FindFirstValue("role"), out var role)
        ? role
        : throw new ApiException(ErrorCodes.Unauthorized);

    public Guid? ClinicId => ParseGuid("clinicId");

    public Guid? DoctorId => ParseGuid("doctorId");

    private Guid? ParseGuid(string claim) => Guid.TryParse(Principal?.FindFirstValue(claim), out var id) ? id : null;
}
