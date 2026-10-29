using System.Security.Cryptography;
using System.Text;

using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure.Auth;

// Issues HS256 access tokens (claims: sub, role, clinicId?, doctorId?) and the opaque refresh / OTP primitives.
public sealed class TokenService(IOptions<JwtOptions> options, TimeProvider clock)
{
    private readonly JwtOptions _options = options.Value;

    public int AccessTokenSeconds => _options.AccessTokenMinutes * 60;

    public TimeSpan RefreshLifetime => TimeSpan.FromDays(_options.RefreshTokenDays);

    public static SymmetricSecurityKey SigningKey(string key) => new(Encoding.UTF8.GetBytes(key));

    public string CreateAccessToken(User user, Guid? clinicId, Guid? doctorId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var claims = new Dictionary<string, object>
        {
            ["sub"] = user.Id.ToString(),
            ["role"] = user.Role.ToString(),
        };
        if (clinicId is { } clinic)
        {
            claims["clinicId"] = clinic.ToString();
        }

        if (doctorId is { } doctor)
        {
            claims["doctorId"] = doctor.ToString();
        }

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Claims = claims,
            IssuedAt = now,
            Expires = now.AddMinutes(_options.AccessTokenMinutes),
            SigningCredentials = new SigningCredentials(SigningKey(_options.Key), SecurityAlgorithms.HmacSha256),
        };
        return new JsonWebTokenHandler().CreateToken(descriptor);
    }

    public static string NewRefreshToken() => Base64Url(RandomNumberGenerator.GetBytes(48));

    public static string HashRefreshToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static string NewOtpCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    // Keyed hash so a leaked otp_codes table cannot be reversed by brute-forcing the 10^6 code space.
    public string HashOtp(string phone, string code)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(_options.Key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{phone}:{code}")));
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
