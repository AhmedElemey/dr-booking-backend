using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Features.Dashboard;

[ApiController]
[Route("api/v1/dashboard/doctors")]
[Authorize(Roles = Roles.Dashboard)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardDoctorsController(DoctorManagementService doctors) : ControllerBase
{
    /// <summary>Doctors in scope: a doctor sees only themselves, staff see every doctor of the clinic (unpublished included).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ManagedDoctorDto>), StatusCodes.Status200OK)]
    public Task<List<ManagedDoctorDto>> List(CancellationToken ct) => doctors.ListAsync(ct);

    /// <summary>Full doctor profile (both languages).</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(ManagedDoctorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<ManagedDoctorDto> Get(Guid id, CancellationToken ct) => doctors.GetAsync(id, ct);

    /// <summary>Update profile fields, specialty and the published flag.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(ManagedDoctorDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<ManagedDoctorDto> Update(Guid id, UpdateDoctorDto request, CancellationToken ct) => doctors.UpdateAsync(id, request, ct);

    /// <summary>Upload the doctor's photo (multipart field <c>file</c>; jpeg, png or webp, at most 2 MB).</summary>
    [HttpPut("{id:guid}/photo")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(PhotoUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<PhotoUrlDto> UploadPhoto(Guid id, IFormFile? file, CancellationToken ct) =>
        new(await doctors.UploadPhotoAsync(id, file, $"{Request.Scheme}://{Request.Host}", ct));

    /// <summary>All of the doctor's services, inactive ones included.</summary>
    [HttpGet("{id:guid}/services")]
    [ProducesResponseType(typeof(List<ManagedServiceDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<List<ManagedServiceDto>> ListServices(Guid id, CancellationToken ct) => doctors.ListServicesAsync(id, ct);

    /// <summary>Create a service (treatment type) with a duration of 15-240 minutes.</summary>
    [HttpPost("{id:guid}/services")]
    [ProducesResponseType(typeof(ManagedServiceDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateService(Guid id, ServiceInputDto request, CancellationToken ct)
    {
        var created = await doctors.CreateServiceAsync(id, request, ct);
        return Created($"/api/v1/dashboard/doctors/{id}/services/{created.Id}", created);
    }

    /// <summary>Update a service, including <c>isActive</c>. Existing appointments are unaffected.</summary>
    [HttpPut("{id:guid}/services/{serviceId:guid}")]
    [ProducesResponseType(typeof(ManagedServiceDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<ManagedServiceDto> UpdateService(Guid id, Guid serviceId, ServiceInputDto request, CancellationToken ct) =>
        doctors.UpdateServiceAsync(id, serviceId, request, ct);

    /// <summary>Soft delete: sets <c>isActive = false</c>.</summary>
    [HttpDelete("{id:guid}/services/{serviceId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteService(Guid id, Guid serviceId, CancellationToken ct)
    {
        await doctors.DeactivateServiceAsync(id, serviceId, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/v1/dashboard/doctors/{id:guid}/slots")]
[Authorize(Roles = Roles.Dashboard)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
public class DashboardSlotsController(SlotService slots) : ControllerBase
{
    /// <summary>Slot calendar (<c>from</c>/<c>to</c> are clinic-local dates, at most 31 days). Reserved slots embed the appointment and patient.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<ManagedSlotDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<List<ManagedSlotDto>> List(Guid id, DateOnly? from, DateOnly? to, SlotStatus? status, CancellationToken ct) =>
        slots.ListAsync(id, from, to, status, ct);

    /// <summary>Create one open slot. SLOT_OVERLAP when it overlaps another slot of the doctor.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ManagedSlotDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid id, CreateSlotDto request, CancellationToken ct)
    {
        var created = await slots.CreateAsync(id, request, ct);
        return Created($"/api/v1/dashboard/doctors/{id}/slots/{created.Id}", created);
    }

    /// <summary>Repeat helper: expand a date range, weekdays and a time window into slot rows (max 92 days / 2000 slots). Past and overlapping slots are skipped and reported.</summary>
    [HttpPost("bulk")]
    [ProducesResponseType(typeof(BulkSlotsResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<BulkSlotsResultDto> Bulk(Guid id, BulkSlotsDto request, CancellationToken ct) => slots.BulkCreateAsync(id, request, ct);

    /// <summary>Block an open slot (hidden from patients). SLOT_RESERVED when it holds an appointment.</summary>
    [HttpPost("{slotId:guid}/block")]
    [ProducesResponseType(typeof(ManagedSlotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<ManagedSlotDto> Block(Guid id, Guid slotId, CancellationToken ct) => slots.BlockAsync(id, slotId, ct);

    /// <summary>Unblock a blocked slot that is still in the future.</summary>
    [HttpPost("{slotId:guid}/unblock")]
    [ProducesResponseType(typeof(ManagedSlotDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<ManagedSlotDto> Unblock(Guid id, Guid slotId, CancellationToken ct) => slots.UnblockAsync(id, slotId, ct);

    /// <summary>Remove an open or blocked slot. SLOT_RESERVED when it holds an appointment.</summary>
    [HttpDelete("{slotId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Delete(Guid id, Guid slotId, CancellationToken ct)
    {
        await slots.DeleteAsync(id, slotId, ct);
        return NoContent();
    }

    /// <summary>Remove all open/blocked future slots between two clinic-local dates (a day off). Reserved slots are kept and counted.</summary>
    [HttpDelete]
    [ProducesResponseType(typeof(DeleteSlotsResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<DeleteSlotsResultDto> DeleteRange(Guid id, DateOnly? from, DateOnly? to, CancellationToken ct) =>
        slots.DeleteRangeAsync(id, from, to, ct);
}

[ApiController]
[Route("api/v1/dashboard/clinic")]
[Authorize(Roles = Roles.Dashboard)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardClinicController(ClinicManagementService clinic) : ControllerBase
{
    /// <summary>The caller's clinic, including its timezone.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ClinicDto), StatusCodes.Status200OK)]
    public Task<ClinicDto> Get(CancellationToken ct) => clinic.GetAsync(ct);

    /// <summary>Update clinic name, address, city, phone and coordinates (staff only).</summary>
    [HttpPut]
    [Authorize(Roles = Roles.ClinicStaff)]
    [ProducesResponseType(typeof(ClinicDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<ClinicDto> Update(UpdateClinicDto request, CancellationToken ct) => clinic.UpdateAsync(request, ct);
}
