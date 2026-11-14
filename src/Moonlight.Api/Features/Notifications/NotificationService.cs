using System.Globalization;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Fcm;
using Moonlight.Api.Infrastructure.Sms;

namespace Moonlight.Api.Features.Notifications;

// Push (FCM) to every device token of the patient; SMS only when enabled (Sms:NotificationsEnabled or the smsNotifications
// feature) and the patient has no device token or pushes are off.
public sealed class NotificationService(
    MoonlightDbContext db,
    IPushSender push,
    ISmsSender sms,
    IOptions<SmsOptions> smsOptions,
    FeatureService features,
    ILogger<NotificationService> logger) : INotificationService
{
    public Task AppointmentConfirmedAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.Confirmed, ct);

    public Task AppointmentCancelledByClinicAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.CancelledByClinic, ct);

    public Task AppointmentReminderAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.Reminder, ct);

    private async Task SendAsync(Guid appointmentId, NotificationKind kind, CancellationToken ct)
    {
        try
        {
            var appointment = await db.Appointments.AsNoTracking()
                .Include(a => a.Slot!).ThenInclude(s => s.Doctor!).ThenInclude(d => d.Clinic)
                .Include(a => a.Patient)
                .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);
            if (appointment is null)
            {
                return;
            }

            var patient = appointment.Patient!;
            var message = Compose(appointment, patient.PreferredLanguage, kind);

            // pushReminders gates the confirmation and reminder pushes; a clinic cancellation is always pushed.
            var pushEnabled = kind == NotificationKind.CancelledByClinic || features.IsEnabled(FeatureKeys.PushReminders);
            var tokens = pushEnabled
                ? await db.DeviceTokens.Where(t => t.UserId == patient.Id).Select(t => t.FcmToken).ToListAsync(ct)
                : [];

            if (tokens.Count > 0)
            {
                var invalid = await push.SendAsync(tokens, message, ct);
                if (invalid.Count > 0)
                {
                    await db.DeviceTokens.Where(t => invalid.Contains(t.FcmToken)).ExecuteDeleteAsync(ct);
                }
            }
            else if (smsOptions.Value.NotificationsEnabled || features.IsEnabled(FeatureKeys.SmsNotifications))
            {
                await sms.SendAsync(patient.Phone, $"{message.Title}: {message.Body}", ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send {Kind} notification for appointment {AppointmentId}.", kind, appointmentId);
        }
    }

    private static PushMessage Compose(Appointment appointment, string language, NotificationKind kind)
    {
        var english = language == "en";
        var slot = appointment.Slot!;
        var doctor = slot.Doctor!;
        var clinic = doctor.Clinic!;
        var zone = ClinicTime.FindZone(clinic.Timezone);
        var when = ClinicTime.ToLocal(slot.StartsAt, zone).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        var doctorName = english ? doctor.NameEn : doctor.NameAr;
        var clinicName = english ? clinic.NameEn : clinic.NameAr;
        var address = english ? clinic.AddressEn : clinic.AddressAr;

        var (type, title, body) = kind switch
        {
            NotificationKind.Confirmed => (
                "appointment.confirmed",
                english ? "Appointment confirmed" : "تم تأكيد حجزك",
                english ? $"{doctorName} at {clinicName}, {when}" : $"{doctorName} - {clinicName}، {when}"),
            NotificationKind.CancelledByClinic => (
                "appointment.cancelled",
                english ? "Appointment cancelled by the clinic" : "ألغت العيادة حجزك",
                (english ? $"{doctorName}, {when}." : $"{doctorName}، {when}.")
                    + (string.IsNullOrWhiteSpace(appointment.CancelReason) ? "" : (english ? $" Reason: {appointment.CancelReason}." : $" السبب: {appointment.CancelReason}."))
                    + (english ? " Tap to book another time." : " اضغط لحجز موعد آخر.")),
            _ => (
                "appointment.reminder",
                english ? "Appointment reminder" : "تذكير بموعدك",
                english ? $"{doctorName} at {clinicName}, {when}. {address}" : $"{doctorName} - {clinicName}، {when}. {address}"),
        };

        return new PushMessage(
            title,
            body,
            new Dictionary<string, string> { ["type"] = type, ["appointmentId"] = appointment.Id.ToString() });
    }

    private enum NotificationKind
    {
        Confirmed,
        CancelledByClinic,
        Reminder,
    }
}
