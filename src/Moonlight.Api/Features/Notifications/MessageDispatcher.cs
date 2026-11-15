using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Fcm;
using Moonlight.Api.Infrastructure.Sms;
using Moonlight.Api.Infrastructure.WhatsApp;

namespace Moonlight.Api.Features.Notifications;

// A patient message, ready for any channel. Kind is a stable key (also the outbox kind and the WhatsApp template key);
// WhatsAppParameters are the body parameters of the approved WhatsApp template.
public sealed record OutboundContent(
    string Kind,
    string Title,
    string Body,
    IReadOnlyDictionary<string, string> Data,
    IReadOnlyList<string> WhatsAppParameters);

public enum DeliveryOutcome
{
    Sent,
    Failed,
    Skipped,
}

/// <summary>The outcome of a recall delivery and the channel actually used (Auto when nothing could be used).</summary>
public sealed record RecallDelivery(DeliveryOutcome Outcome, RecallChannel Channel);

// Delivers one message to a patient over push / SMS / WhatsApp and records every attempt in the outbox
// (outbound_messages). Never throws for a provider failure: the failure is the outbox row.
public sealed class MessageDispatcher(
    MoonlightDbContext db,
    IPushSender push,
    ISmsSender sms,
    IWhatsAppSender whatsApp,
    IOptions<SmsOptions> smsOptions,
    IOptions<WhatsAppOptions> whatsAppOptions,
    FeatureService features,
    TimeProvider clock,
    ILogger<MessageDispatcher> logger)
{
    // Message kind -> (key under WhatsApp:Templates, default template name).
    private static readonly Dictionary<string, (string Key, string Default)> Templates = new()
    {
        ["appointment.confirmed"] = ("confirmed", "appointment_confirmed"),
        ["appointment.cancelled"] = ("cancelled", "appointment_cancelled"),
        ["appointment.reminder"] = ("reminder", "appointment_reminder"),
        ["waitlist.slot-opened"] = ("waitlist", "waiting_list_slot_opened"),
        ["review.prompt"] = ("review", "review_prompt"),
        ["recall"] = ("recall", "recall"),
    };

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    /// <summary>Standard appointment delivery: push to every device, SMS when there is none and SMS is on, plus WhatsApp when enabled.</summary>
    public async Task SendAsync(User patient, Guid? clinicId, OutboundContent content, bool pushEnabled, CancellationToken ct)
    {
        var tokens = pushEnabled
            ? await db.DeviceTokens.Where(t => t.UserId == patient.Id).Select(t => t.FcmToken).ToListAsync(ct)
            : [];

        if (tokens.Count > 0)
        {
            await PushAsync(patient, clinicId, tokens, content, ct);
        }
        else if (smsOptions.Value.NotificationsEnabled || features.IsEnabled(FeatureKeys.SmsNotifications))
        {
            await SmsAsync(patient, clinicId, content, ct);
        }

        if (WhatsAppAllowed(patient))
        {
            await WhatsAppAsync(patient, clinicId, content, ct);
        }
    }

    /// <summary>Recall delivery honouring the campaign's channel. Skipped when the patient cannot be reached that way.</summary>
    public async Task<RecallDelivery> SendRecallAsync(User patient, Guid clinicId, OutboundContent content, RecallChannel channel, CancellationToken ct)
    {
        var whatsAppOk = WhatsAppAllowed(patient);
        if (channel is RecallChannel.WhatsApp or RecallChannel.Auto && whatsAppOk)
        {
            return new RecallDelivery(await WhatsAppAsync(patient, clinicId, content, ct), RecallChannel.WhatsApp);
        }

        if (channel == RecallChannel.WhatsApp)
        {
            return new RecallDelivery(DeliveryOutcome.Skipped, channel);
        }

        if (channel is RecallChannel.Push or RecallChannel.Auto)
        {
            var tokens = await db.DeviceTokens.Where(t => t.UserId == patient.Id).Select(t => t.FcmToken).ToListAsync(ct);
            if (tokens.Count > 0)
            {
                return new RecallDelivery(await PushAsync(patient, clinicId, tokens, content, ct), RecallChannel.Push);
            }
        }

        var smsAllowed = channel == RecallChannel.Sms
            || (channel == RecallChannel.Auto && (smsOptions.Value.NotificationsEnabled || features.IsEnabled(FeatureKeys.SmsNotifications)));
        return smsAllowed
            ? new RecallDelivery(await SmsAsync(patient, clinicId, content, ct), RecallChannel.Sms)
            : new RecallDelivery(DeliveryOutcome.Skipped, channel);
    }

    private bool WhatsAppAllowed(User patient) => features.IsEnabled(FeatureKeys.WhatsappReminders) && patient.WhatsAppOptIn;

    private async Task<DeliveryOutcome> PushAsync(User patient, Guid? clinicId, IReadOnlyList<string> tokens, OutboundContent content, CancellationToken ct)
    {
        var message = new PushMessage(content.Title, content.Body, content.Data);
        try
        {
            var invalid = await push.SendAsync(tokens, message, ct);
            if (invalid.Count > 0)
            {
                await db.DeviceTokens.Where(t => invalid.Contains(t.FcmToken)).ExecuteDeleteAsync(ct);
            }

            await LogAsync(MessageChannel.Push, $"{tokens.Count} device(s)", patient, clinicId, content, MessageStatus.Sent, null, null, ct);
            return DeliveryOutcome.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Push {Kind} to user {UserId} failed.", content.Kind, patient.Id);
            await LogAsync(MessageChannel.Push, $"{tokens.Count} device(s)", patient, clinicId, content, MessageStatus.Failed, null, ex.Message, ct);
            return DeliveryOutcome.Failed;
        }
    }

    private async Task<DeliveryOutcome> SmsAsync(User patient, Guid? clinicId, OutboundContent content, CancellationToken ct)
    {
        try
        {
            await sms.SendAsync(patient.Phone, $"{content.Title}: {content.Body}", ct);
            await LogAsync(MessageChannel.Sms, patient.Phone, patient, clinicId, content, MessageStatus.Sent, null, null, ct);
            return DeliveryOutcome.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "SMS {Kind} to user {UserId} failed.", content.Kind, patient.Id);
            await LogAsync(MessageChannel.Sms, patient.Phone, patient, clinicId, content, MessageStatus.Failed, null, ex.Message, ct);
            return DeliveryOutcome.Failed;
        }
    }

    private async Task<DeliveryOutcome> WhatsAppAsync(User patient, Guid? clinicId, OutboundContent content, CancellationToken ct)
    {
        var (key, fallback) = Templates.GetValueOrDefault(content.Kind, (content.Kind, content.Kind.Replace('.', '_').Replace('-', '_')));
        var template = whatsAppOptions.Value.Templates.TryGetValue(key, out var configured) && !string.IsNullOrWhiteSpace(configured) ? configured : fallback;
        try
        {
            var result = await whatsApp.SendAsync(patient.Phone, template, patient.PreferredLanguage, content.WhatsAppParameters, ct);
            await LogAsync(MessageChannel.WhatsApp, patient.Phone, patient, clinicId, content, MessageStatus.Sent, result.ProviderRef, null, ct);
            return DeliveryOutcome.Sent;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "WhatsApp {Kind} to user {UserId} failed.", content.Kind, patient.Id);
            await LogAsync(MessageChannel.WhatsApp, patient.Phone, patient, clinicId, content, MessageStatus.Failed, null, ex.Message, ct);
            return DeliveryOutcome.Failed;
        }
    }

    private async Task LogAsync(
        MessageChannel channel, string to, User patient, Guid? clinicId, OutboundContent content,
        MessageStatus status, string? providerRef, string? error, CancellationToken ct)
    {
        try
        {
            db.OutboundMessages.Add(new OutboundMessage
            {
                Channel = channel,
                To = to,
                UserId = patient.Id,
                ClinicId = clinicId,
                Kind = content.Kind,
                Body = Truncate($"{content.Title}: {content.Body}", 4000),
                Status = status,
                ProviderRef = providerRef,
                Error = error is null ? null : Truncate(error, 1000),
                CreatedAt = Now,
            });
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not write the outbox row for {Kind}.", content.Kind);
        }
    }

    private static string Truncate(string text, int max) => text.Length <= max ? text : text[..max];
}
