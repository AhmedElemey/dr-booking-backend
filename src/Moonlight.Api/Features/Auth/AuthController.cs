using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Auth;

[ApiController]
[Route("api/v1/auth")]
[Produces("application/json")]
public class AuthController(AuthService auth, CurrentUser current) : ControllerBase
{
    /// <summary>Send a 6-digit OTP by SMS to a phone number.</summary>
    /// <remarks>Rate limited: 1 per minute and 5 per hour per phone, 20 per hour per IP.</remarks>
    [HttpPost("otp/request")]
    [AllowAnonymous]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> RequestOtp(OtpRequestDto request, CancellationToken ct)
    {
        await auth.RequestOtpAsync(request.Phone, ct);
        return NoContent();
    }

    /// <summary>Verify an OTP and obtain an access/refresh token pair. Unknown phones become Patient users.</summary>
    [HttpPost("otp/verify")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
    public async Task<AuthResponse> VerifyOtp(OtpVerifyDto request, CancellationToken ct) =>
        await auth.VerifyOtpAsync(request.Phone, request.Code, ct);

    /// <summary>Exchange a refresh token for a new token pair. The old refresh token is rotated out; reusing it revokes the family.</summary>
    [HttpPost("refresh")]
    [AllowAnonymous]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
    public async Task<AuthResponse> Refresh(RefreshDto request, CancellationToken ct) =>
        await auth.RefreshAsync(request.RefreshToken, ct);

    /// <summary>Revoke the refresh-token family of the given token (or all of the caller's tokens when none is sent).</summary>
    [HttpPost("logout")]
    [Authorize]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Logout(LogoutDto? request, CancellationToken ct)
    {
        await auth.LogoutAsync(current.Id, request?.RefreshToken, ct);
        return NoContent();
    }
}
