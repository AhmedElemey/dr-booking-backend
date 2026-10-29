namespace Moonlight.Api.Infrastructure.Fcm;

public sealed record PushMessage(string Title, string Body, IReadOnlyDictionary<string, string> Data);

public interface IPushSender
{
    // Sends to every token and returns the tokens FCM reported as invalid / unregistered so the caller can delete them.
    Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default);
}

// Used when no FCM service account is configured (local development, tests).
public sealed class LoggingPushSender(ILogger<LoggingPushSender> logger) : IPushSender
{
    public Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
    {
        logger.LogInformation("Push (stub) to {Count} device(s): {Title} / {Body}", tokens.Count, message.Title, message.Body);
        return Task.FromResult<IReadOnlyList<string>>([]);
    }
}
