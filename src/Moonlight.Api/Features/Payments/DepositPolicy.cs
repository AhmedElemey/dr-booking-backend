using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Features.Payments;

/// <summary>The fixed booking deposit a clinic asks for: the clinic's own amount, else the config default.</summary>
public static class DepositPolicy
{
    // A clinic amount of 0 means "no deposit" for that clinic; null falls back to the config default.
    public static decimal AmountFor(Clinic? clinic, ClinicConfig config) => clinic?.DepositAmountEgp ?? config.DepositAmountEgp;
}
