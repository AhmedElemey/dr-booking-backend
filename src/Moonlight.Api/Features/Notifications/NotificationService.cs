using System.Globalization;

using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Notifications;

// Push (FCM) to every device token of the patient; SMS only when enabled (Sms:NotificationsEnabled or the smsNotifications
// feature) and the patient has no device token or pushes are off; WhatsApp too when whatsappReminders is on and the patient
// opted in. Every attempt lands in the outbox (see MessageDispatcher).
public sealed class NotificationService(
    MoonlightDbContext db,
    MessageDispatcher dispatcher,
    FeatureService features,
    ILogger<NotificationService> logger) : INotificationService
{
    public Task AppointmentConfirmedAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.Confirmed, ct);

    public Task AppointmentCancelledByClinicAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.CancelledByClinic, ct);

    public Task AppointmentReminderAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.Reminder, ct);

    public Task ReviewPromptAsync(Guid appointmentId, CancellationToken ct = default) =>
        SendAsync(appointmentId, NotificationKind.ReviewPrompt, ct);

    public async Task WaitingListSlotOpenedAsync(Guid waitingListEntryId, CancellationToken ct = default)
    {
        try
        {
            var entry = await db.WaitingListEntries.AsNoTracking()
                .Include(w => w.Patient)
                .Include(w => w.Doctor!).ThenInclude(d => d.Clinic)
                .FirstOrDefaultAsync(w => w.Id == waitingListEntryId, ct);
            if (entry?.Patient is null || entry.Doctor is null)
            {
                return;
            }

            var english = entry.Patient.PreferredLanguage == "en";
            var doctorName = english ? entry.Doctor.NameEn : entry.Doctor.NameAr;
            var date = entry.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var content = new OutboundContent(
                "waitlist.slot-opened",
                english ? "A slot opened" : "تم فتح موعد",
                english ? $"A slot opened with {doctorName} on {date}. Book now." : $"تم فتح موعد مع {doctorName} يوم {date}. احجز الآن.",
                new Dictionary<string, string>
                {
                    ["type"] = "waitlist.slot-opened",
                    ["waitingListEntryId"] = entry.Id.ToString(),
                    ["doctorId"] = entry.DoctorId.ToString(),
                    ["date"] = date,
                },
                [doctorName, date]);
            await dispatcher.SendAsync(entry.Patient, entry.Doctor.ClinicId, content, pushEnabled: true, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send the waiting-list notification for entry {EntryId}.", waitingListEntryId);
        }
    }

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
            var content = Compose(appointment, patient.PreferredLanguage, kind);

            // pushReminders gates the confirmation and reminder pushes; a clinic cancellation is always pushed.
            var pushEnabled = kind is NotificationKind.CancelledByClinic or NotificationKind.ReviewPrompt || features.IsEnabled(FeatureKeys.PushReminders);
            await dispatcher.SendAsync(patient, appointment.Slot!.ClinicId, content, pushEnabled, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send {Kind} notification for appointment {AppointmentId}.", kind, appointmentId);
        }
    }

    private static OutboundContent Compose(Appointment appointment, string language, NotificationKind kind)
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
        var video = appointment.Mode == AppointmentMode.Video
            ? (english ? " (video consultation)" : " (استشارة فيديو)")
            : "";

        var (type, title, body) = kind switch
        {
            NotificationKind.Confirmed => (
                "appointment.confirmed",
                english ? "Appointment confirmed" : "تم تأكيد حجزك",
                (english ? $"{doctorName} at {clinicName}, {when}" : $"{doctorName} - {clinicName}، {when}") + video),
            NotificationKind.CancelledByClinic => (
                "appointment.cancelled",
                english ? "Appointment cancelled by the clinic" : "ألغت العيادة حجزك",
                (english ? $"{doctorName}, {when}." : $"{doctorName}، {when}.")
                    + (string.IsNullOrWhiteSpace(appointment.CancelReason) ? "" : (english ? $" Reason: {appointment.CancelReason}." : $" السبب: {appointment.CancelReason}."))
                    + (english ? " Tap to book another time." : " اضغط لحجز موعد آخر.")),
            NotificationKind.ReviewPrompt => (
                "review.prompt",
                english ? "Rate your visit" : "قيّم زيارتك",
                english ? $"How was your visit with {doctorName}? Tap to leave a rating." : $"كيف كانت زيارتك مع {doctorName}؟ اضغط لإضافة تقييمك."),
            _ => (
                "appointment.reminder",
                english ? "Appointment reminder" : "تذكير بموعدك",
                (english ? $"{doctorName} at {clinicName}, {when}. {address}" : $"{doctorName} - {clinicName}، {when}. {address}") + video),
        };

        return new OutboundContent(
            type,
            title,
            body,
            new Dictionary<string, string> { ["type"] = type, ["appointmentId"] = appointment.Id.ToString() },
            [doctorName, clinicName, when]);
    }

    private enum NotificationKind
    {
        Confirmed,
        CancelledByClinic,
        Reminder,
        ReviewPrompt,
    }
}
