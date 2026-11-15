using Microsoft.Extensions.Options;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Notifications;

// Ticks every Reminders:IntervalMinutes (5 by default) and runs the reminder processor and the maintenance jobs
// (waiting-list expiry, review prompts, recall campaigns) in a fresh scope.
public sealed class ReminderBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<RemindersOptions> options,
    TimeProvider clock,
    ILogger<ReminderBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes), clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<ReminderProcessor>().RunOnceAsync(stoppingToken);
                await scope.ServiceProvider.GetRequiredService<MaintenanceProcessor>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Reminder run failed; will retry on the next tick.");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
