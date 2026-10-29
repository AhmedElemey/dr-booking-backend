using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Features.Appointments;

public sealed class AppointmentMapper(Language lang)
{
    public static IQueryable<Appointment> WithDetails(IQueryable<Appointment> query) =>
        query.Include(a => a.Slot!).ThenInclude(s => s.Doctor!).ThenInclude(d => d.Clinic)
            .Include(a => a.Service)
            .Include(a => a.Patient);

    public AppointmentDto ToPatientDto(Appointment a)
    {
        var slot = a.Slot!;
        var doctor = slot.Doctor!;
        var clinic = doctor.Clinic!;
        var (local, zone) = Local(slot.StartsAt, clinic.Timezone);
        return new AppointmentDto(
            a.Id,
            a.Status,
            a.SlotId,
            slot.StartsAt,
            slot.EndsAt,
            local,
            zone,
            new DoctorRefDto(doctor.Id, doctor.Slug, lang.Pick(doctor.NameAr, doctor.NameEn), doctor.PhotoUrl),
            new ClinicRefDto(clinic.Id, lang.Pick(clinic.NameAr, clinic.NameEn), lang.Pick(clinic.AddressAr, clinic.AddressEn), clinic.Phone, clinic.Latitude, clinic.Longitude),
            ToService(a.Service),
            a.PatientNote,
            a.CancelledBy,
            a.CancelReason);
    }

    public DashboardAppointmentDto ToDashboardDto(Appointment a)
    {
        var slot = a.Slot!;
        var doctor = slot.Doctor!;
        var (local, zone) = Local(slot.StartsAt, doctor.Clinic!.Timezone);
        return new DashboardAppointmentDto(
            a.Id,
            a.Status,
            a.SlotId,
            slot.StartsAt,
            slot.EndsAt,
            local,
            zone,
            new DashboardDoctorRefDto(doctor.Id, lang.Pick(doctor.NameAr, doctor.NameEn)),
            new PatientRefDto(a.Patient!.Id, a.Patient.FullName, a.Patient.Phone),
            ToService(a.Service),
            a.PatientNote,
            a.CancelledBy,
            a.CancelReason);
    }

    private static (DateTimeOffset Local, string Zone) Local(DateTime utc, string timezone)
    {
        var zone = ClinicTime.FindZone(timezone);
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return (new DateTimeOffset(ClinicTime.ToLocal(utc, zone), offset), timezone);
    }

    private ServiceRefDto? ToService(Service? s) =>
        s is null ? null : new ServiceRefDto(s.Id, lang.Pick(s.NameAr, s.NameEn), s.DurationMinutes, s.PriceEgp);
}
