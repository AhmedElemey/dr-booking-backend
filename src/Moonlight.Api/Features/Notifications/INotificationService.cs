namespace Moonlight.Api.Features.Notifications;

// Sends patient notifications (spec section 6). Implementations must never throw: a failed notification never fails a request.
public interface INotificationService
{
    Task AppointmentConfirmedAsync(Guid appointmentId, CancellationToken ct = default);

    Task AppointmentCancelledByClinicAsync(Guid appointmentId, CancellationToken ct = default);

    Task AppointmentReminderAsync(Guid appointmentId, CancellationToken ct = default);
}
