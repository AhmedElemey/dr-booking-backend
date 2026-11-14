using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Api.Features.Appointments;

[ApiController]
[Route("api/v1/appointments")]
[Authorize(Roles = Roles.Patient)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class AppointmentsController(AppointmentService appointments) : ControllerBase
{
    /// <summary>Book an open slot. The appointment is Confirmed immediately and the slot becomes Reserved.</summary>
    /// <remarks>Errors: SLOT_TAKEN, SLOT_UNAVAILABLE (past / inside the lead time), BOOKING_LIMIT, 404 for a slot that is not visible.</remarks>
    [HttpPost]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Book(BookAppointmentDto request, CancellationToken ct)
    {
        var created = await appointments.PatientBookAsync(request, ct);
        return Created($"/api/v1/appointments/{created.Id}", created);
    }

    /// <summary>The caller's own appointments, <c>scope=upcoming</c> (default) or <c>past</c>, paged.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<AppointmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<PagedResult<AppointmentDto>> List(string? scope, int? page, int? pageSize, CancellationToken ct) =>
        appointments.PatientListAsync(scope, PageRequest.Create(page, pageSize), ct);

    /// <summary>One of the caller's own appointments.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<AppointmentDto> Get(Guid id, CancellationToken ct) => appointments.GetPatientAsync(id, ct);

    /// <summary>Cancel an appointment until the cancel window before it starts. Frees the slot immediately.</summary>
    [HttpPost("{id:guid}/cancel")]
    [RequireFeature(FeatureKeys.PatientSelfService)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<AppointmentDto> Cancel(Guid id, CancelAppointmentDto? request, CancellationToken ct) =>
        appointments.PatientCancelAsync(id, request?.Reason, ct);

    /// <summary>Move an appointment to another open slot of the same doctor (cancel window applies).</summary>
    [HttpPost("{id:guid}/reschedule")]
    [RequireFeature(FeatureKeys.PatientSelfService)]
    [ProducesResponseType(typeof(AppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<AppointmentDto> Reschedule(Guid id, RescheduleAppointmentDto request, CancellationToken ct) =>
        appointments.PatientRescheduleAsync(id, request, ct);
}
