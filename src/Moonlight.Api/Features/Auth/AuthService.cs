using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Auth;
using Moonlight.Api.Infrastructure.Sms;

namespace Moonlight.Api.Features.Auth;

// The fixed development OTP. Registered in the container only when the host is not Production.
public sealed record FixedOtpBypass(string Code);

public sealed class AuthService(
    MoonlightDbContext db,
    TokenService tokens,
    ISmsSender sms,
    TimeProvider clock,
    ILogger<AuthService> logger,
    FixedOtpBypass? fixedOtp = null)
{
    public const int OtpTtlMinutes = 5;
    public const int MaxOtpAttempts = 5;

    public async Task RequestOtpAsync(string? phoneInput, CancellationToken ct)
    {
        var phone = PhoneNumber.Normalize(phoneInput);
        var now = clock.GetUtcNow().UtcDateTime;
        var code = TokenService.NewOtpCode();

        // Only the newest code for a phone is ever valid.
        await db.OtpCodes.Where(o => o.Phone == phone).ExecuteDeleteAsync(ct);
        db.OtpCodes.Add(new OtpCode
        {
            Phone = phone,
            CodeHash = tokens.HashOtp(phone, code),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(OtpTtlMinutes),
        });
        await db.SaveChangesAsync(ct);

        await sms.SendAsync(phone, $"رمز التحقق الخاص بك في مونلايت: {code} / Your Moonlight code: {code}", ct);
    }

    public async Task<AuthResponse> VerifyOtpAsync(string? phoneInput, string? code, CancellationToken ct)
    {
        var phone = PhoneNumber.Normalize(phoneInput);
        new ValidationErrors()
            .Require(code is { Length: 6 } && code.All(char.IsAsciiDigit), "code", "code must be 6 digits.")
            .ThrowIfAny();

        await CheckOtpAsync(phone, code!, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == phone, ct);
        var isNew = user is null;
        if (user is null)
        {
            user = new User { Phone = phone, Role = UserRole.Patient, CreatedAt = now };
            db.Users.Add(user);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (ex.IsUniqueViolation())
            {
                // A concurrent verify created the same user first.
                db.Entry(user).State = EntityState.Detached;
                user = await db.Users.FirstAsync(u => u.Phone == phone, ct);
                isNew = false;
            }
        }

        if (!user.IsActive)
        {
            throw new ApiException(ErrorCodes.Forbidden, "This account has been deactivated.");
        }

        return await IssueAsync(user, Guid.NewGuid(), isNew, ct);
    }

    public async Task<AuthResponse> RefreshAsync(string? refreshToken, CancellationToken ct)
    {
        new ValidationErrors().Require(!string.IsNullOrWhiteSpace(refreshToken), "refreshToken", "refreshToken is required.").ThrowIfAny();

        var now = clock.GetUtcNow().UtcDateTime;
        var hash = TokenService.HashRefreshToken(refreshToken!);
        var stored = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct)
            ?? throw new ApiException(ErrorCodes.Unauthorized);

        // Atomic claim: only one caller can rotate a given token.
        var claimed = stored.RevokedAt is null && stored.ExpiresAt > now
            && await db.RefreshTokens.Where(t => t.Id == stored.Id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct) == 1;
        if (!claimed)
        {
            if (stored.RevokedAt is not null || stored.ExpiresAt > now)
            {
                // A rotated token showed up again: assume it leaked and kill the whole family.
                logger.LogWarning("Refresh token reuse detected for user {UserId}; revoking family {FamilyId}.", stored.UserId, stored.FamilyId);
                await RevokeFamilyAsync(stored.FamilyId, now, ct);
            }

            throw new ApiException(ErrorCodes.Unauthorized);
        }

        var user = await db.Users.FirstAsync(u => u.Id == stored.UserId, ct);
        if (!user.IsActive)
        {
            await RevokeFamilyAsync(stored.FamilyId, now, ct);
            throw new ApiException(ErrorCodes.Unauthorized);
        }

        return await IssueAsync(user, stored.FamilyId, false, ct);
    }

    public async Task LogoutAsync(Guid userId, string? refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (!string.IsNullOrWhiteSpace(refreshToken))
        {
            var hash = TokenService.HashRefreshToken(refreshToken);
            var familyId = await db.RefreshTokens
                .Where(t => t.TokenHash == hash && t.UserId == userId)
                .Select(t => (Guid?)t.FamilyId)
                .FirstOrDefaultAsync(ct);
            if (familyId is { } family)
            {
                await RevokeFamilyAsync(family, now, ct);
            }

            return;
        }

        await db.RefreshTokens.Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private Task<int> RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct) =>
        db.RefreshTokens.Where(t => t.FamilyId == familyId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);

    private async Task CheckOtpAsync(string phone, string code, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        if (fixedOtp is not null && code == fixedOtp.Code)
        {
            await db.OtpCodes.Where(o => o.Phone == phone).ExecuteDeleteAsync(ct);
            return;
        }

        var otp = await db.OtpCodes.Where(o => o.Phone == phone).OrderByDescending(o => o.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new ApiException(ErrorCodes.OtpInvalid);
        if (otp.ExpiresAt <= now)
        {
            await db.OtpCodes.Where(o => o.Phone == phone).ExecuteDeleteAsync(ct);
            throw new ApiException(ErrorCodes.OtpExpired);
        }

        var matches = System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.UTF8.GetBytes(otp.CodeHash),
            System.Text.Encoding.UTF8.GetBytes(tokens.HashOtp(phone, code)));
        if (!matches)
        {
            otp.Attempts++;
            if (otp.Attempts >= MaxOtpAttempts)
            {
                db.OtpCodes.Remove(otp);
            }

            await db.SaveChangesAsync(ct);
            throw new ApiException(ErrorCodes.OtpInvalid);
        }

        await db.OtpCodes.Where(o => o.Phone == phone).ExecuteDeleteAsync(ct);
    }

    private async Task<AuthResponse> IssueAsync(User user, Guid familyId, bool isNew, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var (clinicId, doctorId) = await db.LoadAsync(user, ct);

        var refresh = TokenService.NewRefreshToken();
        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            FamilyId = familyId,
            TokenHash = TokenService.HashRefreshToken(refresh),
            CreatedAt = now,
            ExpiresAt = now + tokens.RefreshLifetime,
        });
        await db.SaveChangesAsync(ct);

        var dto = new UserDto(user.Id, user.Phone, user.FullName, user.Role, user.PreferredLanguage, clinicId, doctorId);
        return new AuthResponse(tokens.CreateAccessToken(user, clinicId, doctorId), refresh, tokens.AccessTokenSeconds, isNew, dto);
    }
}
