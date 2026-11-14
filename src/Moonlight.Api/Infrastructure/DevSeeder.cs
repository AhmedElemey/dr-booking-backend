using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure;

// Development-only demo data: a Cairo clinic with three published doctors, services, 14 days of open slots and
// one user per role. Idempotent: re-running only tops up the slots.
public static class DevSeeder
{
    public const string AdminPhone = "+201000000001";
    public const string StaffPhone = "+201000000002";
    public const string DoctorAmrPhone = "+201000000011";
    public const string DoctorSaraPhone = "+201000000012";
    public const string DoctorYoussefPhone = "+201000000013";
    public const int SlotDays = 14;

    public static async Task SeedAsync(MoonlightDbContext db, ClinicConfig config, TimeProvider clock, ILogger logger, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.NameEn == "Smile Care Clinic", ct);
        if (clinic is null)
        {
            clinic = new Clinic
            {
                NameAr = "عيادة سمايل كير",
                NameEn = "Smile Care Clinic",
                AddressAr = "١٢ ميدان التحرير، وسط البلد، القاهرة",
                AddressEn = "12 Tahrir Square, Downtown, Cairo",
                City = "Cairo",
                Latitude = 30.0444,
                Longitude = 31.2357,
                Phone = "+20225781234",
                CreatedAt = now,
            };
            db.Clinics.Add(clinic);
        }

        await EnsureUserAsync(db, AdminPhone, $"{config.Name.En} Admin", UserRole.PlatformAdmin, now, ct);
        var staff = await EnsureUserAsync(db, StaffPhone, "Mona Reception", UserRole.ClinicStaff, now, ct);
        if (!await db.ClinicStaff.AnyAsync(s => s.UserId == staff.Id, ct))
        {
            db.ClinicStaff.Add(new ClinicStaff { UserId = staff.Id, ClinicId = clinic.Id });
        }

        var doctors = new[]
        {
            await EnsureDoctorAsync(db, clinic, DoctorAmrPhone, "dr-amr-hassan", "د. عمرو حسن", "Dr. Amr Hassan", ReferenceData.GeneralDentistryId,
                "طبيب أسنان عام بخبرة في الحشوات وتنظيف الأسنان والفحص الدوري.",
                "General dentist focused on check-ups, cleaning and fillings.", 12, 300m, now, ct),
            await EnsureDoctorAsync(db, clinic, DoctorSaraPhone, "dr-sara-mahmoud", "د. سارة محمود", "Dr. Sara Mahmoud", ReferenceData.OrthodonticsId,
                "أخصائية تقويم الأسنان للكبار والصغار.",
                "Orthodontist treating teenagers and adults.", 9, 450m, now, ct),
            await EnsureDoctorAsync(db, clinic, DoctorYoussefPhone, "dr-youssef-nabil", "د. يوسف نبيل", "Dr. Youssef Nabil", ReferenceData.PediatricId,
                "طبيب أسنان أطفال يهتم بتجربة هادئة وممتعة للطفل.",
                "Pediatric dentist who makes visits calm and friendly for children.", 7, 350m, now, ct),
        };
        await db.SaveChangesAsync(ct);

        foreach (var doctor in doctors)
        {
            await EnsureServicesAsync(db, doctor, ct);
        }

        await db.SaveChangesAsync(ct);

        var zone = ClinicTime.FindZone(clinic.Timezone);
        var today = ClinicTime.LocalDate(now, zone);
        var created = 0;
        foreach (var doctor in doctors)
        {
            created += await TopUpSlotsAsync(db, doctor, clinic, zone, today, now, ct);
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Dev seed ready: clinic {Clinic}, {Doctors} doctors, {Slots} new slots.", clinic.NameEn, doctors.Length, created);
    }

    private static async Task<User> EnsureUserAsync(MoonlightDbContext db, string phone, string name, UserRole role, DateTime now, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Phone == phone, ct);
        if (user is null)
        {
            user = new User { Phone = phone, FullName = name, Role = role, CreatedAt = now };
            db.Users.Add(user);
        }

        return user;
    }

    private static async Task<Doctor> EnsureDoctorAsync(
        MoonlightDbContext db, Clinic clinic, string phone, string slug, string nameAr, string nameEn, Guid specialtyId,
        string bioAr, string bioEn, int years, decimal fee, DateTime now, CancellationToken ct)
    {
        var user = await EnsureUserAsync(db, phone, nameEn, UserRole.Doctor, now, ct);
        var doctor = await db.Doctors.FirstOrDefaultAsync(d => d.Slug == slug, ct);
        if (doctor is null)
        {
            doctor = new Doctor
            {
                UserId = user.Id,
                ClinicId = clinic.Id,
                SpecialtyId = specialtyId,
                Slug = slug,
                NameAr = nameAr,
                NameEn = nameEn,
                BioAr = bioAr,
                BioEn = bioEn,
                YearsExperience = years,
                ConsultationFeeEgp = fee,
                IsPublished = true,
                CreatedAt = now,
            };
            db.Doctors.Add(doctor);
        }

        return doctor;
    }

    private static async Task EnsureServicesAsync(MoonlightDbContext db, Doctor doctor, CancellationToken ct)
    {
        if (await db.Services.AnyAsync(s => s.DoctorId == doctor.Id, ct))
        {
            return;
        }

        (string Ar, string En, int Minutes, decimal Price)[] services = doctor.Slug switch
        {
            "dr-sara-mahmoud" =>
            [
                ("استشارة تقويم", "Orthodontic consultation", 30, 300m),
                ("ضبط التقويم", "Braces adjustment", 30, 250m),
            ],
            "dr-youssef-nabil" =>
            [
                ("كشف أطفال", "Child check-up", 30, 300m),
                ("حشو لبني", "Baby tooth filling", 45, 400m),
            ],
            _ =>
            [
                ("كشف", "Check-up", 30, 300m),
                ("تنظيف الأسنان", "Cleaning", 45, 500m),
                ("حشو", "Filling", 60, 700m),
            ],
        };
        db.Services.AddRange(services.Select(s => new Service
        {
            DoctorId = doctor.Id,
            NameAr = s.Ar,
            NameEn = s.En,
            DurationMinutes = s.Minutes,
            PriceEgp = s.Price,
        }));
    }

    // Saturday to Thursday, two sessions a day, 30-minute slots, for the next 14 clinic-local days.
    private static async Task<int> TopUpSlotsAsync(
        MoonlightDbContext db, Doctor doctor, Clinic clinic, TimeZoneInfo zone, DateOnly today, DateTime now, CancellationToken ct)
    {
        DayOfWeek[] workingDays = [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];
        var (windowStart, windowEnd) = ClinicTime.DayRangeUtc(today, today.AddDays(SlotDays), zone);
        var existing = await db.Slots.AsNoTracking()
            .Where(s => s.DoctorId == doctor.Id && s.StartsAt < windowEnd && s.EndsAt > windowStart)
            .Select(s => new TimeRange(s.StartsAt, s.EndsAt))
            .ToListAsync(ct);

        var created = 0;
        foreach (var (start, end) in new[] { (new TimeOnly(10, 0), new TimeOnly(14, 0)), (new TimeOnly(17, 0), new TimeOnly(20, 0)) })
        {
            var result = SlotGenerator.Generate(new BulkSlotSpec(today, today.AddDays(SlotDays - 1), workingDays, start, end, 30), zone, now, existing);
            db.Slots.AddRange(result.Created.Select(r => new Slot
            {
                DoctorId = doctor.Id,
                ClinicId = clinic.Id,
                StartsAt = r.StartsAt,
                EndsAt = r.EndsAt,
                CreatedAt = now,
            }));
            existing = [.. existing, .. result.Created];
            created += result.Created.Count;
        }

        return created;
    }
}
