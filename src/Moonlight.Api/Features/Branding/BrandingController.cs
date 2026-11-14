using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Branding;

public sealed record BrandingColorsDto(string Primary, string Accent);

public sealed record BrandingDto(string Slug, LocalizedText Name, LocalizedText Tagline, BrandingColorsDto Colors, string SupportPhone, string LogoUrl);

[ApiController]
[Route("api/v1/branding")]
[AllowAnonymous]
public class BrandingController(ClinicConfig config) : ControllerBase
{
    public const string LogoRoute = "/api/v1/branding/logo";

    /// <summary>Clinic identity from clinic.config.json. <c>logoUrl</c> is a path on this API.</summary>
    [HttpGet]
    [Produces(MediaTypeNames.Application.Json)]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(BrandingDto), StatusCodes.Status200OK)]
    public BrandingDto Get() =>
        new(config.Slug, config.Name, config.Tagline, new BrandingColorsDto(config.Colors.Primary, config.Colors.Accent), config.SupportPhone, LogoRoute);

    /// <summary>The clinic logo file (SVG or PNG).</summary>
    [HttpGet("logo")]
    [ResponseCache(Duration = 60, Location = ResponseCacheLocation.Any)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    public IActionResult Logo()
    {
        var contentType = Path.GetExtension(config.LogoPath).ToLowerInvariant() switch
        {
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            _ => "application/octet-stream",
        };
        return PhysicalFile(config.LogoPath, contentType);
    }
}
