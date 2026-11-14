using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Storage;

namespace Moonlight.Api.Features.Dashboard;

public sealed class DoctorManagementService(MoonlightDbContext db, DashboardScope scope, IPhotoStorage storage)
{
    public const int MaxPhotoBytes = 2 * 1024 * 1024;

    public async Task<List<ManagedDoctorDto>> ListAsync(CancellationToken ct)
    {
        var doctors = await scope.Doctors().AsNoTracking().OrderBy(d => d.NameEn).ThenBy(d => d.Id).ToListAsync(ct);
        return doctors.Select(ToDto).ToList();
    }

    public async Task<ManagedDoctorDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await scope.RequireDoctorAsync(id, ct));

    public async Task<ManagedDoctorDto> UpdateAsync(Guid id, UpdateDoctorDto request, CancellationToken ct)
    {
        var errors = new ValidationErrors()
            .RequireText(request.NameAr, "nameAr", 200)
            .RequireText(request.NameEn, "nameEn", 200)
            .RequireText(request.BioAr, "bioAr", 4000, required: false)
            .RequireText(request.BioEn, "bioEn", 4000, required: false)
            .Require(request.SpecialtyId is not null, "specialtyId", "specialtyId is required.")
            .Require(request.YearsExperience is null or (>= 0 and <= 80), "yearsExperience", "yearsExperience must be between 0 and 80.")
            .Require(request.ConsultationFeeEgp is null or (>= 0 and <= 1_000_000), "consultationFeeEgp", "consultationFeeEgp must be a non-negative amount.");
        errors.ThrowIfAny();

        var doctor = await scope.RequireDoctorAsync(id, ct);
        if (!await db.Specialties.AnyAsync(s => s.Id == request.SpecialtyId, ct))
        {
            throw ApiException.Validation("specialtyId", "Unknown specialty.");
        }

        doctor.NameAr = request.NameAr!.Trim();
        doctor.NameEn = request.NameEn!.Trim();
        doctor.BioAr = Clean(request.BioAr);
        doctor.BioEn = Clean(request.BioEn);
        doctor.YearsExperience = request.YearsExperience;
        doctor.ConsultationFeeEgp = request.ConsultationFeeEgp;
        doctor.SpecialtyId = request.SpecialtyId!.Value;
        doctor.IsPublished = request.IsPublished ?? doctor.IsPublished;
        await db.SaveChangesAsync(ct);
        return ToDto(doctor);
    }

    public async Task<string> UploadPhotoAsync(Guid id, IFormFile? file, string baseUrl, CancellationToken ct)
    {
        var doctor = await scope.RequireDoctorAsync(id, ct);
        if (file is null || file.Length == 0)
        {
            throw ApiException.Validation("file", "An image file is required.");
        }

        if (file.Length > MaxPhotoBytes)
        {
            throw ApiException.Validation("file", "The image must be 2 MB or smaller.");
        }

        await using var stream = file.OpenReadStream();
        var header = new byte[12];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var extension = DetectImage(header.AsSpan(0, read))
            ?? throw ApiException.Validation("file", "Only jpeg, png and webp images are accepted.");
        stream.Position = 0;

        var path = await storage.SaveAsync($"{doctor.Id:N}-{Guid.NewGuid():N}.{extension}", stream, ct);
        doctor.PhotoUrl = baseUrl.TrimEnd('/') + path;
        await db.SaveChangesAsync(ct);
        return doctor.PhotoUrl;
    }

    // ---- Services ---------------------------------------------------------------------------------------------

    public async Task<List<ManagedServiceDto>> ListServicesAsync(Guid doctorId, CancellationToken ct)
    {
        await scope.RequireDoctorAsync(doctorId, ct);
        var services = await db.Services.AsNoTracking().Where(s => s.DoctorId == doctorId)
            .OrderBy(s => s.DurationMinutes).ThenBy(s => s.NameEn).ThenBy(s => s.Id).ToListAsync(ct);
        return services.Select(ToDto).ToList();
    }

    public async Task<ManagedServiceDto> CreateServiceAsync(Guid doctorId, ServiceInputDto request, CancellationToken ct)
    {
        await scope.RequireDoctorAsync(doctorId, ct);
        ValidateService(request);
        var service = new Service { DoctorId = doctorId, NameAr = "", NameEn = "" };
        Apply(service, request);
        db.Services.Add(service);
        await db.SaveChangesAsync(ct);
        return ToDto(service);
    }

    public async Task<ManagedServiceDto> UpdateServiceAsync(Guid doctorId, Guid serviceId, ServiceInputDto request, CancellationToken ct)
    {
        await scope.RequireDoctorAsync(doctorId, ct);
        ValidateService(request);
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == serviceId && s.DoctorId == doctorId, ct) ?? throw ApiException.NotFound();
        Apply(service, request);
        await db.SaveChangesAsync(ct);
        return ToDto(service);
    }

    // Services are never hard-deleted (appointments may reference them).
    public async Task DeactivateServiceAsync(Guid doctorId, Guid serviceId, CancellationToken ct)
    {
        await scope.RequireDoctorAsync(doctorId, ct);
        var service = await db.Services.FirstOrDefaultAsync(s => s.Id == serviceId && s.DoctorId == doctorId, ct) ?? throw ApiException.NotFound();
        service.IsActive = false;
        await db.SaveChangesAsync(ct);
    }

    private static void ValidateService(ServiceInputDto request) =>
        new ValidationErrors()
            .RequireText(request.NameAr, "nameAr", 200)
            .RequireText(request.NameEn, "nameEn", 200)
            .Require(
                request.DurationMinutes is >= Service.MinDurationMinutes and <= Service.MaxDurationMinutes,
                "durationMinutes",
                $"durationMinutes must be between {Service.MinDurationMinutes} and {Service.MaxDurationMinutes}.")
            .Require(request.PriceEgp is null or (>= 0 and <= 1_000_000), "priceEgp", "priceEgp must be a non-negative amount.")
            .ThrowIfAny();

    private static void Apply(Service service, ServiceInputDto request)
    {
        service.NameAr = request.NameAr!.Trim();
        service.NameEn = request.NameEn!.Trim();
        service.DurationMinutes = request.DurationMinutes!.Value;
        service.PriceEgp = request.PriceEgp;
        service.IsActive = request.IsActive ?? service.IsActive;
    }

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Sniffs the file signature rather than trusting the client's content type.
    private static string? DetectImage(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            return "jpg";
        }

        if (h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47)
        {
            return "png";
        }

        if (h.Length >= 12 && h[0] == 'R' && h[1] == 'I' && h[2] == 'F' && h[3] == 'F' && h[8] == 'W' && h[9] == 'E' && h[10] == 'B' && h[11] == 'P')
        {
            return "webp";
        }

        return null;
    }

    private static ManagedDoctorDto ToDto(Doctor d) => new(
        d.Id, d.ClinicId, d.SpecialtyId, d.Slug, d.NameAr, d.NameEn, d.BioAr, d.BioEn, d.PhotoUrl, d.YearsExperience, d.ConsultationFeeEgp, d.IsPublished);

    private static ManagedServiceDto ToDto(Service s) => new(s.Id, s.NameAr, s.NameEn, s.DurationMinutes, s.PriceEgp, s.IsActive);
}
