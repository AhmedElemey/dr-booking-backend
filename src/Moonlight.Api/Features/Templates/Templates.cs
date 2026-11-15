using System.Globalization;
using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Templates;

public sealed record TemplateRuleDto(DayOfWeek? DayOfWeek, string? Start, string? End);

public sealed record WorkingHoursTemplateDto(Guid Id, Guid ClinicId, string Name, int SlotMinutes, List<TemplateRuleDto> Rules);

public sealed record TemplateInputDto(string? Name, int? SlotMinutes, List<TemplateRuleDto>? Rules);

public sealed record ApplyTemplateDto(Guid? TemplateId, DateOnly? FromDate, DateOnly? ToDate);

public sealed class TemplateService(MoonlightDbContext db, DashboardScope scope)
{
    private static readonly string[] TimeFormats = ["HH:mm"];

    public async Task<List<WorkingHoursTemplateDto>> ListAsync(CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var rows = await db.WorkingHoursTemplates.AsNoTracking().Where(t => t.ClinicId == clinicId).OrderBy(t => t.Name).ThenBy(t => t.Id).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<WorkingHoursTemplateDto> CreateAsync(TemplateInputDto request, CancellationToken ct)
    {
        var rules = Validate(request);
        var template = new WorkingHoursTemplate
        {
            ClinicId = scope.ClinicId,
            Name = request.Name!.Trim(),
            SlotMinutes = request.SlotMinutes!.Value,
            Rules = rules,
        };
        db.WorkingHoursTemplates.Add(template);
        await db.SaveChangesAsync(ct);
        return ToDto(template);
    }

    public async Task<WorkingHoursTemplateDto> UpdateAsync(Guid id, TemplateInputDto request, CancellationToken ct)
    {
        var rules = Validate(request);
        var clinicId = scope.ClinicId;
        var template = await db.WorkingHoursTemplates.FirstOrDefaultAsync(t => t.Id == id && t.ClinicId == clinicId, ct) ?? throw ApiException.NotFound();
        template.Name = request.Name!.Trim();
        template.SlotMinutes = request.SlotMinutes!.Value;
        template.Rules = rules;
        await db.SaveChangesAsync(ct);
        return ToDto(template);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var deleted = await db.WorkingHoursTemplates.Where(t => t.Id == id && t.ClinicId == clinicId).ExecuteDeleteAsync(ct);
        if (deleted == 0)
        {
            throw ApiException.NotFound();
        }
    }

    private static List<WorkingHoursRule> Validate(TemplateInputDto request)
    {
        var errors = new ValidationErrors()
            .RequireText(request.Name, "name", 200)
            .Require(
                request.SlotMinutes is { } m && m is >= Slot.MinLengthMinutes and <= Slot.MaxLengthMinutes && m % Slot.AlignmentMinutes == 0,
                "slotMinutes",
                $"slotMinutes must be a multiple of {Slot.AlignmentMinutes} between {Slot.MinLengthMinutes} and {Slot.MaxLengthMinutes}.")
            .Require(request.Rules is { Count: > 0 }, "rules", "Add at least one rule.");
        errors.ThrowIfAny();

        var rules = new List<WorkingHoursRule>();
        foreach (var (rule, index) in request.Rules!.Select((r, i) => (r, i)))
        {
            var field = $"rules[{index}]";
            var ok = true;
            if (rule.DayOfWeek is null)
            {
                errors.Add($"{field}.dayOfWeek", "dayOfWeek is required.");
                ok = false;
            }

            if (!TimeOnly.TryParseExact(rule.Start, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var start) || start.Minute % Slot.AlignmentMinutes != 0)
            {
                errors.Add($"{field}.start", $"start must be HH:mm on a {Slot.AlignmentMinutes}-minute boundary.");
                ok = false;
            }

            if (!TimeOnly.TryParseExact(rule.End, TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
            {
                errors.Add($"{field}.end", "end must be HH:mm.");
                ok = false;
            }

            if (ok && end <= start)
            {
                errors.Add($"{field}.end", "end must be after start.");
                ok = false;
            }

            if (ok)
            {
                rules.Add(new WorkingHoursRule { DayOfWeek = rule.DayOfWeek!.Value, Start = start.ToString("HH:mm", CultureInfo.InvariantCulture), End = end.ToString("HH:mm", CultureInfo.InvariantCulture) });
            }
        }

        foreach (var day in rules.GroupBy(r => r.DayOfWeek))
        {
            var sorted = day.OrderBy(r => r.Start, StringComparer.Ordinal).ToList();
            for (var i = 1; i < sorted.Count; i++)
            {
                if (string.CompareOrdinal(sorted[i].Start, sorted[i - 1].End) < 0)
                {
                    errors.Add("rules", $"The rules of {day.Key} overlap.");
                }
            }
        }

        errors.ThrowIfAny();
        return rules;
    }

    private static WorkingHoursTemplateDto ToDto(WorkingHoursTemplate t) =>
        new(t.Id, t.ClinicId, t.Name, t.SlotMinutes, t.Rules.Select(r => new TemplateRuleDto(r.DayOfWeek, r.Start, r.End)).ToList());
}

[ApiController]
[Route("api/v1/dashboard/working-hours-templates")]
[Authorize(Roles = Roles.ClinicStaff)]
[RequireFeature(FeatureKeys.WorkingHoursTemplates)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class WorkingHoursTemplatesController(TemplateService templates) : ControllerBase
{
    /// <summary>Working-hours templates of the clinic.</summary>
    [HttpGet]
    [Authorize(Roles = Roles.Dashboard)]
    [ProducesResponseType(typeof(List<WorkingHoursTemplateDto>), StatusCodes.Status200OK)]
    public Task<List<WorkingHoursTemplateDto>> List(CancellationToken ct) => templates.ListAsync(ct);

    /// <summary>Create a weekly template: a slot length and rules <c>{dayOfWeek, start "HH:mm", end "HH:mm"}</c>.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(WorkingHoursTemplateDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(TemplateInputDto request, CancellationToken ct)
    {
        var created = await templates.CreateAsync(request, ct);
        return Created($"/api/v1/dashboard/working-hours-templates/{created.Id}", created);
    }

    /// <summary>Replace a template.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(WorkingHoursTemplateDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<WorkingHoursTemplateDto> Update(Guid id, TemplateInputDto request, CancellationToken ct) => templates.UpdateAsync(id, request, ct);

    /// <summary>Delete a template (slots already created from it stay).</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await templates.DeleteAsync(id, ct);
        return NoContent();
    }
}

[ApiController]
[Route("api/v1/dashboard/doctors/{id:guid}/slots/apply-template")]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.WorkingHoursTemplates)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class ApplyTemplateController(SlotService slots) : ControllerBase
{
    /// <summary>Expand a template over a date range (max 92 days). Past, overlapping and holiday slots are skipped and reported (reasons Past, Overlap, holiday).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(BulkSlotsResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<BulkSlotsResultDto> Apply(Guid id, ApplyTemplateDto request, CancellationToken ct) => slots.ApplyTemplateAsync(id, request, ct);
}
