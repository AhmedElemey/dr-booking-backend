using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Family;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Insurance;
using Moonlight.Api.Features.Promotions;
using Moonlight.Api.Features.Reviews;

namespace Moonlight.Api.Features.Appointments;

public sealed class AppointmentMapper(Language lang, FeatureService features, ClinicConfig config)
{
    public static IQueryable<Appointment> WithDetails(IQueryable<Appointment> query) =>
        query.Include(a => a.Slot!).ThenInclude(s => s.Doctor!).ThenInclude(d => d.Clinic)
            .Include(a => a.Service)
            .Include(a => a.Patient)
            .Include(a => a.FamilyMember)
            .Include(a => a.InsuranceProvider)
            .Include(a => a.Review)
            .Include(a => a.Payments);

    public AppointmentDto ToPatientDto(Appointment a, int? queuePosition = null)
    {
        var slot = a.Slot!;
        var doctor = slot.Doctor!;
        var clinic = doctor.Clinic!;
        var (local, zone) = Local(slot.StartsAt, clinic.Timezone);
        var price = PriceOf(a);
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
            a.CancelReason,
            a.Mode,
            price,
            a.DiscountEgp,
            Math.Max(0, price - a.DiscountEgp),
            PaymentStatusOf(a),
            DepositRequiredOf(a, price),
            DepositPaidOf(a),
            ToFamily(a.FamilyMember),
            ToInsurance(a),
            features.IsEnabled(FeatureKeys.Reviews) && a.Status == AppointmentStatus.Completed && a.Review is null,
            ToReview(a.Review),
            queuePosition);
    }

    public DashboardAppointmentDto ToDashboardDto(Appointment a)
    {
        var slot = a.Slot!;
        var doctor = slot.Doctor!;
        var (local, zone) = Local(slot.StartsAt, doctor.Clinic!.Timezone);
        var price = PriceOf(a);
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
            a.CancelReason,
            a.Mode,
            price,
            a.DiscountEgp,
            Math.Max(0, price - a.DiscountEgp),
            PaymentStatusOf(a),
            DepositRequiredOf(a, price),
            DepositPaidOf(a),
            ToFamily(a.FamilyMember),
            ToInsurance(a),
            ToReview(a.Review),
            a.CheckedInAt,
            a.StartedAt);
    }

    // The fixed deposit the clinic asks for at booking (0 when deposits are off), capped at the appointment total.
    private decimal DepositRequiredOf(Appointment a, decimal price) =>
        config.DepositAmountEgp > 0 ? Math.Min(config.DepositAmountEgp, Math.Max(0, price - a.DiscountEgp)) : 0m;

    private static decimal DepositPaidOf(Appointment a) =>
        a.Payments.Where(p => p.Kind == PaymentKind.Deposit && p.Status is PaymentStatus.Paid or PaymentStatus.Refunding).Sum(p => p.AmountEgp);

    // The price snapshot taken at booking; appointments that predate it fall back to the current service price / doctor fee.
    private static decimal PriceOf(Appointment a) => a.PriceEgp ?? CouponService.PriceOf(a.Slot!.Doctor!, a.Service) ?? 0m;

    public static AppointmentPaymentStatus PaymentStatusOf(Appointment a)
    {
        if (a.Payments.Any(p => p.Status is PaymentStatus.Paid or PaymentStatus.Refunding))
        {
            return AppointmentPaymentStatus.Paid;
        }

        if (a.Payments.Any(p => p.Status == PaymentStatus.Pending))
        {
            return AppointmentPaymentStatus.Pending;
        }

        return a.Payments.Any(p => p.Status == PaymentStatus.Refunded) ? AppointmentPaymentStatus.Refunded : AppointmentPaymentStatus.Unpaid;
    }

    private static FamilyMemberRefDto? ToFamily(FamilyMember? m) => m is null ? null : new FamilyMemberRefDto(m.Id, m.FullName, m.Relation);

    private static ReviewRefDto? ToReview(Review? r) => r is null ? null : new ReviewRefDto(r.Rating, r.Comment);

    private InsuranceRefDto? ToInsurance(Appointment a) =>
        a.InsuranceProvider is null
            ? null
            : new InsuranceRefDto(
                new InsuranceProviderRefDto(a.InsuranceProvider.Id, lang.Pick(a.InsuranceProvider.NameAr, a.InsuranceProvider.NameEn)),
                a.InsuranceMemberNumber,
                a.InsuranceStatus ?? InsuranceStatus.Pending,
                a.InsuranceNote);

    private static (DateTimeOffset Local, string Zone) Local(DateTime utc, string timezone)
    {
        var zone = ClinicTime.FindZone(timezone);
        var offset = zone.GetUtcOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
        return (new DateTimeOffset(ClinicTime.ToLocal(utc, zone), offset), timezone);
    }

    private ServiceRefDto? ToService(Service? s) =>
        s is null ? null : new ServiceRefDto(s.Id, lang.Pick(s.NameAr, s.NameEn), s.DurationMinutes, s.PriceEgp);
}
