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

    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();

    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<DeviceToken> DeviceTokens => Set<DeviceToken>();

    public DbSet<FeatureState> FeatureStates => Set<FeatureState>();

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
