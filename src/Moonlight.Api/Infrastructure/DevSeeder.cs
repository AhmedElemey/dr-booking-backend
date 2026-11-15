using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Holidays;

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
    public const string PatientNourPhone = "+201000000021";
    public const string PatientHalaPhone = "+201000000022";
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
        await SeedPhase2Async(db, clinic, doctors, zone, today, now, ct);
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

    // Sample data for the 13 Phase 2 features. The feature switches themselves stay off by default (clinic.config.json);
    // flip them on in the console or in the config to see the data. Idempotent: skipped once the demo coupon exists.
    private static async Task SeedPhase2Async(
        MoonlightDbContext db, Clinic clinic, Doctor[] doctors, TimeZoneInfo zone, DateOnly today, DateTime now, CancellationToken ct)
    {
        if (await db.Coupons.AnyAsync(c => c.Code == "WELCOME10", ct))
        {
            return;
        }

        var amr = doctors.Single(d => d.Slug == "dr-amr-hassan");
        var sara = doctors.Single(d => d.Slug == "dr-sara-mahmoud");
        var youssef = doctors.Single(d => d.Slug == "dr-youssef-nabil");
        sara.OffersVideo = true;

        // insurance
        var misr = new InsuranceProvider { NameAr = "مصر للتأمين", NameEn = "Misr Insurance" };
        var allianz = new InsuranceProvider { NameAr = "أليانز مصر", NameEn = "Allianz Egypt" };
        var bupa = new InsuranceProvider { NameAr = "بوبا مصر", NameEn = "Bupa Egypt" };
        db.InsuranceProviders.AddRange(misr, allianz, bupa);
        db.DoctorInsuranceProviders.AddRange(
            new DoctorInsuranceProvider { DoctorId = amr.Id, InsuranceProviderId = misr.Id },
            new DoctorInsuranceProvider { DoctorId = amr.Id, InsuranceProviderId = allianz.Id },
            new DoctorInsuranceProvider { DoctorId = sara.Id, InsuranceProviderId = misr.Id },
            new DoctorInsuranceProvider { DoctorId = sara.Id, InsuranceProviderId = allianz.Id },
            new DoctorInsuranceProvider { DoctorId = sara.Id, InsuranceProviderId = bupa.Id },
            new DoctorInsuranceProvider { DoctorId = youssef.Id, InsuranceProviderId = misr.Id });

        // promotions
        db.Coupons.AddRange(
            new Coupon
            {
                ClinicId = clinic.Id,
                Code = "WELCOME10",
                TitleAr = "خصم 10% للمرضى الجدد",
                TitleEn = "10% off for new patients",
                DescriptionAr = "خصم 10% على أول حجز (حتى 100 جنيه).",
                DescriptionEn = "10% off your first booking (up to EGP 100).",
                DiscountType = CouponDiscountType.Percent,
                Value = 10,
                MaxDiscountEgp = 100,
                ValidFrom = today,
                ValidTo = today.AddDays(90),
                PerPatientLimit = 1,
                IsPublic = true,
                CreatedAt = now,
            },
            new Coupon
            {
                ClinicId = clinic.Id,
                Code = "SMILE50",
                TitleAr = "خصم 50 جنيه",
                TitleEn = "EGP 50 off",
                DiscountType = CouponDiscountType.Fixed,
                Value = 50,
                ValidFrom = today,
                ValidTo = today.AddDays(30),
                MaxRedemptions = 100,
                PerPatientLimit = 2,
                IsPublic = false,
                CreatedAt = now,
            });

        // holidays (Egyptian public holidays of this and next year; moveable dates are estimates) and a template
        foreach (var year in new[] { today.Year, today.Year + 1 })
        {
            db.ClinicHolidays.AddRange(EgyptHolidays.ForYear(year).Select(h => new ClinicHoliday
            {
                ClinicId = clinic.Id,
                Date = h.Date,
                NameAr = h.NameAr,
                NameEn = h.NameEn,
                Source = HolidaySource.EgyptPublic,
            }));
        }

        DayOfWeek[] workingDays = [DayOfWeek.Saturday, DayOfWeek.Sunday, DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday];
        db.WorkingHoursTemplates.Add(new WorkingHoursTemplate
        {
            ClinicId = clinic.Id,
            Name = "Standard week (Sat-Thu)",
            SlotMinutes = 30,
            Rules = workingDays.SelectMany(d => new[]
            {
                new WorkingHoursRule { DayOfWeek = d, Start = "10:00", End = "14:00" },
                new WorkingHoursRule { DayOfWeek = d, Start = "17:00", End = "20:00" },
            }).ToList(),
        });

        // patients: Nour (with a family member), Hala (lapsed, for the recall campaign)
        var nour = await EnsureUserAsync(db, PatientNourPhone, "Nour Ahmed", UserRole.Patient, now, ct);
        var hala = await EnsureUserAsync(db, PatientHalaPhone, "Hala Samir", UserRole.Patient, now, ct);
        var omar = new FamilyMember { OwnerUserId = nour.Id, FullName = "Omar Ahmed", Relation = FamilyRelation.Child, DateOfBirth = today.AddYears(-8), Gender = Gender.Male, CreatedAt = now };
        db.FamilyMembers.Add(omar);
        await db.SaveChangesAsync(ct);

        // a completed, paid and reviewed visit two weeks ago
        var past = await PastSlotAsync(db, amr, clinic, zone, today.AddDays(-14), 11, now, ct);
        var completed = new Appointment
        {
            SlotId = past.Id, PatientUserId = nour.Id, CreatedByUserId = nour.Id, Status = AppointmentStatus.Completed,
            ServiceId = await db.Services.Where(s => s.DoctorId == amr.Id).OrderBy(s => s.DurationMinutes).Select(s => (Guid?)s.Id).FirstAsync(ct),
            PriceEgp = 300, CompletedAt = past.EndsAt, ReviewPromptSentAt = past.EndsAt, CreatedAt = now.AddDays(-20), UpdatedAt = past.EndsAt,
        };
        db.Appointments.Add(completed);
        db.Reviews.Add(new Review
        {
            AppointmentId = completed.Id, DoctorId = amr.Id, PatientUserId = nour.Id, Rating = 5,
            Comment = "Gentle and thorough. Highly recommended.", Reply = "Thank you Nour, see you at the next check-up!", RepliedAt = past.EndsAt.AddDays(1),
            CreatedAt = past.EndsAt.AddHours(3),
        });
        db.Payments.Add(new Payment
        {
            AppointmentId = completed.Id, AmountEgp = 300, Status = PaymentStatus.Paid, Method = PaymentMethod.Online, Provider = "mock",
            ProviderRef = "seed-" + completed.Id.ToString("N"), CreatedAt = now.AddDays(-20), PaidAt = now.AddDays(-20),
        });

        // upcoming: Omar at the pediatric dentist (insurance claim pending), Nour on a video call with Dr Sara
        var forOmar = await OpenSlotAsync(db, youssef, today.AddDays(2), ct);
        var forVideo = await OpenSlotAsync(db, sara, today.AddDays(3), ct);
        if (forOmar is not null)
        {
            forOmar.Status = SlotStatus.Reserved;
            db.Appointments.Add(new Appointment
            {
                SlotId = forOmar.Id, PatientUserId = nour.Id, FamilyMemberId = omar.Id, CreatedByUserId = nour.Id, PriceEgp = 300,
                InsuranceProviderId = misr.Id, InsuranceMemberNumber = "MI-204518", InsuranceStatus = InsuranceStatus.Pending, CreatedAt = now, UpdatedAt = now,
            });
        }

        if (forVideo is not null)
        {
            forVideo.Status = SlotStatus.Reserved;
            db.Appointments.Add(new Appointment
            {
                SlotId = forVideo.Id, PatientUserId = nour.Id, CreatedByUserId = nour.Id, Mode = AppointmentMode.Video, PriceEgp = 300, CreatedAt = now, UpdatedAt = now,
            });
        }

        // a lapsed patient (last visit 8 months ago) and the recall campaign that targets her
        var lapsed = await PastSlotAsync(db, amr, clinic, zone, today.AddMonths(-8), 12, now, ct);
        db.Appointments.Add(new Appointment
        {
            SlotId = lapsed.Id, PatientUserId = hala.Id, CreatedByUserId = hala.Id, Status = AppointmentStatus.Completed, PriceEgp = 300,
            CompletedAt = lapsed.EndsAt, ReviewPromptSentAt = lapsed.EndsAt, CreatedAt = now.AddMonths(-8), UpdatedAt = lapsed.EndsAt,
        });
        db.RecallCampaigns.Add(new RecallCampaign
        {
            ClinicId = clinic.Id,
            Name = "6-month check-up reminder",
            MessageAr = "أهلا {name}، مر وقت طويل على زيارتك الأخيرة مع {doctor}. احجزي موعدا في {clinic}: {link}",
            MessageEn = "Hi {name}, it has been a while since your last visit with {doctor}. Book your check-up at {clinic}: {link}",
            Channel = RecallChannel.Auto,
            MonthsSinceLastVisit = 6,
            Mode = RecallMode.Recurring,
            CreatedAt = now,
        });

        // waiting list: Hala waits for a day with Dr Youssef
        db.WaitingListEntries.Add(new WaitingListEntry { PatientUserId = hala.Id, DoctorId = youssef.Id, Date = today.AddDays(4), CreatedAt = now });

        // patient record: medical info and entries (one shared with the patient)
        db.PatientMedicalInfos.Add(new PatientMedicalInfo { PatientUserId = nour.Id, BloodType = "O+", Allergies = "Penicillin", UpdatedAt = now });
        db.PatientRecordEntries.AddRange(
            new PatientRecordEntry
            {
                PatientUserId = nour.Id, AppointmentId = completed.Id, DoctorId = amr.Id, ClinicId = clinic.Id, AuthorUserId = amr.UserId,
                Kind = RecordKind.Diagnosis, Title = "Mild gingivitis", Body = "Improve flossing; re-check in six months.", VisibleToPatient = true,
                CreatedAt = past.EndsAt, UpdatedAt = past.EndsAt,
            },
            new PatientRecordEntry
            {
                PatientUserId = nour.Id, AppointmentId = completed.Id, DoctorId = amr.Id, ClinicId = clinic.Id, AuthorUserId = amr.UserId,
                Kind = RecordKind.Note, Title = "Anxious about injections", Body = "Offer topical anaesthetic first.", VisibleToPatient = false,
                CreatedAt = past.EndsAt, UpdatedAt = past.EndsAt,
            });

        // messages log
        db.OutboundMessages.AddRange(
            new OutboundMessage
            {
                Channel = MessageChannel.WhatsApp, To = nour.Phone, UserId = nour.Id, ClinicId = clinic.Id, Kind = "appointment.confirmed",
                Body = "Appointment confirmed: Dr. Youssef Nabil at Smile Care Clinic", Status = MessageStatus.Sent, ProviderRef = "mock-seed", CreatedAt = now,
            },
            new OutboundMessage
            {
                Channel = MessageChannel.Sms, To = hala.Phone, UserId = hala.Id, ClinicId = clinic.Id, Kind = "recall",
                Body = "Smile Care Clinic: it has been a while since your last visit.", Status = MessageStatus.Failed, Error = "Sample failure", CreatedAt = now,
            });

        await db.SaveChangesAsync(ct);
    }

    private static async Task<Slot> PastSlotAsync(
        MoonlightDbContext db, Doctor doctor, Clinic clinic, TimeZoneInfo zone, DateOnly day, int hour, DateTime now, CancellationToken ct)
    {
        ClinicTime.TryLocalToUtc(day.ToDateTime(new TimeOnly(hour, 0)), zone, out var start);
        var slot = new Slot { DoctorId = doctor.Id, ClinicId = clinic.Id, StartsAt = start, EndsAt = start.AddMinutes(30), Status = SlotStatus.Reserved, CreatedAt = now };
        db.Slots.Add(slot);
        await db.SaveChangesAsync(ct);
        return slot;
    }

    private static Task<Slot?> OpenSlotAsync(MoonlightDbContext db, Doctor doctor, DateOnly from, CancellationToken ct)
    {
        var lowest = DateTime.SpecifyKind(from.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc);
        return db.Slots.Where(s => s.DoctorId == doctor.Id && s.Status == SlotStatus.Open && s.StartsAt >= lowest).OrderBy(s => s.StartsAt).FirstOrDefaultAsync(ct);
    }
}
