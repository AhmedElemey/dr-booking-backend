namespace Moonlight.Api.Common;

// Language of the response, from Accept-Language (ar|en); Arabic is the default.
public sealed class Language(IHttpContextAccessor accessor)
{
    public string Code { get; } = accessor.HttpContext is { } context ? FromRequest(context.Request) : "ar";

    public bool IsEnglish => Code == "en";

    public string Pick(string ar, string en) => IsEnglish ? en : ar;

    public string? PickOptional(string? ar, string? en) => IsEnglish ? en ?? ar : ar ?? en;

    public static string FromRequest(HttpRequest request)
    {
        var header = request.Headers.AcceptLanguage.ToString();
        foreach (var part in header.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var tag = part.Split(';')[0].Trim().ToLowerInvariant();
            if (tag.StartsWith("en", StringComparison.Ordinal))
            {
                return "en";
            }

            if (tag.StartsWith("ar", StringComparison.Ordinal))
            {
                return "ar";
            }
        }

        return "ar";
    }
}
