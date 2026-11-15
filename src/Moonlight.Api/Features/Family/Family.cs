using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Family;

public sealed record FamilyMemberRefDto(Guid Id, string FullName, FamilyRelation Relation);

public sealed record FamilyMemberDto(Guid Id, string FullName, FamilyRelation Relation, DateOnly? DateOfBirth, Gender? Gender, DateTime CreatedAt);

public sealed record FamilyMemberInputDto(string? FullName, FamilyRelation? Relation, DateOnly? DateOfBirth, Gender? Gender);

public sealed class FamilyService(MoonlightDbContext db, CurrentUser current, TimeProvider clock)
{
    public async Task<List<FamilyMemberDto>> ListAsync(CancellationToken ct)
    {
        var owner = current.Id;
        var rows = await db.FamilyMembers.AsNoTracking().Where(f => f.OwnerUserId == owner && f.DeletedAt == null).OrderBy(f => f.CreatedAt).ThenBy(f => f.Id).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<FamilyMemberDto> CreateAsync(FamilyMemberInputDto request, CancellationToken ct)
    {
        Validate(request);
        var owner = current.Id;
        if (await db.FamilyMembers.CountAsync(f => f.OwnerUserId == owner && f.DeletedAt == null, ct) >= FamilyMember.MaxPerOwner)
        {
            throw ApiException.Unprocessable("fullName", $"You can add at most {FamilyMember.MaxPerOwner} family members.");
        }

        var member = new FamilyMember { OwnerUserId = owner, FullName = "", CreatedAt = clock.GetUtcNow().UtcDateTime };
        Apply(member, request);
        db.FamilyMembers.Add(member);
        await db.SaveChangesAsync(ct);
        return ToDto(member);
    }

    public async Task<FamilyMemberDto> UpdateAsync(Guid id, FamilyMemberInputDto request, CancellationToken ct)
    {
        Validate(request);
        var owner = current.Id;
        var member = await db.FamilyMembers.FirstOrDefaultAsync(f => f.Id == id && f.OwnerUserId == owner && f.DeletedAt == null, ct) ?? throw ApiException.NotFound();
        Apply(member, request);
        await db.SaveChangesAsync(ct);
        return ToDto(member);
    }

    // Soft delete: the clinic's records and the appointments keep pointing at the member; only the patient's own lists and booking forget them.
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var owner = current.Id;
        var now = clock.GetUtcNow().UtcDateTime;
        var deleted = await db.FamilyMembers.Where(f => f.Id == id && f.OwnerUserId == owner && f.DeletedAt == null)
            .ExecuteUpdateAsync(u => u.SetProperty(f => f.DeletedAt, now), ct);
        if (deleted == 0)
        {
            throw ApiException.NotFound();
        }

        await db.WaitingListEntries.Where(w => w.FamilyMemberId == id && (w.Status == WaitingListStatus.Waiting || w.Status == WaitingListStatus.Notified))
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WaitingListStatus.Cancelled), ct);
    }

    private void Validate(FamilyMemberInputDto request) =>
        new ValidationErrors()
            .RequireText(request.FullName, "fullName", 200)
            .Require(request.Relation is not null, "relation", "relation is required (Child, Spouse, Parent, Sibling, Other).")
            .Require(request.DateOfBirth is null || request.DateOfBirth <= DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), "dateOfBirth", "dateOfBirth cannot be in the future.")
            .ThrowIfAny();

    private static void Apply(FamilyMember member, FamilyMemberInputDto request)
    {
        member.FullName = request.FullName!.Trim();
        member.Relation = request.Relation!.Value;
        member.DateOfBirth = request.DateOfBirth;
        member.Gender = request.Gender;
    }

    private static FamilyMemberDto ToDto(FamilyMember f) => new(f.Id, f.FullName, f.Relation, f.DateOfBirth, f.Gender, f.CreatedAt);
}

[ApiController]
[Route("api/v1/me/family")]
[Authorize(Roles = Roles.Patient)]
[RequireFeature(FeatureKeys.FamilyAccounts)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class FamilyController(FamilyService family) : ControllerBase
{
    /// <summary>The caller's family members (people they can book for), oldest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<FamilyMemberDto>), StatusCodes.Status200OK)]
    public Task<List<FamilyMemberDto>> List(CancellationToken ct) => family.ListAsync(ct);

    /// <summary>Add a family member (at most 10 per account).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(FamilyMemberDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create(FamilyMemberInputDto request, CancellationToken ct)
    {
        var created = await family.CreateAsync(request, ct);
        return Created($"/api/v1/me/family/{created.Id}", created);
    }

    /// <summary>Replace a family member's details.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(FamilyMemberDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<FamilyMemberDto> Update(Guid id, FamilyMemberInputDto request, CancellationToken ct) => family.UpdateAsync(id, request, ct);

    /// <summary>Remove a family member from the account (a soft delete: clinic records and appointments keep pointing at them; they disappear from the list and from booking).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await family.DeleteAsync(id, ct);
        return NoContent();
    }
}
