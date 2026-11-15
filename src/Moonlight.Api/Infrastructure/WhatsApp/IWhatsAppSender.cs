using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

using Microsoft.Extensions.Options;

using Moonlight.Api.Common;

namespace Moonlight.Api.Infrastructure.WhatsApp;

public sealed record WhatsAppResult(string? ProviderRef);

public interface IWhatsAppSender
{
    /// <summary>Sends an approved template message. Throws when the provider rejects it; callers log the failure.</summary>
    Task<WhatsAppResult> SendAsync(string phone, string templateName, string language, IReadOnlyList<string> parameters, CancellationToken ct = default);
}

// Development stub: the message is only logged (the notification service also writes it to the outbox).
public sealed class MockWhatsAppSender(ILogger<MockWhatsAppSender> logger) : IWhatsAppSender
{
    public Task<WhatsAppResult> SendAsync(string phone, string templateName, string language, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        logger.LogInformation("WhatsApp (mock) to {Phone}: template {Template} [{Language}] ({Parameters})", phone, templateName, language, string.Join(" | ", parameters));
        return Task.FromResult(new WhatsAppResult("mock-" + Guid.NewGuid().ToString("N")));
    }
}

// WhatsApp Business Cloud API: POST {ApiBaseUrl}/{PhoneNumberId}/messages with a template message.
public sealed class MetaWhatsAppSender(HttpClient http, IOptions<WhatsAppOptions> options, ILogger<MetaWhatsAppSender> logger) : IWhatsAppSender
{
    public async Task<WhatsAppResult> SendAsync(string phone, string templateName, string language, IReadOnlyList<string> parameters, CancellationToken ct = default)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.PhoneNumberId) || string.IsNullOrWhiteSpace(settings.AccessToken))
        {
            throw new InvalidOperationException("WhatsApp:PhoneNumberId and WhatsApp:AccessToken must be configured for the Meta provider.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.ApiBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(settings.PhoneNumberId)}/messages")
        {
            Content = JsonContent.Create(BuildPayload(settings, phone, templateName, language, parameters)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.AccessToken);

        using var response = await http.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("WhatsApp send answered {Status}.", (int)response.StatusCode);
            throw new HttpRequestException($"WhatsApp Cloud API answered {(int)response.StatusCode}: {Truncate(text)}");
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            var id = document.RootElement.TryGetProperty("messages", out var messages) && messages.GetArrayLength() > 0
                ? messages[0].GetProperty("id").GetString()
                : null;
            return new WhatsAppResult(id);
        }
        catch (JsonException)
        {
            return new WhatsAppResult(null);
        }
    }

    // Meta wants the number in international format without the "+".
    public static object BuildPayload(WhatsAppOptions settings, string phone, string templateName, string language, IReadOnlyList<string> parameters) => new
    {
        messaging_product = "whatsapp",
        to = phone.TrimStart('+'),
        type = "template",
        template = new
        {
            name = templateName,
            language = new { code = language == "en" ? settings.LanguageCodeEn : settings.LanguageCodeAr },
            components = parameters.Count == 0
                ? Array.Empty<object>()
                : [new { type = "body", parameters = parameters.Select(p => new { type = "text", text = p }).ToArray() }],
        },
    };

    private static string Truncate(string text) => text.Length <= 300 ? text : text[..300];
}
