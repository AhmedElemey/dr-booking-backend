namespace Moonlight.Api.Domain;

// Platform-admin switch: one row per (clinic, feature key) the super admin overrode. No row means the
// deployment default (clinic.config.json or the console pull) applies.
public class ClinicFeatureOverride
{
    public Guid ClinicId { get; set; }

    public Clinic? Clinic { get; set; }

    public required string Key { get; set; }

    public bool Enabled { get; set; }

    public DateTime UpdatedAt { get; set; }
}
