namespace Moonlight.Api.Domain;

public class Specialty
{
    public Guid Id { get; set; }

    public required string NameAr { get; set; }

    public required string NameEn { get; set; }
}

public class Doctor
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public User? User { get; set; }

    public Guid ClinicId { get; set; }

    public Clinic? Clinic { get; set; }

    public Guid SpecialtyId { get; set; }

    public Specialty? Specialty { get; set; }

    public required string Slug { get; set; }

    public required string NameAr { get; set; }

    public required string NameEn { get; set; }

    public string? BioAr { get; set; }

    public string? BioEn { get; set; }

    public string? PhotoUrl { get; set; }

    public int? YearsExperience { get; set; }

    public decimal? ConsultationFeeEgp { get; set; }

    public bool IsPublished { get; set; }

    public DateTime CreatedAt { get; set; }
}

public class Service
{
    public const int MinDurationMinutes = 15;
    public const int MaxDurationMinutes = 240;

    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid DoctorId { get; set; }

    public required string NameAr { get; set; }

    public required string NameEn { get; set; }

    public int DurationMinutes { get; set; }

    public decimal? PriceEgp { get; set; }

    public bool IsActive { get; set; } = true;
}
