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

// A Pending booking the clinic never answered expires once its visit time arrives: it is cancelled (cancelledBy = Clinic),
// its coupon is released and the patient is told. The claim (UPDATE ... WHERE status = 'Pending') is atomic.
public sealed class PendingExpiryProcessor(
    MoonlightDbContext db,
    INotificationService notifications,
    TimeProvider clock,
    ILogger<PendingExpiryProcessor> logger)
{
    public const string ExpiredReason = "The clinic did not respond before the visit time.";

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var due = await db.Appointments.AsNoTracking()
            .Where(a => a.Status == AppointmentStatus.Pending && a.Slot!.StartsAt <= now)
            .Select(a => a.Id)
            .Take(200)
            .ToListAsync(ct);

        var expired = 0;
        foreach (var id in due)
        {
            var claimed = await db.Appointments.Where(a => a.Id == id && a.Status == AppointmentStatus.Pending)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.Status, AppointmentStatus.Cancelled)
                    .SetProperty(a => a.CancelledBy, (CancelledBy?)CancelledBy.Clinic)
                    .SetProperty(a => a.CancelReason, ExpiredReason)
                    .SetProperty(a => a.UpdatedAt, now), ct);
            if (claimed != 1)
            {
                continue;
            }

            var coupons = await db.CouponRedemptions.Where(r => r.AppointmentId == id).Select(r => r.CouponId).ToListAsync(ct);
            if (coupons.Count > 0)
            {
                await db.CouponRedemptions.Where(r => r.AppointmentId == id).ExecuteDeleteAsync(ct);
                foreach (var couponId in coupons)
                {
                    await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE coupons SET redemptions_count = GREATEST(redemptions_count - 1, 0) WHERE id = {couponId}", ct);
                }
            }

            await notifications.AppointmentCancelledByClinicAsync(id, ct);
            expired++;
        }

        if (expired > 0)
        {
            logger.LogInformation("Expired {Count} unanswered pending booking(s).", expired);
        }

        return expired;
    }
}

// Everything that runs on the background tick besides reminders: waiting-list expiry, review prompts, recall campaigns.
public sealed class MaintenanceProcessor(
    WaitingListService waitingList,
    ReviewPromptProcessor reviewPrompts,
    PendingExpiryProcessor pendingExpiry,
    RecallService recall,
    ILogger<MaintenanceProcessor> logger)
{
    public async Task RunOnceAsync(CancellationToken ct)
    {
        await GuardAsync("waiting-list expiry", () => waitingList.ExpireAsync(ct));
        await GuardAsync("pending expiry", () => pendingExpiry.RunOnceAsync(ct));
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
