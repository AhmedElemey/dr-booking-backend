using System.Text.Json.Nodes;

namespace Moonlight.Tests;

/// <summary>
/// The hosted demo runs the same clinic as the shipped config with a wider feature set:
/// clinic.config.demo.json must mirror clinic.config.json exactly outside the features block.
/// </summary>
public sealed class DemoClinicConfigTests
{
    private static readonly string RepoRoot = FindRepoRoot();

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

    [Fact]
    public void Demo_config_mirrors_the_shipped_config_except_for_features()
    {
        var production = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "clinic.config.json")))!.AsObject();
        var demo = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "clinic.config.demo.json")))!.AsObject();

        Assert.NotEqual(production["features"]!.ToJsonString(), demo["features"]!.ToJsonString());

        production.Remove("features");
        demo.Remove("features");
        Assert.Equal(production.ToJsonString(), demo.ToJsonString());
    }

    [Fact]
    public void Demo_config_enables_exactly_the_demo_feature_set()
    {
        var demo = JsonNode.Parse(File.ReadAllText(Path.Combine(RepoRoot, "clinic.config.demo.json")))!["features"]!.AsObject();

        string[] on = ["waitingList", "familyAccounts", "insurance", "holidayCalendar", "workingHoursTemplates", "reviews", "promotions", "reports"];
        foreach (var key in on)
        {
            Assert.True(demo[key]!.GetValue<bool>(), $"{key} should be on in the demo config");
        }

        string[] off = ["patientRecords", "onlinePayments", "recallCampaigns", "whatsappReminders", "whatsappOtp", "smsNotifications", "videoConsultation"];
        foreach (var key in off)
        {
            Assert.False(demo[key]!.GetValue<bool>(), $"{key} should stay off in the demo config");
        }
    }
}
