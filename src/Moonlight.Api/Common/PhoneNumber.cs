using System.Text.RegularExpressions;

namespace Moonlight.Api.Common;

// Egyptian mobile numbers only (010 / 011 / 012 / 015), normalized to E.164 (+201012345678).
public static partial class PhoneNumber
{
    [GeneratedRegex(@"^(?:\+?20|0)?(1[0125]\d{8})$")]
    private static partial Regex EgyptianMobile();

    public static bool TryNormalize(string? input, out string e164)
    {
        e164 = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var cleaned = input.Trim().Replace(" ", "").Replace("-", "").Replace("(", "").Replace(")", "");
        if (cleaned.StartsWith("00", StringComparison.Ordinal))
        {
            cleaned = "+" + cleaned[2..];
        }

        var match = EgyptianMobile().Match(cleaned);
        if (!match.Success)
        {
            return false;
        }

        e164 = "+20" + match.Groups[1].Value;
        return true;
    }

    public static string Normalize(string? input, string field = "phone") =>
        TryNormalize(input, out var e164)
            ? e164
            : throw ApiException.Validation(field, "Enter a valid Egyptian mobile number, for example 01012345678.");

    /// <summary>Hides the middle of a number for logs: +201012345678 becomes +2010****78.</summary>
    public static string Mask(string? phone)
    {
        if (string.IsNullOrEmpty(phone) || phone.Length < 8)
        {
            return "****";
        }

        return phone[..5] + "****" + phone[^2..];
    }
}
