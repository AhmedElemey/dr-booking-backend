using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Flags;

public sealed record StoredFeatures(IReadOnlyDictionary<string, bool> Features, DateTime UpdatedAt);

public interface IFeatureStateStore
{
    Task<StoredFeatures?> LoadAsync(CancellationToken ct);

    Task SaveAsync(IReadOnlyDictionary<string, bool> features, DateTime consoleUpdatedAt, DateTime fetchedAt, CancellationToken ct);
}

public sealed class EfFeatureStateStore(IServiceScopeFactory scopes) : IFeatureStateStore
{
    public async Task<StoredFeatures?> LoadAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
        var row = await db.FeatureStates.AsNoTracking().FirstOrDefaultAsync(f => f.Id == FeatureState.SingletonId, ct);
        if (row is null)
        {
            return null;
        }

        var features = JsonSerializer.Deserialize<Dictionary<string, bool>>(row.Features);
        return features is null ? null : new StoredFeatures(features, row.ConsoleUpdatedAt);
    }

    public async Task SaveAsync(IReadOnlyDictionary<string, bool> features, DateTime consoleUpdatedAt, DateTime fetchedAt, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
        var json = JsonSerializer.Serialize(features);
        var row = await db.FeatureStates.FirstOrDefaultAsync(f => f.Id == FeatureState.SingletonId, ct);
        if (row is null)
        {
            db.FeatureStates.Add(new FeatureState { Features = json, ConsoleUpdatedAt = consoleUpdatedAt, FetchedAt = fetchedAt });
        }
        else
        {
            row.Features = json;
            row.ConsoleUpdatedAt = consoleUpdatedAt;
            row.FetchedAt = fetchedAt;
        }

        await db.SaveChangesAsync(ct);
    }
}
