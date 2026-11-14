namespace Moonlight.Api.Common;

public sealed class BookingOptions
{
    public const string Section = "Booking";

    public int MaxUpcomingPerPatient { get; set; } = 3;

    public int PatientCancelWindowHours { get; set; } = 2;

    public int MinLeadMinutes { get; set; } = 60;
}

public sealed class JwtOptions
{
    public const string Section = "Jwt";

    public string Key { get; set; } = "";

    public string Issuer { get; set; } = "moonlight";

    public string Audience { get; set; } = "moonlight";

    public int AccessTokenMinutes { get; set; } = 15;

    public int RefreshTokenDays { get; set; } = 30;
}

public sealed class AuthOptions
{
    public const string Section = "Auth";

    // Development / staging only. Startup fails when this is set in Production.
    public string? FixedOtp { get; set; }
}

public sealed class RateLimitOptions
{
    public const string Section = "RateLimits";

    public int OtpPerPhonePerMinute { get; set; } = 1;

    public int OtpPerPhonePerHour { get; set; } = 5;

    public int OtpPerIpPerHour { get; set; } = 20;
}

public sealed class SmsOptions
{
    public const string Section = "Sms";

    // "Log" is the stub that writes the message to the log; a real provider lands with M6.
    public string Provider { get; set; } = "Log";

    // Confirmations / reminders by SMS for patients without a device token. OTP delivery is independent of this.
    public bool NotificationsEnabled { get; set; }
}

public sealed class FcmOptions
{
    public const string Section = "Fcm";

    public string? ServiceAccountJson { get; set; }
}

public sealed class StorageOptions
{
    public const string Section = "Storage";

    public string Path { get; set; } = "uploads";

    public string RequestPath { get; set; } = "/uploads";
}

public sealed class RemindersOptions
{
    public const string Section = "Reminders";

    public bool Enabled { get; set; } = true;

    public int IntervalMinutes { get; set; } = 5;
}
