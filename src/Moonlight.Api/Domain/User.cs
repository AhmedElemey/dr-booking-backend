namespace Moonlight.Api.Domain;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Phone { get; set; }

    public string? FullName { get; set; }

    public UserRole Role { get; set; }

    public string PreferredLanguage { get; set; } = "ar";

    public bool IsActive { get; set; } = true;

    public bool WhatsAppOptIn { get; set; } = true;

    public bool MarketingOptIn { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

public class ClinicStaff
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public Guid ClinicId { get; set; }

    public Clinic? Clinic { get; set; }

    // Clinic admin: manages the clinic's staff and sensitive corrections (status correction, refunds). Ordinary reception has this off.
    public bool IsAdmin { get; set; }
}

public class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Phone { get; set; }

    public required string CodeHash { get; set; }

    public DateTime ExpiresAt { get; set; }

    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class RefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public required string TokenHash { get; set; }

    public Guid FamilyId { get; set; }

    public DateTime ExpiresAt { get; set; }

    public DateTime? RevokedAt { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class DeviceToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public required string FcmToken { get; set; }

    public DevicePlatform Platform { get; set; }

    public DateTime CreatedAt { get; set; }
}
