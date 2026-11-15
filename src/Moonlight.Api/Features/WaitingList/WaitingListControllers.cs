using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;

namespace Moonlight.Api.Features.WaitingList;

[ApiController]
[Route("api/v1/waiting-list")]
[Authorize(Roles = Roles.Patient)]
[RequireFeature(FeatureKeys.WaitingList)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class WaitingListController(WaitingListService waitingList) : ControllerBase
{
    /// <summary>Ask to be notified when a slot of the doctor opens on that clinic-local day. ALREADY_WAITING for a duplicate active entry.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(JoinedWaitingListDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Join(JoinWaitingListDto request, CancellationToken ct)
    {
        var created = await waitingList.JoinAsync(request, ct);
        return Created($"/api/v1/waiting-list/{created.Id}", created);
    }

    /// <summary>My active entries and those that ended in the last 30 days.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<WaitingListEntryDto>), StatusCodes.Status200OK)]
    public Task<List<WaitingListEntryDto>> Mine(CancellationToken ct) => waitingList.MineAsync(ct);

    /// <summary>Leave the waiting list (status Cancelled).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await waitingList.CancelAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/v1/dashboard/waiting-list")]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.WaitingList)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardWaitingListController(WaitingListService waitingList) : ControllerBase
{
    /// <summary>Waiting-list entries in scope (doctor: own; staff: whole clinic), oldest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<DashboardWaitingListEntryDto>), StatusCodes.Status200OK)]
    public Task<List<DashboardWaitingListEntryDto>> List(Guid? doctorId, DateOnly? date, WaitingListStatus? status, CancellationToken ct) =>
        waitingList.DashboardListAsync(doctorId, date, status, ct);

    /// <summary>Notify one entry by hand ("a slot opened"). Marks it Notified.</summary>
    [HttpPost("{id:guid}/notify")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Notify(Guid id, CancellationToken ct)
    {
        await waitingList.NotifyAsync(id, ct);
        return Ok(new { notified = true });
    }
}
