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
    private DateTime _loadedAt;
    private Guid? _soleClinicId;

    /// <summary>The override for (clinic, key), or null when the super admin never set one.</summary>
    public bool? Get(Guid clinicId, string key)
    {
        EnsureLoaded();
        return _overrides.TryGetValue(clinicId, out var map) && map.TryGetValue(key, out var on) ? on : null;
    }

    /// <summary>The id of the only clinic in the deployment, or null when there are none or several (reloaded every minute).</summary>
    public Guid? SoleClinicId
    {
        get
        {
            EnsureLoaded();
            return _soleClinicId;
        }
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
        if (_loaded && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(1))
        {
            return;
        }

        lock (_gate)
        {
            if (_loaded && DateTime.UtcNow - _loadedAt < TimeSpan.FromMinutes(1))
            {
                return;
            }

            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MoonlightDbContext>();
            var rows = db.ClinicFeatureOverrides.AsNoTracking().ToList();
            _overrides = rows
                .GroupBy(r => r.ClinicId)
                .ToDictionary(g => g.Key, g => g.ToDictionary(r => r.Key, r => r.Enabled));
            var clinicIds = db.Clinics.AsNoTracking().Select(c => c.Id).Take(2).ToList();
            _soleClinicId = clinicIds.Count == 1 ? clinicIds[0] : null;
            _loadedAt = DateTime.UtcNow;
            _loaded = true;
        }
    }
}
