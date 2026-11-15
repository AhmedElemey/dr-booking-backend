using System.Net.Mime;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Promotions;

public static class CouponReasons
{
    public const string NotFound = "NotFound";
    public const string Expired = "Expired";
    public const string NotStarted = "NotStarted";
    public const string Exhausted = "Exhausted";
    public const string PerPatientLimit = "PerPatientLimit";
    public const string NotApplicable = "NotApplicable";
    public const string NoPrice = "NoPrice";
}

public sealed record ValidateCouponDto(string? Code, Guid? DoctorId, Guid? ServiceId);

public sealed record CouponValidationDto(bool Valid, decimal DiscountEgp, decimal TotalEgp, string? Reason);

public sealed record PromotionDto(
    string Code,
    string Title,
    string? Description,
    CouponDiscountType DiscountType,
    decimal Value,
    DateOnly ValidTo,
    Guid? DoctorId,
    Guid? ServiceId);

public sealed record CouponDto(
    Guid Id,
    Guid? ClinicId,
    string Code,
    string TitleAr,
    string TitleEn,
    string? DescriptionAr,
    string? DescriptionEn,
    CouponDiscountType DiscountType,
    decimal Value,
    decimal? MaxDiscountEgp,
    DateOnly ValidFrom,
    DateOnly ValidTo,
    int? MaxRedemptions,
    int PerPatientLimit,
    Guid? DoctorId,
    Guid? ServiceId,
    bool IsPublic,
    bool IsActive,
    int RedemptionsCount,
    DateTime CreatedAt);

public sealed record CouponInputDto(
    string? Code,
    string? TitleAr,
    string? TitleEn,
    string? DescriptionAr,
    string? DescriptionEn,
    CouponDiscountType? DiscountType,
    decimal? Value,
    decimal? MaxDiscountEgp,
    DateOnly? ValidFrom,
    DateOnly? ValidTo,
    int? MaxRedemptions,
    int? PerPatientLimit,
    Guid? DoctorId,
    Guid? ServiceId,
    bool? IsPublic,
    bool? IsActive);

public sealed record CouponRedemptionDto(Guid AppointmentId, Guid PatientUserId, string? PatientName, decimal DiscountEgp, DateTime CreatedAt);

public sealed record CouponEvaluation(bool Valid, string? Reason, Coupon? Coupon, decimal Price, decimal DiscountEgp)
{
    public decimal Total => Math.Max(0, Price - DiscountEgp);
}

public sealed partial class CouponService(
    MoonlightDbContext db,
    CurrentUser current,
    DashboardScope scope,
    Language lang,
    TimeProvider clock)
{
    [GeneratedRegex("^[A-Z0-9-]{3,20}$")]
    private static partial Regex CodePattern();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // The price a booking costs: the service price, else the doctor's consultation fee.
    public static decimal? PriceOf(Doctor doctor, Service? service) => service?.PriceEgp ?? doctor.ConsultationFeeEgp;

    public static string NormalizeCode(string? code) => (code ?? "").Trim().ToUpperInvariant();

    // ---- Patient ------------------------------------------------------------------------------------------------

    public async Task<List<PromotionDto>> PublicOffersAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(Now);
        var rows = await db.Coupons.AsNoTracking()
            .Where(c => c.IsPublic && c.IsActive && c.ValidFrom <= today && c.ValidTo >= today && (c.MaxRedemptions == null || c.RedemptionsCount < c.MaxRedemptions))
            .OrderBy(c => c.ValidTo).ThenBy(c => c.Code).ToListAsync(ct);
        return rows.Select(c => new PromotionDto(
            c.Code,
            lang.Pick(c.TitleAr, c.TitleEn),
            lang.PickOptional(c.DescriptionAr, c.DescriptionEn),
            c.DiscountType,
            c.Value,
            c.ValidTo,
            c.DoctorId,
            c.ServiceId)).ToList();
    }

    public async Task<CouponValidationDto> ValidateAsync(ValidateCouponDto request, CancellationToken ct)
    {
        new ValidationErrors()
            .RequireText(request.Code, "code", 40)
            .Require(request.DoctorId is not null, "doctorId", "doctorId is required.")
            .ThrowIfAny();

        var doctor = await db.Doctors.AsNoTracking().Include(d => d.Clinic).FirstOrDefaultAsync(d => d.Id == request.DoctorId, ct) ?? throw ApiException.NotFound();
        var service = request.ServiceId is { } serviceId
            ? await db.Services.AsNoTracking().FirstOrDefaultAsync(s => s.Id == serviceId && s.DoctorId == doctor.Id && s.IsActive, ct)
              ?? throw ApiException.Validation("serviceId", "The service does not exist for this doctor.")
            : null;
        var evaluation = await EvaluateAsync(request.Code, doctor, service, current.Id, ct);
        return new CouponValidationDto(evaluation.Valid, evaluation.DiscountEgp, evaluation.Total, evaluation.Reason);
    }

    /// <summary>Checks a coupon for one booking. The doctor must have its Clinic loaded.</summary>
    public async Task<CouponEvaluation> EvaluateAsync(string? code, Doctor doctor, Service? service, Guid patientUserId, CancellationToken ct)
    {
        var price = PriceOf(doctor, service) ?? 0m;
        var normalized = NormalizeCode(code);
        var coupon = normalized.Length == 0 ? null : await db.Coupons.AsNoTracking().FirstOrDefaultAsync(c => c.Code == normalized, ct);
        if (coupon is null || !coupon.IsActive)
        {
            return Invalid(CouponReasons.NotFound, price);
        }

        var applicable = (coupon.ClinicId is null || coupon.ClinicId == doctor.ClinicId)
            && (coupon.DoctorId is null || coupon.DoctorId == doctor.Id)
            && (coupon.ServiceId is null || coupon.ServiceId == service?.Id);
        if (!applicable)
        {
            return Invalid(CouponReasons.NotApplicable, price);
        }

        var today = ClinicTime.LocalDate(Now, ClinicTime.FindZone(doctor.Clinic!.Timezone));
        if (today < coupon.ValidFrom)
        {
            return Invalid(CouponReasons.NotStarted, price);
        }

        if (today > coupon.ValidTo)
        {
            return Invalid(CouponReasons.Expired, price);
        }

        if (coupon.MaxRedemptions is { } max && coupon.RedemptionsCount >= max)
        {
            return Invalid(CouponReasons.Exhausted, price);
        }

        var used = await db.CouponRedemptions.CountAsync(r => r.CouponId == coupon.Id && r.PatientUserId == patientUserId, ct);
        if (used >= coupon.PerPatientLimit)
        {
            return Invalid(CouponReasons.PerPatientLimit, price);
        }

        if (price <= 0)
        {
            return Invalid(CouponReasons.NoPrice, price);
        }

        return new CouponEvaluation(true, null, coupon, price, DiscountFor(coupon, price));
    }

    public static decimal DiscountFor(Coupon coupon, decimal price)
    {
        var discount = coupon.DiscountType == CouponDiscountType.Percent ? price * coupon.Value / 100m : coupon.Value;
        if (coupon.MaxDiscountEgp is { } cap)
        {
            discount = Math.Min(discount, cap);
        }

        return Math.Round(Math.Min(discount, price), 2, MidpointRounding.AwayFromZero);
    }

    // Used by booking inside its transaction: the conditional UPDATE keeps maxRedemptions exact under concurrency.
    public async Task RedeemAsync(Coupon coupon, Guid appointmentId, Guid patientUserId, decimal discountEgp, CancellationToken ct)
    {
        var updated = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE coupons SET redemptions_count = redemptions_count + 1 WHERE id = {coupon.Id} AND (max_redemptions IS NULL OR redemptions_count < max_redemptions)", ct);
        if (updated == 0)
        {
            throw Invalid(CouponReasons.Exhausted);
        }

        db.CouponRedemptions.Add(new CouponRedemption
        {
            CouponId = coupon.Id,
            AppointmentId = appointmentId,
            PatientUserId = patientUserId,
            DiscountEgp = discountEgp,
            CreatedAt = Now,
        });
    }

    /// <summary>A cancelled appointment gives its coupon use back.</summary>
    public async Task ReleaseAsync(Guid appointmentId, CancellationToken ct)
    {
        var released = await db.CouponRedemptions.Where(r => r.AppointmentId == appointmentId).Select(r => r.CouponId).ToListAsync(ct);
        if (released.Count == 0)
        {
            return;
        }

        await db.CouponRedemptions.Where(r => r.AppointmentId == appointmentId).ExecuteDeleteAsync(ct);
        foreach (var couponId in released)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE coupons SET redemptions_count = GREATEST(redemptions_count - 1, 0) WHERE id = {couponId}", ct);
        }
    }

    public static ApiException Invalid(string reason) =>
        new(ErrorCodes.CouponInvalid, extensions: new Dictionary<string, object?> { ["reason"] = reason });

    private static CouponEvaluation Invalid(string reason, decimal price) => new(false, reason, null, price, 0);

    // ---- Dashboard ----------------------------------------------------------------------------------------------

    public async Task<List<CouponDto>> ListAsync(CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var rows = await db.Coupons.AsNoTracking().Where(c => c.ClinicId == clinicId).OrderByDescending(c => c.CreatedAt).ThenBy(c => c.Id).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<CouponDto> GetAsync(Guid id, CancellationToken ct) => ToDto(await LoadAsync(id, tracking: false, ct));

    public async Task<CouponDto> CreateAsync(CouponInputDto request, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        await ValidateAsync(request, clinicId, ct);
        var coupon = new Coupon
        {
            ClinicId = clinicId,
            Code = NormalizeCode(request.Code),
            TitleAr = "",
            TitleEn = "",
            CreatedAt = Now,
        };
        Apply(coupon, request);
        db.Coupons.Add(coupon);
        await SaveAsync(ct);
        return ToDto(coupon);
    }

    public async Task<CouponDto> UpdateAsync(Guid id, CouponInputDto request, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        await ValidateAsync(request, clinicId, ct);
        var coupon = await LoadAsync(id, tracking: true, ct);
        coupon.Code = NormalizeCode(request.Code);
        Apply(coupon, request);
        await SaveAsync(ct);
        return ToDto(coupon);
    }

    // A coupon that was redeemed keeps its history: it is deactivated instead of removed.
    public async Task DeleteAsync(Guid id, CancellationToken ct)
    {
        var coupon = await LoadAsync(id, tracking: true, ct);
        if (coupon.RedemptionsCount > 0 || await db.CouponRedemptions.AnyAsync(r => r.CouponId == id, ct))
        {
            coupon.IsActive = false;
        }
        else
        {
            db.Coupons.Remove(coupon);
        }

        await db.SaveChangesAsync(ct);
    }

    public async Task<List<CouponRedemptionDto>> RedemptionsAsync(Guid id, CancellationToken ct)
    {
        await LoadAsync(id, tracking: false, ct);
        var rows = await db.CouponRedemptions.AsNoTracking().Where(r => r.CouponId == id)
            .Join(db.Users, r => r.PatientUserId, u => u.Id, (r, u) => new { r, u.FullName })
            .OrderByDescending(x => x.r.CreatedAt).ToListAsync(ct);
        return rows.Select(x => new CouponRedemptionDto(x.r.AppointmentId, x.r.PatientUserId, x.FullName, x.r.DiscountEgp, x.r.CreatedAt)).ToList();
    }

    private async Task<Coupon> LoadAsync(Guid id, bool tracking, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var query = tracking ? db.Coupons : db.Coupons.AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.Id == id && c.ClinicId == clinicId, ct) ?? throw ApiException.NotFound();
    }

    private async Task ValidateAsync(CouponInputDto request, Guid clinicId, CancellationToken ct)
    {
        var errors = new ValidationErrors()
            .Require(CodePattern().IsMatch(NormalizeCode(request.Code)), "code", "code must be 3-20 characters: letters A-Z, digits and hyphens.")
            .RequireText(request.TitleAr, "titleAr", 200)
            .RequireText(request.TitleEn, "titleEn", 200)
            .RequireText(request.DescriptionAr, "descriptionAr", 1000, required: false)
            .RequireText(request.DescriptionEn, "descriptionEn", 1000, required: false)
            .Require(request.DiscountType is not null, "discountType", "discountType must be Percent or Fixed.")
            .Require(request.Value is > 0 and <= 1_000_000, "value", "value must be greater than zero.")
            .Require(request.ValidFrom is not null, "validFrom", "validFrom is required.")
            .Require(request.ValidTo is not null, "validTo", "validTo is required.")
            .Require(request.MaxDiscountEgp is null or > 0, "maxDiscountEgp", "maxDiscountEgp must be greater than zero.")
            .Require(request.MaxRedemptions is null or > 0, "maxRedemptions", "maxRedemptions must be greater than zero.")
            .Require(request.PerPatientLimit is null or > 0, "perPatientLimit", "perPatientLimit must be at least 1.");
        if (request.DiscountType == CouponDiscountType.Percent)
        {
            errors.Require(request.Value is <= 100, "value", "A percent discount is at most 100.");
        }

        if (request.ValidFrom is { } from && request.ValidTo is { } to)
        {
            errors.Require(to >= from, "validTo", "validTo must not be before validFrom.");
        }

        errors.ThrowIfAny();

        if (request.DoctorId is { } doctorId && !await scope.Doctors().AnyAsync(d => d.Id == doctorId, ct))
        {
            throw ApiException.Validation("doctorId", "The doctor does not exist in this clinic.");
        }

        if (request.ServiceId is { } serviceId && !await db.Services.AnyAsync(s => s.Id == serviceId && db.Doctors.Any(d => d.Id == s.DoctorId && d.ClinicId == clinicId), ct))
        {
            throw ApiException.Validation("serviceId", "The service does not exist in this clinic.");
        }
    }

    private static void Apply(Coupon coupon, CouponInputDto request)
    {
        coupon.TitleAr = request.TitleAr!.Trim();
        coupon.TitleEn = request.TitleEn!.Trim();
        coupon.DescriptionAr = string.IsNullOrWhiteSpace(request.DescriptionAr) ? null : request.DescriptionAr.Trim();
        coupon.DescriptionEn = string.IsNullOrWhiteSpace(request.DescriptionEn) ? null : request.DescriptionEn.Trim();
        coupon.DiscountType = request.DiscountType!.Value;
        coupon.Value = request.Value!.Value;
        coupon.MaxDiscountEgp = request.MaxDiscountEgp;
        coupon.ValidFrom = request.ValidFrom!.Value;
        coupon.ValidTo = request.ValidTo!.Value;
        coupon.MaxRedemptions = request.MaxRedemptions;
        coupon.PerPatientLimit = request.PerPatientLimit ?? 1;
        coupon.DoctorId = request.DoctorId;
        coupon.ServiceId = request.ServiceId;
        coupon.IsPublic = request.IsPublic ?? false;
        coupon.IsActive = request.IsActive ?? true;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw ApiException.Validation("code", "This code is already in use.");
        }
    }

    private static CouponDto ToDto(Coupon c) => new(
        c.Id, c.ClinicId, c.Code, c.TitleAr, c.TitleEn, c.DescriptionAr, c.DescriptionEn, c.DiscountType, c.Value, c.MaxDiscountEgp,
        c.ValidFrom, c.ValidTo, c.MaxRedemptions, c.PerPatientLimit, c.DoctorId, c.ServiceId, c.IsPublic, c.IsActive, c.RedemptionsCount, c.CreatedAt);
}

[ApiController]
[Authorize]
[RequireFeature(FeatureKeys.Promotions)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class PromotionsController(CouponService coupons) : ControllerBase
{
    /// <summary>Public, active offers (localized by Accept-Language). Needs sign-in when guestBrowsing is off.</summary>
    [HttpGet("api/v1/promotions")]
    [AllowAnonymous]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(List<PromotionDto>), StatusCodes.Status200OK)]
    public Task<List<PromotionDto>> Offers(CancellationToken ct) => coupons.PublicOffersAsync(ct);

    /// <summary>Check a coupon for a doctor/service without booking. Answers 200 with <c>valid=false</c> and a <c>reason</c> when it cannot be used.</summary>
    [HttpPost("api/v1/coupons/validate")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(typeof(CouponValidationDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<CouponValidationDto> Validate(ValidateCouponDto request, CancellationToken ct) => coupons.ValidateAsync(request, ct);
}

[ApiController]
[Route("api/v1/dashboard/coupons")]
[Authorize(Roles = Roles.ClinicStaff)]
[RequireFeature(FeatureKeys.Promotions)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardCouponsController(CouponService coupons) : ControllerBase
{
    /// <summary>List the clinic's coupons, newest first.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<CouponDto>), StatusCodes.Status200OK)]
    public Task<List<CouponDto>> List(CancellationToken ct) => coupons.ListAsync(ct);

    /// <summary>One coupon.</summary>
    [HttpGet("{id:guid}")]
    [ProducesResponseType(typeof(CouponDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<CouponDto> Get(Guid id, CancellationToken ct) => coupons.GetAsync(id, ct);

    /// <summary>Create a coupon (<c>validFrom</c>/<c>validTo</c> are clinic-local dates, both inclusive).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(CouponDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Create(CouponInputDto request, CancellationToken ct)
    {
        var created = await coupons.CreateAsync(request, ct);
        return Created($"/api/v1/dashboard/coupons/{created.Id}", created);
    }

    /// <summary>Replace a coupon (the redemption counter is kept).</summary>
    [HttpPut("{id:guid}")]
    [ProducesResponseType(typeof(CouponDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<CouponDto> Update(Guid id, CouponInputDto request, CancellationToken ct) => coupons.UpdateAsync(id, request, ct);

    /// <summary>Delete a coupon; one that was redeemed is deactivated instead.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await coupons.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Who redeemed the coupon and for which appointment.</summary>
    [HttpGet("{id:guid}/redemptions")]
    [ProducesResponseType(typeof(List<CouponRedemptionDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<List<CouponRedemptionDto>> Redemptions(Guid id, CancellationToken ct) => coupons.RedemptionsAsync(id, ct);
}
