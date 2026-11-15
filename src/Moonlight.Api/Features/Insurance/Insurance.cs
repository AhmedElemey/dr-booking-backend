using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Insurance;

public sealed record InsuranceProviderRefDto(Guid Id, string Name);

public sealed record InsuranceBookingDto(Guid? ProviderId, string? MemberNumber);

public sealed record InsuranceRefDto(InsuranceProviderRefDto Provider, string? MemberNumber, InsuranceStatus Status, string? Note);

public sealed record InsuranceProviderDto(Guid Id, string NameAr, string NameEn, bool IsActive);

public sealed record InsuranceProviderInputDto(string? NameAr, string? NameEn, bool? IsActive);

public sealed record DoctorInsuranceInputDto(List<Guid>? ProviderIds);

public sealed record InsuranceDecisionDto(InsuranceStatus? Status, string? Note);

public sealed class InsuranceService(MoonlightDbContext db, DashboardScope scope, AppointmentMapper mapper, Language lang)
{
    public async Task<List<InsuranceProviderRefDto>> PublicListAsync(CancellationToken ct)
    {
        var rows = await db.InsuranceProviders.AsNoTracking().Where(i => i.IsActive).ToListAsync(ct);
        return rows.Select(i => new InsuranceProviderRefDto(i.Id, lang.Pick(i.NameAr, i.NameEn))).OrderBy(i => i.Name, StringComparer.Ordinal).ThenBy(i => i.Id).ToList();
    }

    public async Task<List<InsuranceProviderDto>> ListAsync(CancellationToken ct) =>
        (await db.InsuranceProviders.AsNoTracking().OrderBy(i => i.NameEn).ThenBy(i => i.Id).ToListAsync(ct)).Select(ToDto).ToList();

    public async Task<InsuranceProviderDto> CreateAsync(InsuranceProviderInputDto request, CancellationToken ct)
    {
        Validate(request);
        var provider = new InsuranceProvider { NameAr = request.NameAr!.Trim(), NameEn = request.NameEn!.Trim(), IsActive = request.IsActive ?? true };
        db.InsuranceProviders.Add(provider);
        await db.SaveChangesAsync(ct);
        return ToDto(provider);
    }

    public async Task<InsuranceProviderDto> UpdateAsync(Guid id, InsuranceProviderInputDto request, CancellationToken ct)
    {
        Validate(request);
        var provider = await db.InsuranceProviders.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound();
        provider.NameAr = request.NameAr!.Trim();
        provider.NameEn = request.NameEn!.Trim();
        provider.IsActive = request.IsActive ?? provider.IsActive;
        await db.SaveChangesAsync(ct);
        return ToDto(provider);
    }

    // Appointments may reference a provider, so it is deactivated rather than deleted once used.
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var provider = await db.InsuranceProviders.FirstOrDefaultAsync(i => i.Id == id, ct) ?? throw ApiException.NotFound();
        if (await db.Appointments.AnyAsync(a => a.InsuranceProviderId == id, ct))
        {
            provider.IsActive = false;
            await db.SaveChangesAsync(ct);
            return;
        }

        db.InsuranceProviders.Remove(provider);
        await db.SaveChangesAsync(ct);
    }

    public async Task<List<InsuranceProviderRefDto>> SetDoctorProvidersAsync(Guid doctorId, DoctorInsuranceInputDto request, CancellationToken ct)
    {
        new ValidationErrors().Require(request.ProviderIds is not null, "providerIds", "providerIds is required.").ThrowIfAny();
        await scope.RequireDoctorAsync(doctorId, ct);
        var ids = request.ProviderIds!.Distinct().ToList();
        var known = await db.InsuranceProviders.Where(i => ids.Contains(i.Id)).ToListAsync(ct);
        if (known.Count != ids.Count)
        {
            throw ApiException.Validation("providerIds", "Unknown insurance provider.");
        }

        var existing = await db.DoctorInsuranceProviders.Where(d => d.DoctorId == doctorId).ToListAsync(ct);
        db.DoctorInsuranceProviders.RemoveRange(existing.Where(e => !ids.Contains(e.InsuranceProviderId)));
        db.DoctorInsuranceProviders.AddRange(ids.Where(i => existing.All(e => e.InsuranceProviderId != i))
            .Select(i => new DoctorInsuranceProvider { DoctorId = doctorId, InsuranceProviderId = i }));
        await db.SaveChangesAsync(ct);
        return known.Select(i => new InsuranceProviderRefDto(i.Id, lang.Pick(i.NameAr, i.NameEn))).OrderBy(i => i.Name, StringComparer.Ordinal).ToList();
    }

    public async Task<DashboardAppointmentDto> DecideAsync(Guid appointmentId, InsuranceDecisionDto request, CancellationToken ct)
    {
        new ValidationErrors()
            .Require(request.Status is InsuranceStatus.Approved or InsuranceStatus.Rejected, "status", "status must be Approved or Rejected.")
            .RequireText(request.Note, "note", 500, required: false)
            .ThrowIfAny();
        var appointment = await scope.Appointments().FirstOrDefaultAsync(a => a.Id == appointmentId, ct) ?? throw ApiException.NotFound();
        if (appointment.InsuranceProviderId is null)
        {
            throw new ApiException(ErrorCodes.InvalidTransition, "The appointment has no insurance claim.");
        }

        appointment.InsuranceStatus = request.Status;
        appointment.InsuranceNote = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        await db.SaveChangesAsync(ct);
        var reloaded = await AppointmentMapper.WithDetails(db.Appointments.AsNoTracking()).FirstAsync(a => a.Id == appointmentId, ct);
        return mapper.ToDashboardDto(reloaded);
    }

    // Doctor id -> accepted active providers (localized), for the public doctor DTOs.
    public async Task<Dictionary<Guid, List<InsuranceProviderRefDto>>> AcceptedByDoctorAsync(IReadOnlyCollection<Guid> doctorIds, CancellationToken ct)
    {
        var rows = await db.DoctorInsuranceProviders.AsNoTracking().Include(d => d.InsuranceProvider)
            .Where(d => doctorIds.Contains(d.DoctorId) && d.InsuranceProvider!.IsActive).ToListAsync(ct);
        return rows.GroupBy(r => r.DoctorId).ToDictionary(
            g => g.Key,
            g => g.Select(r => new InsuranceProviderRefDto(r.InsuranceProviderId, lang.Pick(r.InsuranceProvider!.NameAr, r.InsuranceProvider.NameEn)))
                .OrderBy(i => i.Name, StringComparer.Ordinal).ToList());
    }

    private static void Validate(InsuranceProviderInputDto request) =>
        new ValidationErrors().RequireText(request.NameAr, "nameAr", 200).RequireText(request.NameEn, "nameEn", 200).ThrowIfAny();

    private static InsuranceProviderDto ToDto(InsuranceProvider i) => new(i.Id, i.NameAr, i.NameEn, i.IsActive);
}

[ApiController]
[Route("api/v1/insurance-providers")]
[AllowAnonymous]
[RequireFeature(FeatureKeys.Insurance)]
[Produces(MediaTypeNames.Application.Json)]
public class PublicInsuranceController(InsuranceService insurance) : ControllerBase
{
    /// <summary>Active insurance providers, localized by Accept-Language. Needs sign-in when guestBrowsing is off.</summary>
    [HttpGet]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(List<InsuranceProviderRefDto>), StatusCodes.Status200OK)]
    public Task<List<InsuranceProviderRefDto>> List(CancellationToken ct) => insurance.PublicListAsync(ct);
}

[ApiController]
[Route("api/v1/dashboard/insurance-providers")]
[Authorize(Roles = Roles.ClinicStaff + "," + Roles.PlatformAdmin)]
[RequireFeature(FeatureKeys.Insurance)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardInsuranceProvidersController(InsuranceService insurance) : ControllerBase
{
    /// <summary>All providers, inactive ones included (the list is shared by every clinic of this deployment).</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<InsuranceProviderDto>), StatusCodes.Status200OK)]
    public Task<List<InsuranceProviderDto>> List(CancellationToken ct) => insurance.ListAsync(ct);

    /// <summary>Add an insurance provider.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(InsuranceProviderDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(InsuranceProviderInputDto request, CancellationToken ct)
    {
        var created = await insurance.CreateAsync(request, ct);
        return Created($"/api/v1/dashboard/insurance-providers/{created.Id}", created);
    }

    /// <summary>Rename or (de)activate an insurance provider.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(InsuranceProviderDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<InsuranceProviderDto> Update(Guid id, InsuranceProviderInputDto request, CancellationToken ct) => insurance.UpdateAsync(id, request, ct);

    /// <summary>Delete a provider; one that appointments already reference is deactivated instead.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await insurance.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.Insurance)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardInsuranceClaimsController(InsuranceService insurance) : ControllerBase
{
    /// <summary>Replace the providers a doctor accepts (staff, or the doctor themselves).</summary>
    [HttpPut("api/v1/dashboard/doctors/{id:guid}/insurance-providers")]
    [ProducesResponseType(typeof(List<InsuranceProviderRefDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<List<InsuranceProviderRefDto>> SetDoctorProviders(Guid id, DoctorInsuranceInputDto request, CancellationToken ct) =>
        insurance.SetDoctorProvidersAsync(id, request, ct);

    /// <summary>Approve or reject the insurance claim of an appointment.</summary>
    [HttpPost("api/v1/dashboard/appointments/{id:guid}/insurance")]
    [ProducesResponseType(typeof(DashboardAppointmentDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<DashboardAppointmentDto> Decide(Guid id, InsuranceDecisionDto request, CancellationToken ct) => insurance.DecideAsync(id, request, ct);
}
