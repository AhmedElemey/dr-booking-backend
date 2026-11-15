using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Recall;
using Moonlight.Api.Features.WaitingList;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Notifications;

// "Rate your visit": a Completed appointment is prompted once, Jobs:ReviewPromptDelayMinutes after it was completed.
// The claim (UPDATE ... review_prompt_sent_at) is atomic, so several API instances never double-send.
public sealed class ReviewPromptProcessor(
    MoonlightDbContext db,
    INotificationService notifications,
    FeatureService features,
    IOptions<BackgroundJobsOptions> jobs,
    TimeProvider clock,
    ILogger<ReviewPromptProcessor> logger)
{
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.Reviews))
        {
            return 0;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var dueBefore = now.AddMinutes(-jobs.Value.ReviewPromptDelayMinutes);
        var candidates = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Completed && a.ReviewPromptSentAt == null && a.CompletedAt != null && a.CompletedAt <= dueBefore)
            .Where(a => !db.Reviews.Any(r => r.AppointmentId == a.Id))
            .Select(a => a.Id)
            .Take(200)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var id in candidates)
        {
            var claimed = await db.Appointments.Where(a => a.Id == id && a.ReviewPromptSentAt == null)
                .ExecuteUpdateAsync(u => u.SetProperty(a => a.ReviewPromptSentAt, now), ct);
            if (claimed == 1)
            {
                await notifications.ReviewPromptAsync(id, ct);
                sent++;
            }
        }

        if (sent > 0)
        {
            logger.LogInformation("Sent {Count} review prompt(s).", sent);
        }

        return sent;
    }
}

// Everything that runs on the background tick besides reminders: waiting-list expiry, review prompts, recall campaigns.
public sealed class MaintenanceProcessor(
    WaitingListService waitingList,
    ReviewPromptProcessor reviewPrompts,
    RecallService recall,
    ILogger<MaintenanceProcessor> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await GuardAsync("waiting-list expiry", () => waitingList.ExpireAsync(ct));
        await GuardAsync("review prompts", () => reviewPrompts.RunOnceAsync(ct));
        await GuardAsync("recall campaigns", () => recall.RunDueAsync(ct));
    }

    // One failing job never stops the others.
    private async Task GuardAsync(string name, Func<Task> job)
    {
        try
        {
            await job();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Background job '{Job}' failed; will retry on the next tick.", name);
        }
    }
}
