using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Flags;

/// <summary>
/// In-memory copy of the platform admin's per-clinic feature overrides (table clinic_feature_overrides).
/// Loaded once from the database, then kept current synchronously by the admin API on every write.
/// </summary>
public sealed class ClinicFeatureOverrides(IServiceScopeFactory scopes)
{
    private readonly object _gate = new();
    private volatile IReadOnlyDictionary<Guid, Dictionary<string, bool>> _overrides =
        new Dictionary<Guid, Dictionary<string, bool>>();
    private bool _loaded;

    /// <summary>The override for (clinic, key), or null when the super admin never set one.</summary>
    public bool? Get(Guid clinicId, string key)
    {
        EnsureLoaded();
        return _overrides.TryGetValue(clinicId, out var map) && map.TryGetValue(key, out var on) ? on : null;
    }

    /// <summary>Replaces the cached overrides of one clinic after the admin API persisted them.</summary>
    public void SetLocal(Guid clinicId, IReadOnlyDictionary<string, bool> map)
    {
        var copy = new Dictionary<Guid, Dictionary<string, bool>>(_overrides);
        copy[clinicId] = new Dictionary<string, bool>(map);
        _overrides = copy;
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
            var rows = db.ClinicFeatureOverrides.AsNoTracking().ToList();
            _overrides = rows
                .GroupBy(r => r.ClinicId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Key, r => r.Enabled));
            _loaded = true;
        }
    }
}
