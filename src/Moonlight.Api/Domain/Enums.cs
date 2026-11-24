namespace Moonlight.Api.Domain;

public enum UserRole
{
    Patient,
    Doctor,
    ClinicStaff,
    PlatformAdmin,
}

public enum SlotStatus
{
    Open,
    Reserved,
    Blocked,
}

public enum AppointmentStatus
{
    // A patient booking waits for the clinic to approve it before it is Confirmed.
    Pending,
    Confirmed,
    Cancelled,
    Completed,
    NoShow,
}

public enum CancelledBy
{
    Patient,
    Clinic,
}

public enum DevicePlatform
{
    Ios,
    Android,
}
