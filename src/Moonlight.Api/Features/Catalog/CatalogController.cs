using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Api.Features.Catalog;

[ApiController]
[Route("api/v1")]
[AllowAnonymous]
[Produces(MediaTypeNames.Application.Json)]
public class CatalogController(CatalogService catalog) : ControllerBase
{
    /// <summary>List specialties (localized by Accept-Language).</summary>
    [HttpGet("specialties")]
    [ProducesResponseType(typeof(List<SpecialtyDto>), StatusCodes.Status200OK)]
    public Task<List<SpecialtyDto>> Specialties(CancellationToken ct) => catalog.SpecialtiesAsync(ct);

    /// <summary>Distinct cities of active clinics that have published doctors.</summary>
    [HttpGet("cities")]
    [ProducesResponseType(typeof(List<string>), StatusCodes.Status200OK)]
    public Task<List<string>> Cities(CancellationToken ct) => catalog.CitiesAsync(ct);

    /// <summary>Search published doctors by specialty, city, name and (insurance feature) accepted insurance provider. Needs sign-in when the guestBrowsing feature is off (401).</summary>
    [HttpGet("doctors")]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(PagedResult<DoctorSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<PagedResult<DoctorSummaryDto>> Doctors(
        Guid? specialtyId, string? city, string? q, Guid? insuranceProviderId, int? page, int? pageSize, CancellationToken ct) =>
        catalog.SearchDoctorsAsync(specialtyId, city, q, insuranceProviderId, PageRequest.Create(page, pageSize), ct);

    /// <summary>Public doctor profile with clinic info and active services. Accepts the doctor's id or slug.</summary>
    [HttpGet("doctors/{idOrSlug}")]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(DoctorProfileDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<DoctorProfileDto> Doctor(string idOrSlug, CancellationToken ct) => catalog.GetDoctorAsync(idOrSlug, ct);

    /// <summary>Open slots of a published doctor, grouped by clinic-local day. <c>from</c>/<c>to</c> are clinic-local dates, at most 14 days.</summary>
    [HttpGet("doctors/{idOrSlug}/slots")]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(DoctorSlotsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<DoctorSlotsDto> Slots(string idOrSlug, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        catalog.GetOpenSlotsAsync(idOrSlug, from, to, ct);
}
