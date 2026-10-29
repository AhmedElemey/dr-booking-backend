using FirebaseAdmin;
using FirebaseAdmin.Messaging;

using Google.Apis.Auth.OAuth2;

namespace Moonlight.Api.Infrastructure.Fcm;

public sealed class FcmPushSender : IPushSender
{
    private readonly FirebaseMessaging _messaging;
    private readonly ILogger<FcmPushSender> _logger;

    public FcmPushSender(string serviceAccountJson, ILogger<FcmPushSender> logger)
    {
        _logger = logger;
        var app = FirebaseApp.GetInstance("moonlight") ?? FirebaseApp.Create(
            new AppOptions { Credential = GoogleCredential.FromJson(serviceAccountJson) },
            "moonlight");
        _messaging = FirebaseMessaging.GetMessaging(app);
    }

    public async Task<IReadOnlyList<string>> SendAsync(IReadOnlyList<string> tokens, PushMessage message, CancellationToken ct = default)
    {
        var invalid = new List<string>();
        foreach (var token in tokens)
        {
            try
            {
                await _messaging.SendAsync(
                    new Message
                    {
                        Token = token,
                        Notification = new Notification { Title = message.Title, Body = message.Body },
                        Data = message.Data.ToDictionary(kv => kv.Key, kv => kv.Value),
                    },
                    ct);
            }
            catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
            {
                _logger.LogInformation("FCM token rejected ({Code}); it will be removed.", ex.MessagingErrorCode);
                invalid.Add(token);
            }
        }

        return invalid;
    }
}
