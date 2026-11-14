namespace Moonlight.Api.Infrastructure.Sms;

public interface ISmsSender
{
    Task SendAsync(string phone, string message, CancellationToken ct = default);
}

// Development / staging stub: the message (including any OTP code) is written to the log.
public sealed class LoggingSmsSender(ILogger<LoggingSmsSender> logger) : ISmsSender
{
    public Task SendAsync(string phone, string message, CancellationToken ct = default)
    {
        logger.LogInformation("SMS (stub) to {Phone}: {Message}", phone, message);
        return Task.CompletedTask;
    }
}
