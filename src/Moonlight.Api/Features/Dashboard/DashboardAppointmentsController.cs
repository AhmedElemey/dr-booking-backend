using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Api.Features.Dashboard;

[ApiController]
[Route("api/v1/dashboard/appointments")]
[Authorize(Roles = Roles.Dashboard)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardAppointmentsController(AppointmentService appointments) : ControllerBase
{
    /// <summary>List appointments in scope (doctor: own; staff: whole clinic), ordered by slot start. <c>from</c>/<c>to</c> are clinic-local dates, inclusive.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DashboardAppointmentDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<PagedResult<DashboardAppointmentDto>> List(
        Guid? doctorId, AppointmentStatus? status, DateOnly? from, DateOnly? to, int? page, int? pageSize, CancellationToken ct) =>
        appointments.DashboardListAsync(doctorId, status, from, to, PageRequest.Create(page, pageSize), ct);

    /// <summary>Appointment detail including the patient's name and phone.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<DashboardAppointmentDto> Get(Guid id, CancellationToken ct) => appointments.GetDashboardAsync(id, ct);

    /// <summary>Book on behalf of a phone-in patient (found or created by phone). Exempt from patient booking limits and lead time.</summary>
    /// <remarks>PHONE_IN_USE when the phone belongs to a doctor or staff user.</remarks>
    [HttpPost]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Book(DashboardBookAppointmentDto request, CancellationToken ct)
    {
        var created = await appointments.DashboardBookAsync(request, ct);
        return Created($"/api/v1/dashboard/appointments/{created.Id}", created);
    }

    /// <summary>Move an appointment to another open slot of the same doctor.</summary>
    [HttpPost("{id:guid}/reschedule")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> Reschedule(Guid id, RescheduleAppointmentDto request, CancellationToken ct) =>
        appointments.DashboardRescheduleAsync(id, request, ct);

    /// <summary>Cancel an appointment before it starts (cancelledBy = Clinic). The patient is notified.</summary>
    [HttpPost("{id:guid}/cancel")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> Cancel(Guid id, CancelAppointmentDto? request, CancellationToken ct) =>
        appointments.DashboardCancelAsync(id, request?.Reason, ct);

    /// <summary>Today's live queue in scope, ordered: with the doctor, checked-in by arrival, then upcoming. Requires the liveQueue feature.</summary>
    [HttpGet("queue")]
    [RequireFeature(FeatureKeys.LiveQueue)]
    [ProducesResponseType(typeof(IReadOnlyList<DashboardQueueEntryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<IReadOnlyList<DashboardQueueEntryDto>> Queue(Guid? doctorId, CancellationToken ct) => appointments.QueueAsync(doctorId, ct);

    /// <summary>Check a patient in on the day of their visit (Confirmed to CheckedIn). Requires the liveQueue feature.</summary>
    [HttpPost("{id:guid}/check-in")]
    [RequireFeature(FeatureKeys.LiveQueue)]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> CheckIn(Guid id, CancellationToken ct) => appointments.CheckInAsync(id, ct);

    /// <summary>Start a checked-in visit (CheckedIn to InProgress). One patient per doctor at a time. Requires the liveQueue feature.</summary>
    [HttpPost("{id:guid}/start")]
    [RequireFeature(FeatureKeys.LiveQueue)]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> Start(Guid id, CancellationToken ct) => appointments.StartAsync(id, ct);

    /// <summary>Mark a started appointment Completed.</summary>
    [HttpPost("{id:guid}/complete")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> Complete(Guid id, CancellationToken ct) => appointments.CompleteAsync(id, ct);

    /// <summary>Mark a started appointment NoShow.</summary>
    [HttpPost("{id:guid}/no-show")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> NoShow(Guid id, CancellationToken ct) => appointments.NoShowAsync(id, ct);
}
