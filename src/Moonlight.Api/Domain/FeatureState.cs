namespace Moonlight.Api.Domain;

/// <summary>Last good copy of the console's feature switches (single row, id 1), so a restart without the console keeps them.</summary>
public class FeatureState
{
    public const int SingletonId = 1;

    public int Id { get; set; } = SingletonId;

    /// <summary>JSON object of feature key to boolean.</summary>
    public required string Features { get; set; }

    /// <summary>The console's own <c>updatedAt</c> for this copy.</summary>
    public DateTime ConsoleUpdatedAt { get; set; }

    public DateTime FetchedAt { get; set; }
}
