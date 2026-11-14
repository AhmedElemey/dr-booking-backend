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
    [HttpGet]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(FeaturesDto), StatusCodes.Status200OK)]
    public FeaturesDto Get()
    {
        var current = features.Current;
        return new FeaturesDto(current.Features, current.Source, current.UpdatedAt);
    }
}
