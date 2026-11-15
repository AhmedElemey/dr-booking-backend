using System.Globalization;
using System.Net.Mime;
using System.Text;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Features.Promotions;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Reports;

public enum ReportGroupBy
{
    Day,
    Week,
    Month,
}

public sealed record BookingTotalsDto(int Booked, int Completed, int Cancelled, int NoShow, double CancellationRate);

public sealed record BookingPeriodDto(string Period, int Booked, int Completed, int Cancelled, int NoShow);

public sealed record BookingByDoctorDto(Guid DoctorId, string Name, int Booked, int Completed);

public sealed record BookingByServiceDto(Guid? ServiceId, string Name, int Booked, int Completed);

public sealed record BookingBySourceDto(int Patient, int Staff);

public sealed record BookingsReportDto(
    BookingTotalsDto Totals,
    IReadOnlyList<BookingPeriodDto> Series,
    IReadOnlyList<BookingByDoctorDto> ByDoctor,
    IReadOnlyList<BookingByServiceDto> ByService,
    BookingBySourceDto BySource);

public sealed record RevenueTotalsDto(decimal ExpectedEgp, decimal CollectedEgp, decimal OnlineEgp, decimal CashEgp, decimal DiscountsEgp, decimal RefundsEgp);

public sealed record RevenuePeriodDto(string Period, decimal ExpectedEgp, decimal CollectedEgp);

public sealed record RevenueByDoctorDto(Guid DoctorId, string Name, decimal ExpectedEgp, decimal CollectedEgp);

public sealed record RevenueReportDto(RevenueTotalsDto Totals, IReadOnlyList<RevenuePeriodDto> Series, IReadOnlyList<RevenueByDoctorDto> ByDoctor);

// Reports over the caller's scope, by clinic-local date of the appointment (payments: of the payment / refund).
public sealed class ReportService(MoonlightDbContext db, DashboardScope scope, Language lang, TimeProvider clock)
{
    public const int MaxRangeDays = 366;

    public async Task<BookingsReportDto> BookingsAsync(DateOnly? from, DateOnly? to, Guid? doctorId, ReportGroupBy groupBy, CancellationToken ct)
    {
        var (zone, fromDate, toDate) = await ResolveRangeAsync(from, to, ct);
        var rows = await LoadAppointmentsAsync(zone, fromDate, toDate, doctorId, ct);

        var totals = new BookingTotalsDto(
            rows.Count,
            rows.Count(r => r.Status == AppointmentStatus.Completed),
            rows.Count(r => r.Status == AppointmentStatus.Cancelled),
            rows.Count(r => r.Status == AppointmentStatus.NoShow),
            rows.Count == 0 ? 0 : Math.Round((double)rows.Count(r => r.Status == AppointmentStatus.Cancelled) / rows.Count, 4));

        var series = Periods(fromDate, toDate, groupBy).Select(p =>
        {
            var inPeriod = rows.Where(r => PeriodOf(r.Date, groupBy) == p).ToList();
            return new BookingPeriodDto(
                p,
                inPeriod.Count,
                inPeriod.Count(r => r.Status == AppointmentStatus.Completed),
                inPeriod.Count(r => r.Status == AppointmentStatus.Cancelled),
                inPeriod.Count(r => r.Status == AppointmentStatus.NoShow));
        }).ToList();

        var byDoctor = rows.GroupBy(r => r.DoctorId)
            .Select(g => new BookingByDoctorDto(g.Key, g.First().DoctorName, g.Count(), g.Count(r => r.Status == AppointmentStatus.Completed)))
            .OrderByDescending(d => d.Booked).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
        var byService = rows.GroupBy(r => r.ServiceId)
            .Select(g => new BookingByServiceDto(g.Key, g.First().ServiceName, g.Count(), g.Count(r => r.Status == AppointmentStatus.Completed)))
            .OrderByDescending(d => d.Booked).ThenBy(d => d.Name, StringComparer.Ordinal).ToList();
        var bySource = new BookingBySourceDto(rows.Count(r => r.ByPatient), rows.Count(r => !r.ByPatient));
        return new BookingsReportDto(totals, series, byDoctor, byService, bySource);
    }

    public async Task<RevenueReportDto> RevenueAsync(DateOnly? from, DateOnly? to, Guid? doctorId, ReportGroupBy groupBy, CancellationToken ct)
    {
        var (zone, fromDate, toDate) = await ResolveRangeAsync(from, to, ct);
        var rows = await LoadAppointmentsAsync(zone, fromDate, toDate, doctorId, ct);
        var (start, end) = ClinicTime.DayRangeUtc(fromDate, toDate, zone);

        var appointmentIds = scope.Appointments().Select(a => a.Id);
        var paymentsQuery = db.Payments.AsNoTracking().Include(p => p.Appointment!).ThenInclude(a => a.Slot!).ThenInclude(s => s.Doctor)
            .Where(p => appointmentIds.Contains(p.AppointmentId));
        if (doctorId is { } d)
        {
            paymentsQuery = paymentsQuery.Where(p => p.Appointment!.Slot!.DoctorId == d);
        }

        var paid = await paymentsQuery.Where(p => (p.Status == PaymentStatus.Paid || p.Status == PaymentStatus.Refunded || p.Status == PaymentStatus.Refunding) && p.PaidAt >= start && p.PaidAt < end).ToListAsync(ct);
        var refunded = await paymentsQuery.Where(p => p.Status == PaymentStatus.Refunded && p.RefundedAt >= start && p.RefundedAt < end).ToListAsync(ct);

        var completed = rows.Where(r => r.Status == AppointmentStatus.Completed).ToList();
        var totals = new RevenueTotalsDto(
            completed.Sum(r => r.Total),
            paid.Sum(p => p.AmountEgp),
            paid.Where(p => p.Method == PaymentMethod.Online).Sum(p => p.AmountEgp),
            paid.Where(p => p.Method == PaymentMethod.Cash).Sum(p => p.AmountEgp),
            rows.Where(r => r.Status != AppointmentStatus.Cancelled).Sum(r => r.Discount),
            refunded.Sum(p => p.AmountEgp));

        var series = Periods(fromDate, toDate, groupBy).Select(p => new RevenuePeriodDto(
            p,
            completed.Where(r => PeriodOf(r.Date, groupBy) == p).Sum(r => r.Total),
            paid.Where(x => PeriodOf(ClinicTime.LocalDate(x.PaidAt!.Value, zone), groupBy) == p).Sum(x => x.AmountEgp))).ToList();

        var byDoctor = rows.Select(r => (DoctorId: r.DoctorId, DoctorName: r.DoctorName)).Concat(paid.Select(p => (DoctorId: p.Appointment!.Slot!.DoctorId, DoctorName: Name(p.Appointment.Slot.Doctor!)))).Distinct()
            .GroupBy(x => x.DoctorId).Select(g => g.First())
            .Select(x => new RevenueByDoctorDto(
                x.DoctorId,
                x.DoctorName,
                completed.Where(r => r.DoctorId == x.DoctorId).Sum(r => r.Total),
                paid.Where(p => p.Appointment!.Slot!.DoctorId == x.DoctorId).Sum(p => p.AmountEgp)))
            .OrderByDescending(x => x.CollectedEgp).ThenBy(x => x.Name, StringComparer.Ordinal).ToList();
        return new RevenueReportDto(totals, series, byDoctor);
    }

    // ---- CSV (UTF-8 with BOM so Excel shows Arabic) -------------------------------------------------------------------

    public static byte[] ToCsv(BookingsReportDto report)
    {
        var lines = new List<string> { "period,booked,completed,cancelled,noShow" };
        lines.AddRange(report.Series.Select(s => string.Join(',', Csv(s.Period), s.Booked, s.Completed, s.Cancelled, s.NoShow)));
        lines.Add(string.Join(',', "total", report.Totals.Booked, report.Totals.Completed, report.Totals.Cancelled, report.Totals.NoShow));
        return Encode(lines);
    }

    public static byte[] ToCsv(RevenueReportDto report)
    {
        var lines = new List<string> { "period,expectedEgp,collectedEgp" };
        lines.AddRange(report.Series.Select(s => string.Join(',', Csv(s.Period), Money(s.ExpectedEgp), Money(s.CollectedEgp))));
        lines.Add(string.Join(',', "total", Money(report.Totals.ExpectedEgp), Money(report.Totals.CollectedEgp)));
        return Encode(lines);
    }

    private static byte[] Encode(IEnumerable<string> lines) =>
        [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(string.Join("\r\n", lines) + "\r\n")];

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Csv(string value) => value.Contains(',') || value.Contains('"') ? $"\"{value.Replace("\"", "\"\"")}\"" : value;

    // ---- Helpers -------------------------------------------------------------------------------------------------------

    private sealed record Row(
        DateOnly Date,
        AppointmentStatus Status,
        Guid DoctorId,
        string DoctorName,
        Guid? ServiceId,
        string ServiceName,
        bool ByPatient,
        decimal Total,
        decimal Discount);

    private async Task<(TimeZoneInfo Zone, DateOnly From, DateOnly To)> ResolveRangeAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var zone = ClinicTime.FindZone((await scope.RequireClinicAsync(ct)).Timezone);
        var today = ClinicTime.LocalDate(clock.GetUtcNow().UtcDateTime, zone);
        var toDate = to ?? today;
        var fromDate = from ?? toDate.AddDays(-29);
        if (toDate < fromDate || toDate.DayNumber - fromDate.DayNumber + 1 > MaxRangeDays)
        {
            throw ApiException.Validation("to", $"The date range must be between 1 and {MaxRangeDays} days.");
        }

        return (zone, fromDate, toDate);
    }

    private async Task<List<Row>> LoadAppointmentsAsync(TimeZoneInfo zone, DateOnly from, DateOnly to, Guid? doctorId, CancellationToken ct)
    {
        var (start, end) = ClinicTime.DayRangeUtc(from, to, zone);
        var query = scope.Appointments().AsNoTracking()
            .Include(a => a.Slot!).ThenInclude(s => s.Doctor)
            .Include(a => a.Service)
            .Where(a => a.Slot!.StartsAt >= start && a.Slot.StartsAt < end);
        if (doctorId is { } d)
        {
            query = query.Where(a => a.Slot!.DoctorId == d);
        }

        var appointments = await query.ToListAsync(ct);
        return appointments.Select(a =>
        {
            var price = a.PriceEgp ?? CouponService.PriceOf(a.Slot!.Doctor!, a.Service) ?? 0m;
            return new Row(
                ClinicTime.LocalDate(a.Slot!.StartsAt, zone),
                a.Status,
                a.Slot.DoctorId,
                Name(a.Slot.Doctor!),
                a.ServiceId,
                a.Service is null ? lang.Pick("بدون خدمة", "No service") : lang.Pick(a.Service.NameAr, a.Service.NameEn),
                a.CreatedByUserId == a.PatientUserId,
                Math.Max(0, price - a.DiscountEgp),
                a.DiscountEgp);
        }).ToList();
    }

    private string Name(Doctor doctor) => lang.Pick(doctor.NameAr, doctor.NameEn);

    /// <summary>Day: yyyy-MM-dd. Week: the Monday that starts the week, yyyy-MM-dd. Month: yyyy-MM.</summary>
    public static string PeriodOf(DateOnly date, ReportGroupBy groupBy) => groupBy switch
    {
        ReportGroupBy.Week => date.AddDays(-(((int)date.DayOfWeek + 6) % 7)).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        ReportGroupBy.Month => date.ToString("yyyy-MM", CultureInfo.InvariantCulture),
        _ => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
    };

    // Every period of the range, so charts have no gaps.
    private static List<string> Periods(DateOnly from, DateOnly to, ReportGroupBy groupBy)
    {
        var keys = new List<string>();
        for (var date = from; date <= to; date = date.AddDays(1))
        {
            var key = PeriodOf(date, groupBy);
            if (keys.Count == 0 || keys[^1] != key)
            {
                keys.Add(key);
            }
        }

        return keys;
    }
}

[ApiController]
[Route("api/v1/dashboard/reports")]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.Reports)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class ReportsController(ReportService reports) : ControllerBase
{
    /// <summary>Booking counts for a clinic-local date range (default: the last 30 days, at most 366). <c>format=csv</c> downloads the series as UTF-8 CSV with a BOM.</summary>
    [HttpGet("bookings")]
    [ProducesResponseType(typeof(BookingsReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Bookings(DateOnly? from, DateOnly? to, Guid? doctorId, ReportGroupBy? groupBy, string? format, CancellationToken ct)
    {
        var report = await reports.BookingsAsync(from, to, doctorId, groupBy ?? ReportGroupBy.Day, ct);
        return IsCsv(format) ? File(ReportService.ToCsv(report), "text/csv; charset=utf-8", "bookings.csv") : Ok(report);
    }

    /// <summary>Revenue: expected (Completed appointments) vs collected (Paid payments, online + cash), refunds and discounts.</summary>
    [HttpGet("revenue")]
    [ProducesResponseType(typeof(RevenueReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Revenue(DateOnly? from, DateOnly? to, Guid? doctorId, ReportGroupBy? groupBy, string? format, CancellationToken ct)
    {
        var report = await reports.RevenueAsync(from, to, doctorId, groupBy ?? ReportGroupBy.Day, ct);
        return IsCsv(format) ? File(ReportService.ToCsv(report), "text/csv; charset=utf-8", "revenue.csv") : Ok(report);
    }

    private static bool IsCsv(string? format) => string.Equals(format, "csv", StringComparison.OrdinalIgnoreCase);
}
