using System.Text.Json;
using System.Text.RegularExpressions;

using Moonlight.Api.Features.Flags;

namespace Moonlight.Api.Common;

public sealed record LocalizedText(string Ar, string En);

public sealed record ClinicColors(string Primary, string Accent);

public sealed record ClinicWebsite(string Domain);

public sealed record ClinicMobile(string AndroidApplicationId, string IosBundleId);

public sealed record ClinicConsoleRef(string Url, string ClinicId);

/// <summary>The clinic's identity and default feature switches, loaded once from <c>clinic.config.json</c>.</summary>
public sealed record ClinicConfig(
    string Slug,
    LocalizedText Name,
    LocalizedText Tagline,
    ClinicColors Colors,
    string Logo,
    string SupportPhone,
    ClinicWebsite Website,
    ClinicMobile Mobile,
    ClinicConsoleRef Console,
    IReadOnlyDictionary<string, bool> Features)
{
    /// <summary>Absolute path of the logo file; set by the loader.</summary>
    public string LogoPath { get; init; } = "";
}

public sealed class ClinicConfigException(string message) : Exception(message);

public static partial class ClinicConfigLoader
{
    public const string FileName = "clinic.config.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Finds the config file: explicit path, then next to the binaries, then the content root and its parents.</summary>
    public static string Locate(string? explicitPath, string contentRoot)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath, contentRoot);
        }

        var candidates = new List<string> { Path.Combine(AppContext.BaseDirectory, FileName) };
        for (var dir = new DirectoryInfo(contentRoot); dir is not null; dir = dir.Parent)
        {
            candidates.Add(Path.Combine(dir.FullName, FileName));
        }

        return candidates.FirstOrDefault(File.Exists) ?? candidates[0];
    }

    public static ClinicConfig LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            throw new ClinicConfigException($"Clinic config not found at '{path}'. Add clinic.config.json (or set ClinicConfig__Path).");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static ClinicConfig Parse(string json, string path)
    {
        ClinicConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<ClinicConfig>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new ClinicConfigException($"Clinic config '{path}' is not valid: {ex.Message}");
        }

        if (config is null)
        {
            throw new ClinicConfigException($"Clinic config '{path}' is empty.");
        }

        var errors = Validate(config);
        var logoPath = Path.GetFullPath(config.Logo ?? "", Path.GetDirectoryName(Path.GetFullPath(path))!);
        if (errors.Count == 0 && !File.Exists(logoPath))
        {
            errors.Add($"logo: file '{config.Logo}' does not exist (looked for {logoPath}).");
        }

        if (errors.Count > 0)
        {
            throw new ClinicConfigException($"Clinic config '{path}' is invalid:{Environment.NewLine}- " + string.Join(Environment.NewLine + "- ", errors));
        }

        return config with { LogoPath = logoPath };
    }

    // Mirrors clinic.config.schema.json; a test keeps the two in step.
    public static List<string> Validate(ClinicConfig config)
    {
        var errors = new List<string>();

        void Text(string? value, string field, int max = 200)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                errors.Add($"{field} is required.");
            }
            else if (value.Length > max)
            {
                errors.Add($"{field} must be at most {max} characters.");
            }
        }

        Text(config.Slug, "slug", 40);
        if (!string.IsNullOrWhiteSpace(config.Slug) && !SlugPattern().IsMatch(config.Slug))
        {
            errors.Add("slug must be lower-case letters, digits and single hyphens (e.g. smile-care).");
        }

        Text(config.Name?.Ar, "name.ar");
        Text(config.Name?.En, "name.en");
        Text(config.Tagline?.Ar, "tagline.ar");
        Text(config.Tagline?.En, "tagline.en");
        foreach (var (field, color) in new[] { ("colors.primary", config.Colors?.Primary), ("colors.accent", config.Colors?.Accent) })
        {
            if (color is null || !HexColor().IsMatch(color))
            {
                errors.Add($"{field} must be a hex colour like #3F3CCF.");
            }
        }

        Text(config.Logo, "logo", 500);
        if (config.SupportPhone is null || !Phone().IsMatch(config.SupportPhone))
        {
            errors.Add("supportPhone must be a phone number such as +201000000000.");
        }

        Text(config.Website?.Domain, "website.domain");
        Text(config.Mobile?.AndroidApplicationId, "mobile.androidApplicationId");
        // The id doubles as the payment-return URI scheme, and `_` is not allowed in a URI scheme.
        if (!string.IsNullOrWhiteSpace(config.Mobile?.AndroidApplicationId) && !AppIdPattern().IsMatch(config.Mobile.AndroidApplicationId))
        {
            errors.Add("mobile.androidApplicationId must be lower-case dot-separated segments of letters and digits without underscores (e.g. com.moonlight.app).");
        }

        Text(config.Mobile?.IosBundleId, "mobile.iosBundleId");
        if (config.Console is null)
        {
            errors.Add("console is required (use empty strings to disable the runtime pull).");
        }
        else if (!string.IsNullOrWhiteSpace(config.Console.Url)
            && !(Uri.TryCreate(config.Console.Url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"))
        {
            errors.Add("console.url must be an absolute http(s) URL or empty.");
        }

        if (config.Features is null)
        {
            errors.Add("features is required.");
        }
        else
        {
            foreach (var key in FeatureKeys.All.Where(k => !config.Features.ContainsKey(k)))
            {
                errors.Add($"features.{key} is required (true or false).");
            }

            foreach (var key in config.Features.Keys.Where(k => !FeatureKeys.All.Contains(k)))
            {
                errors.Add($"features.{key} is not a known feature. Known: {string.Join(", ", FeatureKeys.All)}.");
            }
        }

        return errors;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(\.[a-z][a-z0-9]*)+$")]
    private static partial Regex AppIdPattern();

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^\+?[0-9 ()-]{6,20}$")]
    private static partial Regex Phone();
}
