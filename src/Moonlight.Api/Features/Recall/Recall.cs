using System.Globalization;
using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Notifications;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Recall;

public sealed record RecallCampaignDto(
    Guid Id,
    Guid ClinicId,
    string Name,
    string MessageAr,
    string MessageEn,
    RecallChannel Channel,
    int MonthsSinceLastVisit,
    Guid? DoctorId,
    Guid? ServiceId,
    RecallMode Mode,
    bool IsActive,
    DateTime? LastRunAt,
    DateTime CreatedAt);

public sealed record RecallCampaignInputDto(
    string? Name,
    string? MessageAr,
    string? MessageEn,
    RecallChannel? Channel,
    int? MonthsSinceLastVisit,
    Guid? DoctorId,
    Guid? ServiceId,
    RecallMode? Mode,
    bool? IsActive);

public sealed record RecallSampleDto(string? Name, string Phone, DateTime LastVisitAt);

public sealed record RecallPreviewDto(int AudienceCount, IReadOnlyList<RecallSampleDto> Sample);

public sealed record RecallRunResultDto(int Sent, int Failed, int Skipped);

public sealed record RecallSendDto(Guid Id, Guid PatientUserId, string? PatientName, RecallChannel Channel, RecallSendStatus Status, DateTime SentAt);

// Recall campaigns: "we have not seen you in N months" messages to lapsed patients who opted in to marketing.
public sealed class RecallService(
    MoonlightDbContext db,
    DashboardScope scope,
    MessageDispatcher dispatcher,
    ClinicConfig config,
    IOptions<BackgroundJobsOptions> jobs,
    FeatureService features,
    TimeProvider clock,
    ILogger<RecallService> logger)
{
    public const int RepeatWindowDays = 30;
    public const int MaxAudience = 5000;

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private sealed record Candidate(User Patient, DateTime LastVisitAt, Doctor? LastDoctor);

    // ---- Dashboard CRUD --------------------------------------------------------------------------------------------

    public async Task<List<RecallCampaignDto>> ListAsync(CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var rows = await db.RecallCampaigns.AsNoTracking().Where(c => c.ClinicId == clinicId).OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<RecallCampaignDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, tracking: false, ct));

    public async Task<RecallCampaignDto> CreateAsync(RecallCampaignInputDto request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var campaign = new RecallCampaign { ClinicId = scope.ClinicId, Name = "", MessageAr = "", MessageEn = "", CreatedAt = Now };
        Apply(campaign, request);
        db.RecallCampaigns.Add(campaign);
        await db.SaveChangesAsync(ct);
        return ToDto(campaign);
    }

    public async Task<RecallCampaignDto> UpdateAsync(Guid id, RecallCampaignInputDto request, CancellationToken ct)
    {
        await ValidateAsync(request, ct);
        var campaign = await LoadAsync(id, tracking: true, ct);
        Apply(campaign, request);
        await db.SaveChangesAsync(ct);
        return ToDto(campaign);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var campaign = await LoadAsync(id, tracking: true, ct);
        db.RecallCampaigns.Remove(campaign);
        await db.SaveChangesAsync(ct);
    }

    public async Task<RecallPreviewDto> PreviewAsync(Guid id, CancellationToken ct)
    {
        var campaign = await LoadAsync(id, tracking: false, ct);
        var audience = await AudienceAsync(campaign, ct);
        return new RecallPreviewDto(
            audience.Count,
            audience.Take(10).Select(c => new RecallSampleDto(c.Patient.FullName, c.Patient.Phone, c.LastVisitAt)).ToList());
    }

    public async Task<RecallRunResultDto> SendNowAsync(Guid id, CancellationToken ct)
    {
        var campaign = await LoadAsync(id, tracking: false, ct);
        if (!campaign.IsActive)
        {
            throw new ApiException(ErrorCodes.InvalidTransition, "The campaign is switched off.");
        }

        return await RunAsync(campaign, ct);
    }

    public async Task<PagedResult<RecallSendDto>> SendsAsync(Guid id, PageRequest page, CancellationToken ct)
    {
        await LoadAsync(id, tracking: false, ct);
        return await db.RecallSends.AsNoTracking().Where(s => s.CampaignId == id)
            .Join(db.Users, s => s.PatientUserId, u => u.Id, (s, u) => new { s, u.FullName })
            .OrderByDescending(x => x.s.SentAt).ThenBy(x => x.s.Id)
            .ToPagedAsync(page, rows => Task.FromResult<IReadOnlyList<RecallSendDto>>(
                rows.Select(x => new RecallSendDto(x.s.Id, x.s.PatientUserId, x.FullName, x.s.Channel, x.s.Status, x.s.SentAt)).ToList()), ct);
    }

    // ---- Running -----------------------------------------------------------------------------------------------------

    /// <summary>Daily job: runs every active Recurring campaign whose clinic-local run hour has passed and that has not run today.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.RecallCampaigns))
        {
            return 0;
        }

        var now = Now;
        var campaigns = await db.RecallCampaigns.AsNoTracking()
            .Where(c => c.IsActive && c.Mode == RecallMode.Recurring)
            .Join(db.Clinics, c => c.ClinicId, k => k.Id, (c, k) => new { Campaign = c, k.Timezone })
            .ToListAsync(ct);

        var ran = 0;
        foreach (var item in campaigns)
        {
            var zone = ClinicTime.FindZone(item.Timezone);
            var today = ClinicTime.LocalDate(now, zone);
            if (!ClinicTime.TryLocalToUtc(today.ToDateTime(new TimeOnly(jobs.Value.RecallHour, 0)), zone, out var dueAt) || now < dueAt)
            {
                continue;
            }

            // Atomic claim: of several API instances only one runs the campaign for today.
            var claimed = await db.RecallCampaigns
                .Where(c => c.Id == item.Campaign.Id && (c.LastRunAt == null || c.LastRunAt < dueAt))
                .ExecuteUpdateAsync(u => u.SetProperty(c => c.LastRunAt, now), ct);
            if (claimed == 0)
            {
                continue;
            }

            try
            {
                await RunAsync(item.Campaign, ct);
                ran++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Recall campaign {CampaignId} failed.", item.Campaign.Id);
            }
        }

        return ran;
    }

    private async Task<RecallRunResultDto> RunAsync(RecallCampaign campaign, CancellationToken ct)
    {
        var clinic = await db.Clinics.AsNoTracking().FirstAsync(c => c.Id == campaign.ClinicId, ct);
        var audience = await AudienceAsync(campaign, ct);
        int sent = 0, failed = 0, skipped = 0;
        foreach (var candidate in audience)
        {
            var patient = candidate.Patient;
            var english = patient.PreferredLanguage == "en";
            var text = Render(english ? campaign.MessageEn : campaign.MessageAr, patient, candidate.LastDoctor, clinic, english);
            var content = new OutboundContent(
                "recall",
                english ? clinic.NameEn : clinic.NameAr,
                text,
                new Dictionary<string, string> { ["type"] = "recall", ["campaignId"] = campaign.Id.ToString() },
                [text]);
            var delivery = await dispatcher.SendRecallAsync(patient, clinic.Id, content, campaign.Channel, ct);
            switch (delivery.Outcome)
            {
                case DeliveryOutcome.Sent:
                    sent++;
                    break;
                case DeliveryOutcome.Failed:
                    failed++;
                    break;
                default:
                    skipped++;
                    break;
            }

            db.RecallSends.Add(new RecallSend
            {
                CampaignId = campaign.Id,
                PatientUserId = patient.Id,
                Channel = delivery.Channel,
                Status = delivery.Outcome switch
                {
                    DeliveryOutcome.Sent => RecallSendStatus.Sent,
                    DeliveryOutcome.Failed => RecallSendStatus.Failed,
                    _ => RecallSendStatus.Skipped,
                },
                SentAt = Now,
            });
        }

        var now = Now;
        await db.SaveChangesAsync(ct);
        await db.RecallCampaigns.Where(c => c.Id == campaign.Id).ExecuteUpdateAsync(u => u.SetProperty(c => c.LastRunAt, now), ct);
        return new RecallRunResultDto(sent, failed, skipped);
    }

    // Patients (marketing opt-in) whose last matching Completed visit is at least N months old, with nothing booked ahead,
    // and who were not messaged by this campaign in the last 30 days.
    private async Task<List<Candidate>> AudienceAsync(RecallCampaign campaign, CancellationToken ct)
    {
        var now = Now;
        var cutoff = now.AddMonths(-campaign.MonthsSinceLastVisit);
        var repeatSince = now.AddDays(-RepeatWindowDays);
        var clinicId = campaign.ClinicId;
        var doctorId = campaign.DoctorId;
        var serviceId = campaign.ServiceId;

        var completed = db.Appointments.AsNoTracking().Where(a =>
            a.Status == AppointmentStatus.Completed
            && a.Slot!.ClinicId == clinicId
            && (doctorId == null || a.Slot.DoctorId == doctorId)
            && (serviceId == null || a.ServiceId == serviceId));
        var lapsed = completed
            .GroupBy(a => a.PatientUserId)
            .Select(g => new { PatientId = g.Key, LastVisit = g.Max(a => a.Slot!.StartsAt) })
            .Where(x => x.LastVisit <= cutoff);

        var rows = await lapsed
            .Join(db.Users, l => l.PatientId, u => u.Id, (l, u) => new { l.LastVisit, User = u })
            .Where(x => x.User.Role == UserRole.Patient && x.User.IsActive && x.User.MarketingOptIn)
            .Where(x => !db.Appointments.Any(a => a.PatientUserId == x.User.Id && a.Status == AppointmentStatus.Confirmed && a.Slot!.ClinicId == clinicId && a.Slot.StartsAt > now))
            .Where(x => !db.RecallSends.Any(s => s.CampaignId == campaign.Id && s.PatientUserId == x.User.Id && s.Status != RecallSendStatus.Skipped && s.SentAt > repeatSince))
            .OrderBy(x => x.LastVisit).ThenBy(x => x.User.Id)
            .Take(MaxAudience)
            .ToListAsync(ct);

        var patientIds = rows.Select(r => r.User.Id).ToList();
        var lastDoctors = await completed.Where(a => patientIds.Contains(a.PatientUserId))
            .Select(a => new { a.PatientUserId, a.Slot!.StartsAt, a.Slot.Doctor })
            .ToListAsync(ct);
        var latest = lastDoctors.GroupBy(a => a.PatientUserId).ToDictionary(g => g.Key, g => g.OrderByDescending(a => a.StartsAt).First().Doctor);

        return rows.Select(r => new Candidate(r.User, r.LastVisit, latest.GetValueOrDefault(r.User.Id))).ToList();
    }

    private string Render(string template, User patient, Doctor? doctor, Clinic clinic, bool english)
    {
        var name = string.IsNullOrWhiteSpace(patient.FullName) ? (english ? "there" : "عزيزي المريض") : patient.FullName.Trim();
        var doctorName = doctor is null ? "" : english ? doctor.NameEn : doctor.NameAr;
        var clinicName = english ? clinic.NameEn : clinic.NameAr;
        return template
            .Replace("{name}", name, StringComparison.Ordinal)
            .Replace("{doctor}", doctorName, StringComparison.Ordinal)
            .Replace("{clinic}", clinicName, StringComparison.Ordinal)
            .Replace("{link}", $"https://{config.Website.Domain}", StringComparison.Ordinal);
    }

    // ---- Helpers -----------------------------------------------------------------------------------------------------

    private async Task<RecallCampaign> LoadAsync(Guid id, bool tracking, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var query = tracking ? db.RecallCampaigns : db.RecallCampaigns.AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.Id == id && c.ClinicId == clinicId, ct) ?? throw ApiException.NotFound();
    }

    private async Task ValidateAsync(RecallCampaignInputDto request, CancellationToken ct)
    {
        new ValidationErrors()
            .RequireText(request.Name, "name", 200)
            .RequireText(request.MessageAr, "messageAr", 1000)
            .RequireText(request.MessageEn, "messageEn", 1000)
            .Require(request.Channel is not null, "channel", "channel must be Auto, Sms, WhatsApp or Push.")
            .Require(request.MonthsSinceLastVisit is >= 1 and <= 60, "monthsSinceLastVisit", "monthsSinceLastVisit must be between 1 and 60.")
            .Require(request.Mode is not null, "mode", "mode must be OneOff or Recurring.")
            .ThrowIfAny();

        if (request.DoctorId is { } doctorId && !await scope.Doctors().AnyAsync(d => d.Id == doctorId, ct))
        {
            throw ApiException.Validation("doctorId", "The doctor does not exist in this clinic.");
        }

        if (request.ServiceId is { } serviceId)
        {
            var clinicId = scope.ClinicId;
            var ok = await db.Services.AnyAsync(s => s.Id == serviceId && db.Doctors.Any(d => d.Id == s.DoctorId && d.ClinicId == clinicId), ct);
            if (!ok)
            {
                throw ApiException.Validation("serviceId", "The service does not exist in this clinic.");
            }
        }
    }

    private static void Apply(RecallCampaign campaign, RecallCampaignInputDto request)
    {
        campaign.Name = request.Name!.Trim();
        campaign.MessageAr = request.MessageAr!.Trim();
        campaign.MessageEn = request.MessageEn!.Trim();
        campaign.Channel = request.Channel!.Value;
        campaign.MonthsSinceLastVisit = request.MonthsSinceLastVisit!.Value;
        campaign.DoctorId = request.DoctorId;
        campaign.ServiceId = request.ServiceId;
        campaign.Mode = request.Mode!.Value;
        campaign.IsActive = request.IsActive ?? campaign.IsActive;
    }

    private static RecallCampaignDto ToDto(RecallCampaign c) => new(
        c.Id, c.ClinicId, c.Name, c.MessageAr, c.MessageEn, c.Channel, c.MonthsSinceLastVisit, c.DoctorId, c.ServiceId, c.Mode, c.IsActive, c.LastRunAt, c.CreatedAt);
}

[ApiController]
[Route("api/v1/dashboard/recall-campaigns")]
[Authorize(Roles = Roles.ClinicStaff)]
[RequireFeature(FeatureKeys.RecallCampaigns)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class RecallCampaignsController(RecallService recall) : ControllerBase
{
    /// <summary>Recall campaigns of the clinic, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<RecallCampaignDto>), StatusCodes.Status200OK)]
    public Task<List<RecallCampaignDto>> List(CancellationToken ct) => recall.ListAsync(ct);

    /// <summary>One recall campaign.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(RecallCampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<RecallCampaignDto> Get(Guid id, CancellationToken ct) => recall.GetAsync(id, ct);

    /// <summary>Create a campaign. Messages may use the placeholders {name}, {doctor}, {clinic} and {link}.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(RecallCampaignDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(RecallCampaignInputDto request, CancellationToken ct)
    {
        var created = await recall.CreateAsync(request, ct);
        return Created($"/api/v1/dashboard/recall-campaigns/{created.Id}", created);
    }

    /// <summary>Replace a recall campaign.</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(RecallCampaignDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<RecallCampaignDto> Update(Guid id, RecallCampaignInputDto request, CancellationToken ct) => recall.UpdateAsync(id, request, ct);

    /// <summary>Delete a recall campaign and its send log.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await recall.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Who would receive the campaign right now: the audience size and a sample of ten.</summary>
    [HttpPost("{id:guid}/preview")]
    [ProducesResponseType(typeof(RecallPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<RecallPreviewDto> Preview(Guid id, CancellationToken ct) => recall.PreviewAsync(id, ct);

    /// <summary>Send to the current audience now (the only way a OneOff campaign runs; recurring ones also run daily).</summary>
    [HttpPost("{id:guid}/send-now")]
    [ProducesResponseType(typeof(RecallRunResultDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<RecallRunResultDto> SendNow(Guid id, CancellationToken ct) => recall.SendNowAsync(id, ct);

    /// <summary>Per-patient send log of the campaign, newest first.</summary>
    [HttpGet("{id:guid}/sends")]
    [ProducesResponseType(typeof(PagedResult<RecallSendDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<PagedResult<RecallSendDto>> Sends(Guid id, int? page, int? pageSize, CancellationToken ct) =>
        recall.SendsAsync(id, PageRequest.Create(page, pageSize), ct);
}
