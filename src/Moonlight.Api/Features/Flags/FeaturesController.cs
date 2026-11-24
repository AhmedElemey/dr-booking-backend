using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Moonlight.Api.Features.Flags;

public sealed record FeaturesDto(IReadOnlyDictionary<string, bool> Features, string Source, DateTime UpdatedAt);

[ApiController]
[Route("api/v1/features")]
[AllowAnonymous]
[Produces(MediaTypeNames.Application.Json)]
public class FeaturesController(FeatureService features) : ControllerBase
{
    /// <summary>Feature switches in effect for this clinic. <c>source</c> is <c>console</c> (live), <c>cache</c> (last copy from the console, loaded after a restart) or <c>defaults</c> (clinic.config.json).</summary>
    /// <remarks>Pass <c>clinicId</c> to see the switches as that clinic has them (the platform admin's overrides apply). Without it a single-clinic deployment answers for its only clinic.</remarks>
    [HttpGet]
    [ProducesResponseType(typeof(FeaturesDto), StatusCodes.Status200OK)]
    public FeaturesDto Get(Guid? clinicId = null)
    {
        var current = features.Current;
        Response.Headers.CacheControl = "public, max-age=60";
        return new FeaturesDto(features.EffectiveFor(clinicId), current.Source, current.UpdatedAt);
    }
}
