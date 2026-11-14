using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using Moonlight.Api.Common;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Tests;

public sealed class ClinicConfigTests : IDisposable
{
    private static readonly string RepoRoot = FindRepoRoot();
    private readonly string _dir = Directory.CreateTempSubdirectory("clinic-config-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "clinic.config.json")) && File.Exists(Path.Combine(dir.FullName, "Moonlight.sln")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException("Repository root not found.");
    }

    private static JsonObject ValidJson() => JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "clinic.config.json")))!.AsObject();

    private string Write(JsonObject json)
    {
        File.WriteAllText(Path.Combine(_dir, "clinic.config.json"), json.ToJsonString());
        Directory.CreateDirectory(Path.Combine(_dir, "branding"));
        File.WriteAllText(Path.Combine(_dir, "branding", "logo.svg"), "<svg xmlns=\"http://www.w3.org/2000/svg\"/>");
        return Path.Combine(_dir, "clinic.config.json");
    }

    private string LoadError(Action<JsonObject> mutate)
    {
        var json = ValidJson();
        mutate(json);
        return Assert.Throws<ClinicConfigException>(() => ClinicConfigLoader.LoadFile(Write(json))).Message;
    }

    [Fact]
    public void The_shipped_config_is_valid_and_keeps_todays_identity()
    {
        var config = ClinicConfigLoader.LoadFile(Path.Combine(RepoRoot, "clinic.config.json"));
        var raw = ValidJson();

        // Whatever clinic this repo was made for, the loaded config must mirror its clinic.config.json exactly.
        Assert.Equal(raw["slug"]!.GetValue<string>(), config.Slug);
        Assert.Equal(raw["name"]!["en"]!.GetValue<string>(), config.Name.En);
        Assert.Equal(raw["name"]!["ar"]!.GetValue<string>(), config.Name.Ar);
        Assert.Equal(raw["colors"]!["primary"]!.GetValue<string>(), config.Colors.Primary);
        Assert.Equal(raw["mobile"]!["androidApplicationId"]!.GetValue<string>(), config.Mobile.AndroidApplicationId);
        Assert.Equal(raw["supportPhone"]!.GetValue<string>(), config.SupportPhone);
        Assert.True(File.Exists(config.LogoPath));
        Assert.Equal(FeatureKeys.All.Order(), config.Features.Keys.Order());
        foreach (var key in FeatureKeys.All)
        {
            Assert.Equal(raw["features"]![key]!.GetValue<bool>(), config.Features[key]);
        }
    }

    [Fact]
    public void The_shipped_config_validates_against_the_json_schema()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "clinic.config.schema.json"))).RootElement;
        var errors = new List<string>();
        MiniSchema.Validate(schema, schema, ValidJson(), "$", errors);
        Assert.Empty(errors);
    }

    [Fact]
    public void The_schema_and_the_code_agree_on_features_and_required_fields()
    {
        var schema = JsonDocument.Parse(File.ReadAllText(Path.Combine(RepoRoot, "clinic.config.schema.json"))).RootElement;
        var features = schema.GetProperty("properties").GetProperty("features");
        Assert.Equal(FeatureKeys.All.Order(), features.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());
        Assert.Equal(FeatureKeys.All.Order(), features.GetProperty("properties").EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal(
            ["colors", "console", "features", "logo", "mobile", "name", "slug", "supportPhone", "tagline", "website"],
            schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()!).Order());
    }

    [Theory]
    [InlineData("slug", "Bad Slug", "slug must be")]
    [InlineData("slug", "", "slug is required")]
    [InlineData("supportPhone", "call me", "supportPhone")]
    public void Invalid_top_level_values_are_reported_with_the_field_name(string field, string value, string expected)
    {
        var message = LoadError(json => json[field] = value);
        Assert.Contains(expected, message);
        Assert.Contains("invalid", message);
    }

    [Fact]
    public void Invalid_colours_and_names_are_reported()
    {
        var message = LoadError(json =>
        {
            json["colors"]!["primary"] = "blue";
            json["name"]!["en"] = "";
        });
        Assert.Contains("colors.primary", message);
        Assert.Contains("name.en", message);
    }

    [Fact]
    public void Missing_and_unknown_features_are_rejected()
    {
        var missing = LoadError(json => json["features"]!.AsObject().Remove("guestBrowsing"));
        Assert.Contains("features.guestBrowsing is required", missing);

        var unknown = LoadError(json => json["features"]!["telepathy"] = true);
        Assert.Contains("features.telepathy is not a known feature", unknown);
    }

    [Fact]
    public void A_missing_logo_file_and_a_bad_console_url_are_rejected()
    {
        Assert.Contains("logo", LoadError(json => json["logo"] = "branding/missing.svg"));
        Assert.Contains("console.url", LoadError(json => json["console"]!["url"] = "not a url"));
    }

    [Fact]
    public void Malformed_json_and_a_missing_file_give_clear_errors()
    {
        var path = Path.Combine(_dir, "broken.json");
        File.WriteAllText(path, "{ nope");
        Assert.Contains("not valid", Assert.Throws<ClinicConfigException>(() => ClinicConfigLoader.LoadFile(path)).Message);
        Assert.Contains("not found", Assert.Throws<ClinicConfigException>(() => ClinicConfigLoader.LoadFile(Path.Combine(_dir, "nope.json"))).Message);
    }

    [Fact]
    public void The_logo_path_is_resolved_next_to_the_config_file()
    {
        var config = ClinicConfigLoader.LoadFile(Write(ValidJson()));
        Assert.Equal(Path.Combine(_dir, "branding", "logo.svg"), config.LogoPath);
    }

    [Fact]
    public void Locate_prefers_an_explicit_path()
    {
        Assert.Equal(Path.Combine(_dir, "x.json"), ClinicConfigLoader.Locate("x.json", _dir));
        Assert.EndsWith("clinic.config.json", ClinicConfigLoader.Locate(null, RepoRoot));
    }

    [Fact]
    public void An_invalid_config_stops_the_api_from_starting_with_a_clear_message()
    {
        var json = ValidJson();
        json["colors"]!["accent"] = "teal";
        var path = Write(json);

        using var factory = new MoonlightApiFactory("Host=localhost;Database=unused", settings: new() { ["ClinicConfig:Path"] = path });
        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());
        var message = string.Join(" | ", Chain(ex).Select(e => e.Message));
        Assert.Contains("colors.accent", message);
    }

    private static IEnumerable<Exception> Chain(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException!)
        {
            yield return current;
        }
    }
}

// Just enough JSON Schema (type, required, additionalProperties, properties, $ref, pattern, min/maxLength) for clinic.config.schema.json.
internal static class MiniSchema
{
    public static void Validate(JsonElement root, JsonElement schema, JsonNode? value, string path, List<string> errors)
    {
        if (schema.TryGetProperty("$ref", out var reference))
        {
            var target = root;
            foreach (var part in reference.GetString()!.TrimStart('#', '/').Split('/'))
            {
                target = target.GetProperty(part);
            }

            Validate(root, target, value, path, errors);
            return;
        }

        var type = schema.TryGetProperty("type", out var t) ? t.GetString() : null;
        switch (type)
        {
            case "object":
                if (value is not JsonObject obj)
                {
                    errors.Add($"{path}: expected object");
                    return;
                }

                var properties = schema.TryGetProperty("properties", out var p) ? p : default;
                foreach (var required in schema.TryGetProperty("required", out var r) ? r.EnumerateArray() : [])
                {
                    if (!obj.ContainsKey(required.GetString()!))
                    {
                        errors.Add($"{path}: missing {required.GetString()}");
                    }
                }

                foreach (var (name, child) in obj)
                {
                    if (properties.ValueKind == JsonValueKind.Object && properties.TryGetProperty(name, out var childSchema))
                    {
                        Validate(root, childSchema, child, $"{path}.{name}", errors);
                    }
                    else if (schema.TryGetProperty("additionalProperties", out var ap) && ap.ValueKind == JsonValueKind.False)
                    {
                        errors.Add($"{path}: unexpected {name}");
                    }
                }

                break;
            case "string":
                if (value is not JsonValue sv || !sv.TryGetValue<string>(out var text))
                {
                    errors.Add($"{path}: expected string");
                    return;
                }

                if (schema.TryGetProperty("minLength", out var min) && text.Length < min.GetInt32())
                {
                    errors.Add($"{path}: too short");
                }

                if (schema.TryGetProperty("maxLength", out var max) && text.Length > max.GetInt32())
                {
                    errors.Add($"{path}: too long");
                }

                if (schema.TryGetProperty("pattern", out var pattern) && !Regex.IsMatch(text, pattern.GetString()!))
                {
                    errors.Add($"{path}: does not match {pattern.GetString()}");
                }

                break;
            case "boolean":
                if (value is not JsonValue bv || !bv.TryGetValue<bool>(out _))
                {
                    errors.Add($"{path}: expected boolean");
                }

                break;
        }
    }
}
