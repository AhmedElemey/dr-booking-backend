using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure;

public class MoonlightDbContext(DbContextOptions<MoonlightDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();

    public DbSet<Clinic> Clinics => Set<Clinic>();

    public DbSet<ClinicStaff> ClinicStaff => Set<ClinicStaff>();

    public DbSet<Specialty> Specialties => Set<Specialty>();

    public DbSet<Doctor> Doctors => Set<Doctor>();

    public DbSet<Service> Services => Set<Service>();

    public DbSet<Slot> Slots => Set<Slot>();

    public DbSet<Appointment> Appointments => Set<Appointment>();

    public DbSet<AppointmentStatusCorrection> AppointmentStatusCorrections => Set<AppointmentStatusCorrection>();

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    public DbSet<FeatureState> FeatureStates => Set<FeatureState>();

    public DbSet<ClinicHoliday> ClinicHolidays => Set<ClinicHoliday>();

    public DbSet<WorkingHoursTemplate> WorkingHoursTemplates => Set<WorkingHoursTemplate>();

    public DbSet<WaitingListEntry> WaitingListEntries => Set<WaitingListEntry>();

    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    public DbSet<OutboundMessage> OutboundMessages => Set<OutboundMessage>();

    public DbSet<Payment> Payments => Set<Payment>();

    public DbSet<InsuranceProvider> InsuranceProviders => Set<InsuranceProvider>();

    public DbSet<DoctorInsuranceProvider> DoctorInsuranceProviders => Set<DoctorInsuranceProvider>();

    public DbSet<PatientMedicalInfo> PatientMedicalInfos => Set<PatientMedicalInfo>();

    public DbSet<PatientRecordEntry> PatientRecordEntries => Set<PatientRecordEntry>();

    public DbSet<Review> Reviews => Set<Review>();

    public DbSet<Coupon> Coupons => Set<Coupon>();

    public DbSet<CouponRedemption> CouponRedemptions => Set<CouponRedemption>();

    public DbSet<RecallCampaign> RecallCampaigns => Set<RecallCampaign>();

    public DbSet<RecallSend> RecallSends => Set<RecallSend>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasIndex(u => u.Phone).IsUnique();
            e.Property(u => u.Phone).HasMaxLength(20);
            e.Property(u => u.FullName).HasMaxLength(200);
            e.Property(u => u.Role).HasConversion<string>().HasMaxLength(20);
            e.Property(u => u.PreferredLanguage).HasMaxLength(2);
            e.ToTable(t => t.HasCheckConstraint("ck_users_preferred_language", "preferred_language IN ('ar', 'en')"));
        });

        modelBuilder.Entity<Clinic>(e =>
        {
            e.Property(c => c.NameAr).HasMaxLength(200);
            e.Property(c => c.NameEn).HasMaxLength(200);
            e.Property(c => c.AddressAr).HasMaxLength(500);
            e.Property(c => c.AddressEn).HasMaxLength(500);
            e.Property(c => c.City).HasMaxLength(100);
            e.Property(c => c.Phone).HasMaxLength(30);
            e.Property(c => c.Timezone).HasMaxLength(64);
        });

        modelBuilder.Entity<ClinicStaff>(e =>
        {
            e.HasIndex(s => s.UserId).IsUnique();
            e.HasOne(s => s.User).WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(s => s.Clinic).WithMany().HasForeignKey(s => s.ClinicId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Specialty>(e =>
        {
            e.Property(s => s.NameAr).HasMaxLength(100);
            e.Property(s => s.NameEn).HasMaxLength(100);
            e.Property(s => s.Id).ValueGeneratedNever();
            e.HasData(ReferenceData.Specialties);
        });

        modelBuilder.Entity<Doctor>(e =>
        {
            e.HasIndex(d => d.UserId).IsUnique();
            e.HasIndex(d => d.Slug).IsUnique();
            e.HasIndex(d => d.ClinicId);
            e.HasIndex(d => d.SpecialtyId).HasFilter("is_published").HasDatabaseName("ix_doctors_specialty_id_published");
            e.Property(d => d.Slug).HasMaxLength(200);
            e.Property(d => d.NameAr).HasMaxLength(200);
            e.Property(d => d.NameEn).HasMaxLength(200);
            e.Property(d => d.BioAr).HasMaxLength(4000);
            e.Property(d => d.BioEn).HasMaxLength(4000);
            e.Property(d => d.PhotoUrl).HasMaxLength(500);
            e.Property(d => d.ConsultationFeeEgp).HasPrecision(10, 2);
            e.HasOne(d => d.User).WithMany().HasForeignKey(d => d.UserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Clinic).WithMany().HasForeignKey(d => d.ClinicId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(d => d.Specialty).WithMany().HasForeignKey(d => d.SpecialtyId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Service>(e =>
        {
            e.Property(s => s.NameAr).HasMaxLength(200);
            e.Property(s => s.NameEn).HasMaxLength(200);
            e.Property(s => s.PriceEgp).HasPrecision(10, 2);
            e.HasIndex(s => s.DoctorId);
            e.HasOne<Doctor>().WithMany().HasForeignKey(s => s.DoctorId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_services_duration", "duration_minutes BETWEEN 15 AND 240"));
        });

        modelBuilder.Entity<Slot>(e =>
        {
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(s => new { s.DoctorId, s.StartsAt }, "all_slots").HasDatabaseName("ix_slots_doctor_id_starts_at");
            e.HasIndex(s => new { s.DoctorId, s.StartsAt }, "open_slots").HasFilter("status = 'Open'").HasDatabaseName("ix_slots_doctor_id_starts_at_open");
            e.HasIndex(s => new { s.ClinicId, s.StartsAt });
            e.HasOne(s => s.Doctor).WithMany().HasForeignKey(s => s.DoctorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Clinic>().WithMany().HasForeignKey(s => s.ClinicId).OnDelete(DeleteBehavior.Restrict);
            e.ToTable(t => t.HasCheckConstraint("ck_slots_time_order", "ends_at > starts_at"));
            // The no_overlapping_slots exclusion constraint and btree_gist are created by raw SQL in a migration.
        });

        modelBuilder.Entity<Appointment>(e =>
        {
            e.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.CancelledBy).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.CancelReason).HasMaxLength(500);
            e.Property(a => a.PatientNote).HasMaxLength(1000);
            e.Property(a => a.RowVersion).IsRowVersion();
            e.HasIndex(a => a.SlotId)
                .IsUnique()
                .HasFilter("status <> 'Cancelled'")
                .HasDatabaseName("ux_appointments_active_slot");
            e.HasIndex(a => a.PatientUserId);
            e.HasIndex(a => a.CreatedAt)
                .HasFilter("status = 'Confirmed' AND reminder_sent_at IS NULL")
                .HasDatabaseName("ix_appointments_reminder_due");
            // Cancelled appointments of a freed slot go away if that slot is later removed.
            e.HasOne(a => a.Slot).WithMany().HasForeignKey(a => a.SlotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Patient).WithMany().HasForeignKey(a => a.PatientUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.Service).WithMany().HasForeignKey(a => a.ServiceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.CreatedByUserId).OnDelete(DeleteBehavior.Restrict);
            e.Property(a => a.Mode).HasConversion<string>().HasMaxLength(20).HasDefaultValue(AppointmentMode.InPerson);
            e.Property(a => a.PriceEgp).HasPrecision(10, 2);
            e.Property(a => a.DiscountEgp).HasPrecision(10, 2);
            e.Property(a => a.InsuranceStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.InsuranceMemberNumber).HasMaxLength(100);
            e.Property(a => a.InsuranceNote).HasMaxLength(500);
            e.Property(a => a.VideoRoomName).HasMaxLength(100);
            e.Property(a => a.VideoPatientUrl).HasMaxLength(500);
            e.Property(a => a.VideoHostUrl).HasMaxLength(500);
            e.HasIndex(a => a.FamilyMemberId);
            e.HasIndex(a => a.CompletedAt)
                .HasFilter("status = 'Completed' AND review_prompt_sent_at IS NULL")
                .HasDatabaseName("ix_appointments_review_prompt_due");
            e.HasOne(a => a.FamilyMember).WithMany().HasForeignKey(a => a.FamilyMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(a => a.InsuranceProvider).WithMany().HasForeignKey(a => a.InsuranceProviderId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(a => a.Payments).WithOne(p => p.Appointment).HasForeignKey(p => p.AppointmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(a => a.Review).WithOne().HasForeignKey<Review>(r => r.AppointmentId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AppointmentStatusCorrection>(e =>
        {
            e.Property(c => c.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.ToStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Reason).HasMaxLength(500);
            e.HasIndex(c => c.AppointmentId);
            e.HasOne(c => c.Appointment).WithMany().HasForeignKey(c => c.AppointmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(c => c.ChangedBy).WithMany().HasForeignKey(c => c.ChangedByUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<User>(e =>
        {
            e.Property(u => u.WhatsAppOptIn).HasDefaultValue(true);
            e.Property(u => u.MarketingOptIn).HasDefaultValue(true);
        });

        modelBuilder.Entity<Doctor>(e => e.Property(d => d.OffersVideo).HasDefaultValue(false));

        modelBuilder.Entity<ClinicHoliday>(e =>
        {
            e.HasIndex(h => new { h.ClinicId, h.Date }).IsUnique();
            e.Property(h => h.NameAr).HasMaxLength(200);
            e.Property(h => h.NameEn).HasMaxLength(200);
            e.Property(h => h.Source).HasConversion<string>().HasMaxLength(20);
            e.HasOne<Clinic>().WithMany().HasForeignKey(h => h.ClinicId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WorkingHoursTemplate>(e =>
        {
            e.Property(t => t.Name).HasMaxLength(200);
            e.Property(t => t.Rules)
                .HasColumnType("jsonb")
                .HasConversion(
                    rules => System.Text.Json.JsonSerializer.Serialize(rules, (System.Text.Json.JsonSerializerOptions?)null),
                    json => System.Text.Json.JsonSerializer.Deserialize<List<WorkingHoursRule>>(json, (System.Text.Json.JsonSerializerOptions?)null) ?? new List<WorkingHoursRule>(),
                    new Microsoft.EntityFrameworkCore.ChangeTracking.ValueComparer<List<WorkingHoursRule>>(
                        (a, b) => System.Text.Json.JsonSerializer.Serialize(a, (System.Text.Json.JsonSerializerOptions?)null) == System.Text.Json.JsonSerializer.Serialize(b, (System.Text.Json.JsonSerializerOptions?)null),
                        v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null).GetHashCode(),
                        v => System.Text.Json.JsonSerializer.Deserialize<List<WorkingHoursRule>>(System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null), (System.Text.Json.JsonSerializerOptions?)null)!));
            e.HasIndex(t => t.ClinicId);
            e.HasOne<Clinic>().WithMany().HasForeignKey(t => t.ClinicId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WaitingListEntry>(e =>
        {
            e.Property(w => w.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(w => new { w.DoctorId, w.Date, w.Status });
            e.HasIndex(w => w.PatientUserId);
            e.HasIndex(w => new { w.PatientUserId, w.DoctorId, w.Date, w.FamilyMemberId })
                .IsUnique()
                .HasFilter("status IN ('Waiting', 'Notified')")
                .AreNullsDistinct(false)
                .HasDatabaseName("ux_waiting_list_active");
            e.HasOne(w => w.Patient).WithMany().HasForeignKey(w => w.PatientUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(w => w.Doctor).WithMany().HasForeignKey(w => w.DoctorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FamilyMember>().WithMany().HasForeignKey(w => w.FamilyMemberId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FamilyMember>(e =>
        {
            e.Property(f => f.FullName).HasMaxLength(200);
            e.Property(f => f.Relation).HasConversion<string>().HasMaxLength(20);
            e.Property(f => f.Gender).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(f => f.OwnerUserId);
            e.HasOne<User>().WithMany().HasForeignKey(f => f.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OutboundMessage>(e =>
        {
            e.Property(m => m.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(m => m.To).HasMaxLength(100);
            e.Property(m => m.Kind).HasMaxLength(60);
            e.Property(m => m.Body).HasMaxLength(4000);
            e.Property(m => m.ProviderRef).HasMaxLength(200);
            e.Property(m => m.Error).HasMaxLength(1000);
            e.HasIndex(m => m.CreatedAt);
            e.HasIndex(m => m.ClinicId);
        });

        modelBuilder.Entity<Payment>(e =>
        {
            e.Property(p => p.AmountEgp).HasPrecision(10, 2);
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Method).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(p => p.Provider).HasMaxLength(40);
            e.Property(p => p.ProviderRef).HasMaxLength(200);
            e.Property(p => p.ProviderTransactionId).HasMaxLength(200);
            e.Property(p => p.ReturnUrl).HasMaxLength(500);
            e.HasIndex(p => p.AppointmentId, "ix_payments_appointment_id").HasDatabaseName("ix_payments_appointment_id");
            // One live checkout per appointment.
            e.HasIndex(p => p.AppointmentId, "ux_payments_live_checkout").IsUnique().HasFilter("status = 'Pending'").HasDatabaseName("ux_payments_live_checkout");
            e.HasIndex(p => new { p.Provider, p.ProviderRef });
            e.HasIndex(p => p.CreatedAt);
        });

        modelBuilder.Entity<InsuranceProvider>(e =>
        {
            e.Property(i => i.NameAr).HasMaxLength(200);
            e.Property(i => i.NameEn).HasMaxLength(200);
        });

        modelBuilder.Entity<DoctorInsuranceProvider>(e =>
        {
            e.HasKey(d => new { d.DoctorId, d.InsuranceProviderId });
            e.HasOne<Doctor>().WithMany().HasForeignKey(d => d.DoctorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne(d => d.InsuranceProvider).WithMany().HasForeignKey(d => d.InsuranceProviderId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PatientMedicalInfo>(e =>
        {
            e.Property(m => m.BloodType).HasMaxLength(10);
            e.Property(m => m.Allergies).HasMaxLength(2000);
            e.Property(m => m.ChronicConditions).HasMaxLength(2000);
            e.Property(m => m.Medications).HasMaxLength(2000);
            e.HasIndex(m => m.PatientUserId).IsUnique().HasFilter("family_member_id IS NULL").HasDatabaseName("ux_medical_info_self");
            e.HasIndex(m => new { m.PatientUserId, m.FamilyMemberId }).IsUnique().HasFilter("family_member_id IS NOT NULL").HasDatabaseName("ux_medical_info_member");
            e.HasOne<User>().WithMany().HasForeignKey(m => m.PatientUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<FamilyMember>().WithMany().HasForeignKey(m => m.FamilyMemberId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PatientRecordEntry>(e =>
        {
            e.Property(r => r.Kind).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Title).HasMaxLength(200);
            e.Property(r => r.Body).HasMaxLength(8000);
            e.Property(r => r.AttachmentFile).HasMaxLength(200);
            e.Property(r => r.AttachmentContentType).HasMaxLength(100);
            e.HasIndex(r => new { r.PatientUserId, r.ClinicId });
            e.HasOne<User>().WithMany().HasForeignKey(r => r.PatientUserId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(r => r.AuthorUserId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<FamilyMember>().WithMany().HasForeignKey(r => r.FamilyMemberId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<Appointment>().WithMany().HasForeignKey(r => r.AppointmentId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Doctor>().WithMany().HasForeignKey(r => r.DoctorId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Review>(e =>
        {
            e.HasIndex(r => r.AppointmentId).IsUnique();
            e.HasIndex(r => new { r.DoctorId, r.Status, r.CreatedAt });
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Comment).HasMaxLength(1000);
            e.Property(r => r.Reply).HasMaxLength(1000);
            e.ToTable(t => t.HasCheckConstraint("ck_reviews_rating", "rating BETWEEN 1 AND 5"));
            e.HasOne(r => r.Doctor).WithMany().HasForeignKey(r => r.DoctorId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(r => r.Patient).WithMany().HasForeignKey(r => r.PatientUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Coupon>(e =>
        {
            e.HasIndex(c => c.Code).IsUnique();
            e.Property(c => c.Code).HasMaxLength(20);
            e.Property(c => c.TitleAr).HasMaxLength(200);
            e.Property(c => c.TitleEn).HasMaxLength(200);
            e.Property(c => c.DescriptionAr).HasMaxLength(1000);
            e.Property(c => c.DescriptionEn).HasMaxLength(1000);
            e.Property(c => c.DiscountType).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Value).HasPrecision(10, 2);
            e.Property(c => c.MaxDiscountEgp).HasPrecision(10, 2);
            e.HasOne<Clinic>().WithMany().HasForeignKey(c => c.ClinicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Doctor>().WithMany().HasForeignKey(c => c.DoctorId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Service>().WithMany().HasForeignKey(c => c.ServiceId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CouponRedemption>(e =>
        {
            e.HasKey(r => new { r.CouponId, r.AppointmentId });
            e.Property(r => r.DiscountEgp).HasPrecision(10, 2);
            e.HasIndex(r => new { r.CouponId, r.PatientUserId });
            e.HasOne<Coupon>().WithMany().HasForeignKey(r => r.CouponId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Appointment>().WithMany().HasForeignKey(r => r.AppointmentId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(r => r.PatientUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecallCampaign>(e =>
        {
            e.Property(c => c.Name).HasMaxLength(200);
            e.Property(c => c.MessageAr).HasMaxLength(1000);
            e.Property(c => c.MessageEn).HasMaxLength(1000);
            e.Property(c => c.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(c => c.Mode).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(c => c.ClinicId);
            e.HasOne<Clinic>().WithMany().HasForeignKey(c => c.ClinicId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Doctor>().WithMany().HasForeignKey(c => c.DoctorId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne<Service>().WithMany().HasForeignKey(c => c.ServiceId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RecallSend>(e =>
        {
            e.Property(s => s.Channel).HasConversion<string>().HasMaxLength(20);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(s => new { s.CampaignId, s.PatientUserId, s.SentAt });
            e.HasOne<RecallCampaign>().WithMany().HasForeignKey(s => s.CampaignId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(s => s.PatientUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<OtpCode>(e =>
        {
            e.HasIndex(o => o.Phone);
            e.Property(o => o.Phone).HasMaxLength(20);
            e.Property(o => o.CodeHash).HasMaxLength(128);
        });

        modelBuilder.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.HasIndex(t => t.FamilyId);
            e.HasIndex(t => t.UserId);
            e.Property(t => t.TokenHash).HasMaxLength(128);
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DeviceToken>(e =>
        {
            e.HasIndex(t => t.FcmToken).IsUnique();
            e.HasIndex(t => t.UserId);
            e.Property(t => t.FcmToken).HasMaxLength(512);
            e.Property(t => t.Platform).HasConversion<string>().HasMaxLength(20);
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FeatureState>(e =>
        {
            e.ToTable("feature_state", t => t.HasCheckConstraint("ck_feature_state_singleton", "id = 1"));
            e.Property(f => f.Id).ValueGeneratedNever();
            e.Property(f => f.Features).HasColumnType("jsonb");
        });
    }
}
