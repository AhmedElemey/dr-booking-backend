using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Auth;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Admin;

public sealed record CreateClinicDto(
    string? NameAr,
    string? NameEn,
    string? AddressAr,
    string? AddressEn,
    string? City,
    string? Phone,
    double? Latitude,
    double? Longitude,
    string? Timezone);

public sealed record SetActiveDto(bool? IsActive);

public sealed record CreateUserDto(string? Phone, string? FullName, UserRole? Role, string? NameAr, string? NameEn, Guid? SpecialtyId);

public sealed record AdminUserDto(Guid Id, string Phone, string? FullName, UserRole Role, bool IsActive, Guid? ClinicId, Guid? DoctorId);

public sealed record AdminFeatureValueDto(bool Effective, bool HasOverride);

public sealed record AdminClinicFeaturesDto(
    Guid ClinicId,
    decimal? DepositAmountEgp,
    IReadOnlyDictionary<string, AdminFeatureValueDto> Features);

public sealed record UpdateClinicFeaturesDto(
    Dictionary<string, bool?>? Features,
    decimal? DepositAmountEgp,
    bool? ClearDepositAmountEgp);

public sealed class AdminService(MoonlightDbContext db, CurrentUser current, TimeProvider clock, FeatureService features, ClinicFeatureOverrides overrides)
{
    public async Task<AdminClinicFeaturesDto> GetFeaturesAsync(Guid clinicId, CancellationToken ct)
    {
        var clinic = await db.Clinics.AsNoTracking().FirstOrDefaultAsync(c => c.Id == clinicId, ct)
            ?? throw ApiException.NotFound();
        return FeaturesDto(clinic);
    }

    public async Task<AdminClinicFeaturesDto> UpdateFeaturesAsync(Guid clinicId, UpdateClinicFeaturesDto request, CancellationToken ct)
    {
        var unknown = request.Features is null ? [] : request.Features.Keys.Where(k => !FeatureKeys.All.Contains(k)).ToList();
        new ValidationErrors()
            .Require(unknown.Count == 0, "features", "unknown feature key: " + string.Join(", ", unknown))
            .Require(request.DepositAmountEgp is null or >= 0, "depositAmountEgp", "depositAmountEgp must be zero or more.")
            .ThrowIfAny();

        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.Id == clinicId, ct) ?? throw ApiException.NotFound();

        if (request.Features is { } changes)
        {
            var rows = await db.ClinicFeatureOverrides.Where(o => o.ClinicId == clinicId).ToListAsync(ct);
            foreach (var (key, value) in changes)
            {
                var row = rows.FirstOrDefault(o => o.Key == key);
                if (value is null)
                {
                    if (row is not null)
                    {
                        db.ClinicFeatureOverrides.Remove(row);
                    }
                }
                else if (row is null)
                {
                    db.ClinicFeatureOverrides.Add(new ClinicFeatureOverride
                    {
                        ClinicId = clinicId,
                        Key = key,
                        Enabled = value.Value,
                        UpdatedAt = clock.GetUtcNow().UtcDateTime,
                    });
                }
                else
                {
                    row.Enabled = value.Value;
                    row.UpdatedAt = clock.GetUtcNow().UtcDateTime;
                }
            }
        }

        if (request.ClearDepositAmountEgp == true)
        {
            clinic.DepositAmountEgp = null;
        }
        else if (request.DepositAmountEgp is not null)
        {
            clinic.DepositAmountEgp = request.DepositAmountEgp;
        }

        await db.SaveChangesAsync(ct);

        var remaining = await db.ClinicFeatureOverrides.AsNoTracking()
            .Where(o => o.ClinicId == clinicId)
            .ToDictionaryAsync(o => o.Key, o => o.Enabled, ct);
        overrides.SetLocal(clinicId, remaining);
        return FeaturesDto(clinic);
    }

    private AdminClinicFeaturesDto FeaturesDto(Domain.Clinic clinic)
    {
        var values = FeatureKeys.All.ToDictionary(
            key => key,
            key =>
            {
                var over = overrides.Get(clinic.Id, key);
                return new AdminFeatureValueDto(over ?? features.IsEnabled(key), over is not null);
            });
        return new AdminClinicFeaturesDto(clinic.Id, clinic.DepositAmountEgp, values);
    }

    public async Task<PagedResult<ClinicDto>> ListClinicsAsync(PageRequest page, CancellationToken ct) =>
        await db.Clinics.AsNoTracking().OrderBy(c => c.NameEn).ThenBy(c => c.Id).ToPagedAsync(
            page, rows => Task.FromResult<IReadOnlyList<ClinicDto>>(rows.Select(ClinicManagementService.ToDto).ToList()), ct);

    public async Task<ClinicDto> CreateClinicAsync(CreateClinicDto request, CancellationToken ct)
    {
        // A single-branch clinic has exactly one clinic row; the first one is always allowed.
        if (!features.IsEnabled(FeatureKeys.MultiBranch) && await db.Clinics.AnyAsync(ct))
        {
            throw ApiException.FeatureDisabled(FeatureKeys.MultiBranch);
        }

        var city = ReferenceData.Cities.FirstOrDefault(c => c.Equals(request.City?.Trim(), StringComparison.OrdinalIgnoreCase));
        var timezone = string.IsNullOrWhiteSpace(request.Timezone) ? Clinic.DefaultTimezone : request.Timezone.Trim();
        new ValidationErrors()
            .RequireText(request.NameAr, "nameAr", 200)
            .RequireText(request.NameEn, "nameEn", 200)
            .RequireText(request.AddressAr, "addressAr", 500)
            .RequireText(request.AddressEn, "addressEn", 500)
            .RequireText(request.Phone, "phone", 30)
            .Require(city is not null, "city", "city must be one of: " + string.Join(", ", ReferenceData.Cities))
            .Require(ClinicTime.IsValidZone(timezone), "timezone", "timezone must be a valid IANA time zone id.")
            .Require(request.Latitude is null or (>= -90 and <= 90), "latitude", "latitude must be between -90 and 90.")
            .Require(request.Longitude is null or (>= -180 and <= 180), "longitude", "longitude must be between -180 and 180.")
            .Require((request.Latitude is null) == (request.Longitude is null), "latitude", "latitude and longitude must be set together.")
            .ThrowIfAny();

        var clinic = new Clinic
        {
            NameAr = request.NameAr!.Trim(),
            NameEn = request.NameEn!.Trim(),
            AddressAr = request.AddressAr!.Trim(),
            AddressEn = request.AddressEn!.Trim(),
            City = city!,
            Phone = request.Phone!.Trim(),
            Latitude = request.Latitude,
            Longitude = request.Longitude,
            Timezone = timezone,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Clinics.Add(clinic);
        await db.SaveChangesAsync(ct);
        return ClinicManagementService.ToDto(clinic);
    }

    public async Task<ClinicDto> SetClinicActiveAsync(Guid id, bool? isActive, CancellationToken ct)
    {
        new ValidationErrors().Require(isActive is not null, "isActive", "isActive is required.").ThrowIfAny();
        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.Id == id, ct) ?? throw ApiException.NotFound();
        clinic.IsActive = isActive!.Value;
        await db.SaveChangesAsync(ct);
        return ClinicManagementService.ToDto(clinic);
    }

    public async Task<AdminUserDto> CreateUserAsync(Guid clinicId, CreateUserDto request, CancellationToken ct)
    {
        var clinic = await db.Clinics.FirstOrDefaultAsync(c => c.Id == clinicId, ct) ?? throw ApiException.NotFound();
        var errors = new ValidationErrors()
            .Require(request.Role is UserRole.Doctor or UserRole.ClinicStaff, "role", "role must be 'Doctor' or 'ClinicStaff'.")
            .RequireText(request.FullName, "fullName", 200);
        var phone = PhoneNumber.TryNormalize(request.Phone, out var normalized) ? normalized : null;
        errors.Require(phone is not null, "phone", "Enter a valid Egyptian mobile number, for example 01012345678.");
        if (request.Role == UserRole.Doctor)
        {
            errors.RequireText(request.NameAr, "nameAr", 200).RequireText(request.NameEn, "nameEn", 200);
            errors.Require(request.SpecialtyId is not null, "specialtyId", "specialtyId is required for doctors.");
        }

        errors.ThrowIfAny();

        if (request.Role == UserRole.Doctor && !await db.Specialties.AnyAsync(s => s.Id == request.SpecialtyId, ct))
        {
            throw ApiException.Validation("specialtyId", "Unknown specialty.");
        }

        if (await db.Users.AnyAsync(u => u.Phone == phone, ct))
        {
            throw new ApiException(ErrorCodes.PhoneInUse);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var user = new User { Phone = phone!, FullName = request.FullName!.Trim(), Role = request.Role!.Value, CreatedAt = now };
        db.Users.Add(user);

        Doctor? doctor = null;
        if (request.Role == UserRole.Doctor)
        {
            doctor = new Doctor
            {
                UserId = user.Id,
                ClinicId = clinic.Id,
                SpecialtyId = request.SpecialtyId!.Value,
                Slug = await UniqueSlugAsync(request.NameEn!, ct),
                NameAr = request.NameAr!.Trim(),
                NameEn = request.NameEn!.Trim(),
                IsPublished = false,
                CreatedAt = now,
            };
            db.Doctors.Add(doctor);
        }
        else
        {
            db.ClinicStaff.Add(new ClinicStaff { UserId = user.Id, ClinicId = clinic.Id });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new ApiException(ErrorCodes.PhoneInUse);
        }

        return new AdminUserDto(user.Id, user.Phone, user.FullName, user.Role, user.IsActive, clinic.Id, doctor?.Id);
    }

    public async Task<AdminUserDto> SetUserActiveAsync(Guid id, bool? isActive, CancellationToken ct)
    {
        new ValidationErrors().Require(isActive is not null, "isActive", "isActive is required.").ThrowIfAny();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw ApiException.NotFound();
        if (user.Id == current.Id && isActive == false)
        {
            throw ApiException.Validation("isActive", "You cannot deactivate your own account.");
        }

        user.IsActive = isActive!.Value;
        if (!user.IsActive)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            await db.RefreshTokens.Where(t => t.UserId == id && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
        }

        await db.SaveChangesAsync(ct);
        var (clinicId, doctorId) = await db.LoadAsync(user, ct);
        return new AdminUserDto(user.Id, user.Phone, user.FullName, user.Role, user.IsActive, clinicId, doctorId);
    }

    private async Task<string> UniqueSlugAsync(string nameEn, CancellationToken ct)
    {
        var baseSlug = Slug.From(nameEn);
        var slug = baseSlug;
        for (var n = 2; await db.Doctors.AnyAsync(d => d.Slug == slug, ct); n++)
        {
            slug = $"{baseSlug}-{n}";
        }

        return slug;
    }
}
