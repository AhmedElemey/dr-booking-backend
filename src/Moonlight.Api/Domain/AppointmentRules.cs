namespace Moonlight.Api.Domain;

// Pure transition guards for the appointment state machine (spec section 3).
public static class AppointmentRules
{
    public static bool IsActive(AppointmentStatus status) => status != AppointmentStatus.Cancelled;

    // Only a Confirmed appointment can move anywhere (reschedule, cancel, complete, no-show).
    public static bool CanLeave(AppointmentStatus status) => status == AppointmentStatus.Confirmed;

    // Patients may cancel or reschedule until PatientCancelWindowHours before the slot starts.
    public static bool IsInsidePatientWindow(DateTime now, DateTime slotStartsAt, TimeSpan window) =>
        now <= slotStartsAt - window;

    // Clinic-side cancel is only possible before the visit starts.
    public static bool CanClinicCancel(DateTime now, DateTime slotStartsAt) => now < slotStartsAt;

    // Complete / no-show are only possible once the visit has started.
    public static bool CanCloseOut(DateTime now, DateTime slotStartsAt) => now >= slotStartsAt;
}
