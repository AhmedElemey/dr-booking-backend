using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;

namespace Moonlight.Api.Features.Admin;

[ApiController]
[Route("api/v1/admin")]
[Authorize(Roles = Roles.PlatformAdmin)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class AdminController(AdminService admin) : ControllerBase
{
    /// <summary>List all clinics, paged.</summary>
    [HttpGet("clinics")]
    [ProducesResponseType(typeof(PagedResult<ClinicDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<PagedResult<ClinicDto>> ListClinics(int? page, int? pageSize, CancellationToken ct) =>
        admin.ListClinicsAsync(PageRequest.Create(page, pageSize), ct);

    /// <summary>Effective feature flags of one clinic: deployment defaults plus the super admin's overrides, and its deposit amount.</summary>
    [HttpGet("clinics/{id:guid}/features")]
    [ProducesResponseType(typeof(AdminClinicFeaturesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<AdminClinicFeaturesDto> GetFeatures(Guid id, CancellationToken ct) =>
        admin.GetFeaturesAsync(id, ct);

    /// <summary>Set or clear per-clinic feature overrides (a null flag value clears the override) and the clinic's fixed deposit amount.</summary>
    [HttpPut("clinics/{id:guid}/features")]
    [ProducesResponseType(typeof(AdminClinicFeaturesDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<AdminClinicFeaturesDto> UpdateFeatures(Guid id, UpdateClinicFeaturesDto request, CancellationToken ct) =>
        admin.UpdateFeaturesAsync(id, request, ct);

    /// <summary>Create a clinic. The timezone defaults to Africa/Cairo.</summary>
    [HttpPost("clinics")]
    [ProducesResponseType(typeof(ClinicDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateClinic(CreateClinicDto request, CancellationToken ct)
    {
        var created = await admin.CreateClinicAsync(request, ct);
        return Created($"/api/v1/admin/clinics/{created.Id}", created);
    }

    /// <summary>Hide or show a clinic (and its doctors) everywhere.</summary>
    [HttpPatch("clinics/{id:guid}/active")]
    [ProducesResponseType(typeof(ClinicDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<ClinicDto> SetClinicActive(Guid id, SetActiveDto request, CancellationToken ct) =>
        admin.SetClinicActiveAsync(id, request.IsActive, ct);

    /// <summary>Create a Doctor (also creates the unpublished doctor profile) or ClinicStaff account for a clinic. PHONE_IN_USE when the phone already belongs to a user.</summary>
    [HttpPost("clinics/{id:guid}/users")]
    [ProducesResponseType(typeof(AdminUserDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> CreateUser(Guid id, CreateUserDto request, CancellationToken ct)
    {
        var created = await admin.CreateUserAsync(id, request, ct);
        return Created($"/api/v1/admin/users/{created.Id}", created);
    }

    /// <summary>Deactivate or reactivate a user. Deactivation revokes the user's refresh tokens.</summary>
    [HttpPatch("users/{id:guid}/active")]
    [ProducesResponseType(typeof(AdminUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<AdminUserDto> SetUserActive(Guid id, SetActiveDto request, CancellationToken ct) =>
        admin.SetUserActiveAsync(id, request.IsActive, ct);

    /// <summary>Grant or revoke the clinic-admin flag on a clinic staff account. Only ClinicStaff accounts carry the flag.</summary>
    [HttpPatch("users/{id:guid}/admin")]
    [ProducesResponseType(typeof(AdminUserDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<AdminUserDto> SetStaffAdmin(Guid id, SetAdminDto request, CancellationToken ct) =>
        admin.SetStaffAdminAsync(id, request.IsAdmin, ct);
}
