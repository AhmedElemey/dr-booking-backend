using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Auth;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Me;

public sealed record UpdateMeDto(string? FullName, string? PreferredLanguage, bool? WhatsAppOptIn = null, bool? MarketingOptIn = null);

public sealed record DeviceTokenDto(string? Token, DevicePlatform? Platform);

public sealed record DeviceTokenIdDto(Guid Id);

[ApiController]
[Route("api/v1/me")]
[Authorize]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
public class MeController(MoonlightDbContext db, CurrentUser current, TimeProvider clock) : ControllerBase
{
    /// <summary>The current user, with doctorId / clinicId for doctors and staff.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
    public async Task<UserDto> Get(CancellationToken ct) => await db.ToDtoAsync(await LoadUserAsync(ct), ct);

    /// <summary>Update the caller's full name, preferred language (ar|en) and the WhatsApp / marketing opt-ins (both default to true).</summary>
    [HttpPut]
    [ProducesResponseType(typeof(UserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
    public async Task<UserDto> Update(UpdateMeDto request, CancellationToken ct)
    {
        var errors = new ValidationErrors();
        if (request.FullName is not null)
        {
            errors.RequireText(request.FullName, "fullName", 200);
        }

        if (request.PreferredLanguage is not null)
        {
            errors.Require(request.PreferredLanguage is "ar" or "en", "preferredLanguage", "preferredLanguage must be 'ar' or 'en'.");
        }

        errors.ThrowIfAny();

        var user = await LoadUserAsync(ct);
        user.FullName = request.FullName?.Trim() ?? user.FullName;
        user.PreferredLanguage = request.PreferredLanguage ?? user.PreferredLanguage;
        user.WhatsAppOptIn = request.WhatsAppOptIn ?? user.WhatsAppOptIn;
        user.MarketingOptIn = request.MarketingOptIn ?? user.MarketingOptIn;
        await db.SaveChangesAsync(ct);
        return await db.ToDtoAsync(user, ct);
    }

    /// <summary>Register (or re-assign) an FCM device token for push notifications. Idempotent by token.</summary>
    [HttpPut("device-tokens")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(typeof(DeviceTokenIdDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
    public async Task<DeviceTokenIdDto> RegisterDeviceToken(DeviceTokenDto request, CancellationToken ct)
    {
        new ValidationErrors()
            .RequireText(request.Token, "token", 512)
            .Require(request.Platform is not null, "platform", "platform must be 'ios' or 'android'.")
            .ThrowIfAny();

        var token = request.Token!.Trim();
        var existing = await db.DeviceTokens.FirstOrDefaultAsync(t => t.FcmToken == token, ct);
        if (existing is null)
        {
            existing = new DeviceToken { FcmToken = token, CreatedAt = clock.GetUtcNow().UtcDateTime };
            db.DeviceTokens.Add(existing);
        }

        existing.UserId = current.Id;
        existing.Platform = request.Platform!.Value;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            // Concurrent registration of the same token: the other request won, which is the same outcome.
            db.Entry(existing).State = EntityState.Detached;
            existing = await db.DeviceTokens.FirstAsync(t => t.FcmToken == token, ct);
        }

        return new DeviceTokenIdDto(existing.Id);
    }

    /// <summary>Unregister a device token (on logout).</summary>
    [HttpDelete("device-tokens/{id:guid}")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteDeviceToken(Guid id, CancellationToken ct)
    {
        var userId = current.Id;
        var deleted = await db.DeviceTokens.Where(t => t.Id == id && t.UserId == userId).ExecuteDeleteAsync(ct);
        return deleted == 0 ? throw ApiException.NotFound() : NoContent();
    }

    private async Task<User> LoadUserAsync(CancellationToken ct)
    {
        var userId = current.Id;
        return await db.Users.FirstOrDefaultAsync(u => u.Id == userId, ct) ?? throw new ApiException(ErrorCodes.Unauthorized);
    }
}
