using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Flags;

public static class FeatureKeys
{
    public const string MobileApp = "mobileApp";
    public const string Website = "website";
    public const string GuestBrowsing = "guestBrowsing";
    public const string PatientSelfService = "patientSelfService";
    public const string PushReminders = "pushReminders";
    public const string SmsNotifications = "smsNotifications";
    public const string EnglishLanguage = "englishLanguage";
    public const string MultiBranch = "multiBranch";
    public const string VideoConsultation = "videoConsultation";
    public const string WaitingList = "waitingList";
    public const string FamilyAccounts = "familyAccounts";
    public const string WhatsappReminders = "whatsappReminders";
    public const string WhatsappOtp = "whatsappOtp";
    public const string OnlinePayments = "onlinePayments";
    public const string Insurance = "insurance";
    public const string HolidayCalendar = "holidayCalendar";
    public const string WorkingHoursTemplates = "workingHoursTemplates";
    public const string PatientRecords = "patientRecords";
    public const string Reviews = "reviews";
    public const string Promotions = "promotions";
    public const string RecallCampaigns = "recallCampaigns";
    public const string Reports = "reports";
    public const string LiveQueue = "liveQueue";
    public const string ManualReview = "manualReview";
    public const string PublicPrices = "publicPrices";

    public static readonly IReadOnlyList<string> All =
    [
        MobileApp,
        Website,
        GuestBrowsing,
        PatientSelfService,
        PushReminders,
        SmsNotifications,
        EnglishLanguage,
        MultiBranch,
        VideoConsultation,
        WaitingList,
        FamilyAccounts,
        WhatsappReminders,
        WhatsappOtp,
        OnlinePayments,
        Insurance,
        HolidayCalendar,
        WorkingHoursTemplates,
        PatientRecords,
        Reviews,
        Promotions,
        RecallCampaigns,
        Reports,
        LiveQueue,
        ManualReview,
        PublicPrices,
    ];
}

public static class FeatureSources
{
    public const string Console = "console";
    public const string Cache = "cache";
    public const string Defaults = "defaults";
}

public sealed record FeatureSnapshot(IReadOnlyDictionary<string, bool> Features, string Source, DateTime UpdatedAt);

/// <summary>
/// The feature switches in effect right now: the clinic.config.json defaults until a console pull (or the cached copy of
/// one) replaces them. Thread-safe; readers always see a complete snapshot.
/// </summary>
public sealed class FeatureService
{
    private readonly IReadOnlyDictionary<string, bool> _defaults;
    private readonly ClinicFeatureOverrides? _overrides;
    private readonly AsyncLocal<Guid?> _clinic = new();
    private volatile FeatureSnapshot _current;

    public FeatureService(ClinicConfig config, TimeProvider clock, ClinicFeatureOverrides? overrides = null)
    {
        _overrides = overrides;
        _defaults = Normalize(null, config.Features);
        _current = new FeatureSnapshot(_defaults, FeatureSources.Defaults, clock.GetUtcNow().UtcDateTime);
    }

    public FeatureSnapshot Current => _current;

    /// <summary>
    /// The switch for the clinic of this request: the clinic set with <see cref="UseClinic"/> (staff and doctor tokens, or the clinic
    /// of the slot or doctor being read), else the only clinic of a single-clinic deployment, else the deployment-wide value.
    /// </summary>
    public bool IsEnabled(string key)
    {
        if (_overrides is not null && (_clinic.Value ?? _overrides.SoleClinicId) is { } clinicId && _overrides.Get(clinicId, key) is { } over)
        {
            return over;
        }

        return IsGlobalEnabled(key);
    }

    private bool IsGlobalEnabled(string key) => _current.Features.TryGetValue(key, out var on) && on;

    /// <summary>The platform admin's per-clinic override wins over the deployment-wide value.</summary>
    public bool IsEnabled(string key, Guid clinicId) => _overrides?.Get(clinicId, key) ?? IsGlobalEnabled(key);

    /// <summary>Makes every <see cref="IsEnabled(string)"/> call in this async flow answer for one clinic until the scope is disposed.</summary>
    public IDisposable UseClinic(Guid? clinicId)
    {
        var previous = _clinic.Value;
        _clinic.Value = clinicId;
        return new Scope(() => _clinic.Value = previous);
    }

    /// <summary>All switches as one clinic sees them (the override wins); with no clinic, the only clinic of a single-clinic deployment.</summary>
    public IReadOnlyDictionary<string, bool> EffectiveFor(Guid? clinicId)
    {
        var id = clinicId ?? _clinic.Value ?? _overrides?.SoleClinicId;
        return FeatureKeys.All.ToDictionary(k => k, k => id is { } c ? IsEnabled(k, c) : IsGlobalEnabled(k));
    }

    private sealed class Scope(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }

    /// <summary>Known keys only; keys missing from <paramref name="incoming"/> take the clinic.config.json default.</summary>
    public IReadOnlyDictionary<string, bool> Merge(IReadOnlyDictionary<string, bool>? incoming) => Normalize(incoming, _defaults);

    public void Apply(IReadOnlyDictionary<string, bool>? features, string source, DateTime updatedAt) =>
        _current = new FeatureSnapshot(Merge(features), source, updatedAt);

    /// <summary>Throws FEATURE_DISABLED (403) when the feature is off.</summary>
    public void Require(string key)
    {
        if (!IsEnabled(key))
        {
            throw ApiException.FeatureDisabled(key);
        }
    }

    /// <summary>Throws FEATURE_DISABLED (403) when the feature is off for this clinic.</summary>
    public void Require(string key, Guid clinicId)
    {
        if (!IsEnabled(key, clinicId))
        {
            throw ApiException.FeatureDisabled(key);
        }
    }

    private static IReadOnlyDictionary<string, bool> Normalize(IReadOnlyDictionary<string, bool>? incoming, IReadOnlyDictionary<string, bool> fallback)
    {
        var result = new Dictionary<string, bool>();
        foreach (var key in FeatureKeys.All)
        {
            result[key] = incoming is not null && incoming.TryGetValue(key, out var on) ? on : fallback.GetValueOrDefault(key);
        }

        return result;
    }
}
