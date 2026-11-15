using Microsoft.Extensions.Options;

using Moonlight.Api.Common;

namespace Moonlight.Api.Features.Notifications;

// Ticks every Reminders:IntervalMinutes (5 by default) and runs the reminder processor in a fresh scope.
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

// Waiting-list expiry, review prompts and recurring recall campaigns: a service of its own, so they keep running when
// reminders are switched off (Reminders:Enabled=false) or a reminder run fails. Switch off with Jobs:Enabled=false.
public sealed class MaintenanceBackgroundService(
    IServiceScopeFactory scopes,
    IOptions<RemindersOptions> options,
    TimeProvider clock,
    ILogger<MaintenanceBackgroundService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(options.Value.IntervalMinutes), clock);
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                await scope.ServiceProvider.GetRequiredService<MaintenanceProcessor>().RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Maintenance run failed; will retry on the next tick.");
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
