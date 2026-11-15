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

public sealed class PaymobOptions
{
    public string BaseUrl { get; set; } = "https://accept.paymob.com";

    public string ApiKey { get; set; } = "";

    public string IntegrationId { get; set; } = "";

    public string IframeId { get; set; } = "";

    // Used to verify transaction callbacks (HMAC-SHA512). Never commit it; set Payments__Paymob__HmacSecret.
    public string HmacSecret { get; set; } = "";
}

public sealed class PaymentsOptions
{
    public const string Section = "Payments";

    // "Mock" (default; a backend-served checkout page) or "Paymob".
    public string Provider { get; set; } = "Mock";

    // Public base URL of this API, used to build checkout and return links. Empty: taken from the current request.
    public string PublicBaseUrl { get; set; } = "";

    // Hosts a returnUrl may point to (in addition to the clinic website domain and the mobile app scheme).
    public string[] ReturnUrlHosts { get; set; } = [];

    public PaymobOptions Paymob { get; set; } = new();
}

public sealed class WhatsAppOptions
{
    public const string Section = "WhatsApp";

    // "Mock" (default; logs only) or "Meta" (WhatsApp Cloud API).
    public string Provider { get; set; } = "Mock";

    public string ApiBaseUrl { get; set; } = "https://graph.facebook.com/v21.0";

    public string PhoneNumberId { get; set; } = "";

    public string AccessToken { get; set; } = "";

    // Meta language codes of the approved templates.
    public string LanguageCodeAr { get; set; } = "ar";

    public string LanguageCodeEn { get; set; } = "en";

    // Approved template name per message kind (keys confirmed, cancelled, reminder, waitlist, review, recall; empty keeps the built-in default name).
    public Dictionary<string, string> Templates { get; set; } = [];
}

public sealed class JitsiOptions
{
    public string BaseUrl { get; set; } = "https://meet.jit.si";
}

public sealed class VideoOptions
{
    public const string Section = "Video";

    // "Jitsi" (default) or "Mock".
    public string Provider { get; set; } = "Jitsi";

    public JitsiOptions Jitsi { get; set; } = new();
}

public sealed class BackgroundJobsOptions
{
    public const string Section = "Jobs";

    // The "rate your visit" prompt goes out this long after an appointment is marked Completed.
    public int ReviewPromptDelayMinutes { get; set; } = 30;

    // Recurring recall campaigns run once a day at this clinic-local hour.
    public int RecallHour { get; set; } = 10;
}
