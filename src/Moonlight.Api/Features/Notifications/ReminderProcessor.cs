using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Infrastructure;

using Npgsql;

namespace Moonlight.Api.Features.Notifications;

// Claims due reminders atomically (UPDATE ... RETURNING) and sends them, so each appointment is reminded at most once
// even when several API instances run.
public sealed class ReminderProcessor(MoonlightDbContext db, INotificationService notifications, TimeProvider clock, ILogger<ReminderProcessor> logger)
{
    private const string ClaimSql = """
        UPDATE appointments a
        SET reminder_sent_at = @now
        FROM slots s
        WHERE s.id = a.slot_id
          AND a.status = 'Confirmed'
          AND a.reminder_sent_at IS NULL
          AND s.starts_at BETWEEN @now + interval '2 hours' AND @now + interval '24 hours'
          AND a.created_at < s.starts_at - interval '24 hours'
        RETURNING a.id
        """;

    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var claimed = new List<Guid>();

        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            await using var command = new NpgsqlCommand(ClaimSql, connection);
            command.Parameters.AddWithValue("now", NpgsqlTypes.NpgsqlDbType.TimestampTz, now);
            await using var reader = await command.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                claimed.Add(reader.GetGuid(0));
            }
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }

        foreach (var id in claimed)
        {
            await notifications.AppointmentReminderAsync(id, ct);
        }

        if (claimed.Count > 0)
        {
            logger.LogInformation("Sent {Count} appointment reminder(s).", claimed.Count);
        }

        return claimed.Count;
    }
}
