using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Catalog;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Reviews;

public sealed record RatingDto(double Average, int Count);

public sealed record ReviewRefDto(int Rating, string? Comment);

public sealed record SubmitReviewDto(int? Rating, string? Comment);

public sealed record PublicReviewDto(int Rating, string? Comment, string? PatientFirstName, DateTime CreatedAt, string? Reply);

public sealed record DashboardReviewDto(
    Guid Id,
    Guid AppointmentId,
    DashboardDoctorRefDto Doctor,
    string? PatientName,
    int Rating,
    string? Comment,
    ReviewStatus Status,
    string? Reply,
    DateTime? RepliedAt,
    DateTime CreatedAt);

public sealed record ReplyDto(string? Reply);

public sealed class ReviewService(MoonlightDbContext db, CurrentUser current, DashboardScope scope, Language lang, TimeProvider clock, FeatureService features)
{
    public async Task<ReviewRefDto> SubmitAsync(Guid appointmentId, SubmitReviewDto request, CancellationToken ct)
    {
        new ValidationErrors()
            .Require(request.Rating is >= 1 and <= 5, "rating", "rating must be between 1 and 5.")
            .RequireText(request.Comment, "comment", 1000, required: false)
            .ThrowIfAny();

        var userId = current.Id;
        var appointment = await db.Appointments.Include(a => a.Slot)
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.PatientUserId == userId, ct) ?? throw ApiException.NotFound();
        if (appointment.Status != AppointmentStatus.Completed)
        {
            throw new ApiException(ErrorCodes.NotReviewable);
        }

        if (await db.Reviews.AnyAsync(r => r.AppointmentId == appointmentId, ct))
        {
            throw new ApiException(ErrorCodes.AlreadyReviewed);
        }

        var review = new Review
        {
            AppointmentId = appointmentId,
            DoctorId = appointment.Slot!.DoctorId,
            PatientUserId = userId,
            Rating = request.Rating!.Value,
            Comment = string.IsNullOrWhiteSpace(request.Comment) ? null : request.Comment.Trim(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Reviews.Add(review);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new ApiException(ErrorCodes.AlreadyReviewed);
        }

        return new ReviewRefDto(review.Rating, review.Comment);
    }

    public async Task<PagedResult<PublicReviewDto>> PublicListAsync(string idOrSlug, PageRequest page, CancellationToken ct)
    {
        var doctors = CatalogService.Visible(db).AsNoTracking();
        var doctorId = Guid.TryParse(idOrSlug, out var id)
            ? await doctors.Where(d => d.Id == id).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct)
            : await doctors.Where(d => d.Slug == idOrSlug).Select(d => (Guid?)d.Id).FirstOrDefaultAsync(ct);
        if (doctorId is not { } doctor)
        {
            throw ApiException.NotFound();
        }

        return await db.Reviews.AsNoTracking().Include(r => r.Patient)
            .Where(r => r.DoctorId == doctor && r.Status == ReviewStatus.Published)
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToPagedAsync(page, rows => Task.FromResult<IReadOnlyList<PublicReviewDto>>(
                rows.Select(r => new PublicReviewDto(r.Rating, r.Comment, FirstName(r.Patient?.FullName), r.CreatedAt, r.Reply)).ToList()), ct);
    }

    /// <summary>Published-review averages per doctor; empty while the reviews feature is off. Doctors without reviews are absent.</summary>
    public async Task<Dictionary<Guid, RatingDto>> RatingsAsync(IReadOnlyCollection<Guid> doctorIds, CancellationToken ct)
    {
        if (!features.IsEnabled(FeatureKeys.Reviews) || doctorIds.Count == 0)
        {
            return [];
        }

        var rows = await db.Reviews.AsNoTracking()
            .Where(r => doctorIds.Contains(r.DoctorId) && r.Status == ReviewStatus.Published)
            .GroupBy(r => r.DoctorId)
            .Select(g => new { DoctorId = g.Key, Average = g.Average(r => (double)r.Rating), Count = g.Count() })
            .ToListAsync(ct);
        return rows.ToDictionary(r => r.DoctorId, r => new RatingDto(Math.Round(r.Average, 2), r.Count));
    }

    public async Task<PagedResult<DashboardReviewDto>> DashboardListAsync(Guid? doctorId, ReviewStatus? status, PageRequest page, CancellationToken ct)
    {
        var doctorIds = scope.Doctors().Select(d => d.Id);
        var query = db.Reviews.AsNoTracking().Include(r => r.Doctor).Include(r => r.Patient).Where(r => doctorIds.Contains(r.DoctorId));
        if (doctorId is { } d)
        {
            query = query.Where(r => r.DoctorId == d);
        }

        if (status is { } s)
        {
            query = query.Where(r => r.Status == s);
        }

        return await query.OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id)
            .ToPagedAsync(page, rows => Task.FromResult<IReadOnlyList<DashboardReviewDto>>(rows.Select(ToDashboard).ToList()), ct);
    }

    public Task<DashboardReviewDto> SetStatusAsync(Guid id, ReviewStatus status, CancellationToken ct) =>
        ModifyAsync(id, r => r.Status = status, ct);

    public Task<DashboardReviewDto> ReplyAsync(Guid id, ReplyDto request, CancellationToken ct)
    {
        new ValidationErrors().RequireText(request.Reply, "reply", 1000).ThrowIfAny();
        var now = clock.GetUtcNow().UtcDateTime;
        return ModifyAsync(id, r =>
        {
            r.Reply = request.Reply!.Trim();
            r.RepliedAt = now;
        }, ct);
    }

    private async Task<DashboardReviewDto> ModifyAsync(Guid id, Action<Review> change, CancellationToken ct)
    {
        var doctorIds = scope.Doctors().Select(d => d.Id);
        var review = await db.Reviews.Include(r => r.Doctor).Include(r => r.Patient)
            .FirstOrDefaultAsync(r => r.Id == id && doctorIds.Contains(r.DoctorId), ct) ?? throw ApiException.NotFound();
        change(review);
        await db.SaveChangesAsync(ct);
        return ToDashboard(review);
    }

    private DashboardReviewDto ToDashboard(Review r) => new(
        r.Id,
        r.AppointmentId,
        new DashboardDoctorRefDto(r.DoctorId, lang.Pick(r.Doctor!.NameAr, r.Doctor.NameEn)),
        r.Patient?.FullName,
        r.Rating,
        r.Comment,
        r.Status,
        r.Reply,
        r.RepliedAt,
        r.CreatedAt);

    private static string? FirstName(string? fullName) =>
        string.IsNullOrWhiteSpace(fullName) ? null : fullName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
}

[ApiController]
[Authorize]
[RequireFeature(FeatureKeys.Reviews)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class ReviewsController(ReviewService reviews) : ControllerBase
{
    /// <summary>Rate a completed visit (once). NOT_REVIEWABLE unless the appointment is Completed, ALREADY_REVIEWED on a second attempt.</summary>
    [HttpPost("api/v1/appointments/{id:guid}/review")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(typeof(ReviewRefDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Submit(Guid id, SubmitReviewDto request, CancellationToken ct) =>
        Created($"/api/v1/appointments/{id}", await reviews.SubmitAsync(id, request, ct));

    /// <summary>Published reviews of a doctor, newest first. Needs sign-in when guestBrowsing is off.</summary>
    [HttpGet("api/v1/doctors/{idOrSlug}/reviews")]
    [AllowAnonymous]
    [RequireSignInUnlessGuestBrowsing]
    [ProducesResponseType(typeof(PagedResult<PublicReviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<PagedResult<PublicReviewDto>> List(string idOrSlug, int? page, int? pageSize, CancellationToken ct) =>
        reviews.PublicListAsync(idOrSlug, PageRequest.Create(page, pageSize), ct);
}

[ApiController]
[Route("api/v1/dashboard/reviews")]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.Reviews)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
public class DashboardReviewsController(ReviewService reviews) : ControllerBase
{
    /// <summary>Reviews of the doctors in scope, newest first, any status unless filtered.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<DashboardReviewDto>), StatusCodes.Status200OK)]
    public Task<PagedResult<DashboardReviewDto>> List(Guid? doctorId, ReviewStatus? status, int? page, int? pageSize, CancellationToken ct) =>
        reviews.DashboardListAsync(doctorId, status, PageRequest.Create(page, pageSize), ct);

    /// <summary>Hide a review from the public list.</summary>
    [HttpPost("{id:guid}/hide")]
    [ProducesResponseType(typeof(DashboardReviewDto), StatusCodes.Status200OK)]
    public Task<DashboardReviewDto> Hide(Guid id, CancellationToken ct) => reviews.SetStatusAsync(id, ReviewStatus.Hidden, ct);

    /// <summary>Publish a hidden review again.</summary>
    [HttpPost("{id:guid}/publish")]
    [ProducesResponseType(typeof(DashboardReviewDto), StatusCodes.Status200OK)]
    public Task<DashboardReviewDto> Publish(Guid id, CancellationToken ct) => reviews.SetStatusAsync(id, ReviewStatus.Published, ct);

    /// <summary>Reply publicly to a review.</summary>
    [HttpPut("{id:guid}/reply")]
    [ProducesResponseType(typeof(DashboardReviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public Task<DashboardReviewDto> Reply(Guid id, ReplyDto request, CancellationToken ct) => reviews.ReplyAsync(id, request, ct);
}
