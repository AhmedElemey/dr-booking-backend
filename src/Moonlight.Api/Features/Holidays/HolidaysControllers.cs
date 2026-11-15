using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Api.Features.Holidays;

[ApiController]
[Route("api/v1/clinics/{clinicId:guid}/holidays")]
[AllowAnonymous]
[RequireFeature(FeatureKeys.HolidayCalendar)]
[Produces(MediaTypeNames.Application.Json)]
public class PublicHolidaysController(HolidayService holidays) : ControllerBase
{
    /// <summary>Closed dates of a clinic between two dates (default: the next 366 days), localized by Accept-Language. Needs sign-in when guestBrowsing is off.</summary>
    [HttpGet]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(List<PublicHolidayDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<List<PublicHolidayDto>> List(Guid clinicId, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        holidays.PublicListAsync(clinicId, from, to, ct);
}

[ApiController]
[Route("api/v1/dashboard/holidays")]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.HolidayCalendar)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardHolidaysController(HolidayService holidays) : ControllerBase
{
    /// <summary>Holidays of the clinic for a calendar year (default: the current year).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<HolidayDto>), StatusCodes.Status200OK)]
    public Task<List<HolidayDto>> List(Guid? clinicId, int? year, CancellationToken ct) => holidays.ListAsync(clinicId, year, ct);

    /// <summary>Add a closed day (staff only). Existing appointments on it are untouched; see the conflicts endpoint.</summary>
    [HttpPost]
    [Authorize(Roles = Roles.ClinicStaff)]
    [ProducesResponseType(typeof(HolidayDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CreateHolidayDto request, CancellationToken ct)
    {
        var created = await holidays.CreateAsync(request, ct);
        return Created($"/api/v1/dashboard/holidays/{created.Id}", created);
    }

    /// <summary>Remove a closed day (staff only).</summary>
    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.ClinicStaff)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await holidays.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Import the Egyptian public holidays of a year (existing dates are skipped). Moveable holidays are bundled estimates for 2026-2028.</summary>
    [HttpPost("import-egypt")]
    [Authorize(Roles = Roles.ClinicStaff)]
    [ProducesResponseType(typeof(ImportHolidaysResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<ImportHolidaysResultDto> Import(ImportHolidaysDto request, CancellationToken ct) => holidays.ImportEgyptAsync(request, ct);

    /// <summary>Confirmed appointments (in the caller's scope) that already fall on the holiday.</summary>
    [HttpGet("{id:guid}/conflicts")]
    [ProducesResponseType(typeof(List<DashboardAppointmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<List<DashboardAppointmentDto>> Conflicts(Guid id, CancellationToken ct) => holidays.ConflictsAsync(id, ct);
}
