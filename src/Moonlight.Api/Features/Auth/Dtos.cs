using Moonlight.Api.Domain;

namespace Moonlight.Api.Features.Auth;

public sealed record OtpRequestDto(string? Phone);

public sealed record OtpVerifyDto(string? Phone, string? Code);

public sealed record RefreshDto(string? RefreshToken);

public sealed record LogoutDto(string? RefreshToken);

public sealed record UserDto(
    Guid Id,
    string Phone,
    string? FullName,
    UserRole Role,
    string PreferredLanguage,
    Guid? ClinicId,
    Guid? DoctorId,
    bool WhatsAppOptIn,
    bool MarketingOptIn);

public sealed record AuthResponse(string AccessToken, string RefreshToken, int ExpiresIn, bool IsNewUser, UserDto User);
