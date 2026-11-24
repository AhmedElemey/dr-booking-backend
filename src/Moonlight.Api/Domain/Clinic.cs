namespace Moonlight.Api.Domain;

public class Clinic
{
    public const string DefaultTimezone = "Africa/Cairo";

    public Guid Id { get; set; } = Guid.NewGuid();

    public required string NameAr { get; set; }

    public required string NameEn { get; set; }

    public required string AddressAr { get; set; }

    public required string AddressEn { get; set; }

    public required string City { get; set; }

    public double? Latitude { get; set; }

    public double? Longitude { get; set; }

    public required string Phone { get; set; }

    public string Timezone { get; set; } = DefaultTimezone;

    public bool IsActive { get; set; } = true;

    // Fixed booking deposit for this clinic, set by the platform admin. When null the deployment's
    // clinic.config.json amount applies.
    public decimal? DepositAmountEgp { get; set; }

    // When false, public catalog responses hide consultation and service prices.
    public bool ShowPrices { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}
