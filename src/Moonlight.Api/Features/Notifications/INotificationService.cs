namespace Moonlight.Api.Features.Notifications;

// Sends patient notifications (spec section 6). Implementations must never throw: a failed notification never fails a request.
public interface INotificationService
{
    Task AppointmentConfirmedAsync(Guid appointmentId, CancellationToken ct = default);

    Task AppointmentReceivedAsync(Guid appointmentId, CancellationToken ct = default);

    Task AppointmentCancelledByClinicAsync(Guid appointmentId, CancellationToken ct = default);

    Task AppointmentReminderAsync(Guid appointmentId, CancellationToken ct = default);

    /// <summary>"A slot opened with Dr X on date. Book now." for one waiting-list entry.</summary>
    Task WaitingListSlotOpenedAsync(Guid waitingListEntryId, CancellationToken ct = default);

    /// <summary>"Rate your visit" after an appointment was completed.</summary>
    Task ReviewPromptAsync(Guid appointmentId, CancellationToken ct = default);
}
