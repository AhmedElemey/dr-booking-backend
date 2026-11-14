using System.Text.RegularExpressions;

namespace Moonlight.Api.Common;

public static partial class Slug
{
    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlphanumeric();

    public static string From(string text, string fallback = "doctor")
    {
        var slug = NonAlphanumeric().Replace(text.ToLowerInvariant(), "-").Trim('-');
        return slug.Length == 0 ? fallback : slug;
    }
}
