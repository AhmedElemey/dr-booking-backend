using System.Net.Mime;
using System.Text.RegularExpressions;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Appointments;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Family;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Storage;

namespace Moonlight.Api.Features.Records;

public sealed record PatientListItemDto(Guid Id, string? FullName, string Phone, DateTime? LastVisitAt, int AppointmentsCount);

public sealed record MedicalInfoDto(Guid? FamilyMemberId, string? BloodType, string? Allergies, string? ChronicConditions, string? Medications, DateTime UpdatedAt);

public sealed record MedicalInfoInputDto(Guid? FamilyMemberId, string? BloodType, string? Allergies, string? ChronicConditions, string? Medications);

public sealed record RecordDto(
    Guid Id,
    Guid PatientUserId,
    Guid? FamilyMemberId,
    Guid? AppointmentId,
    Guid? DoctorId,
    Guid AuthorUserId,
    string? AuthorName,
    RecordKind Kind,
    string Title,
    string? Body,
    string? AttachmentUrl,
    bool VisibleToPatient,
    DateTime CreatedAt,
    DateTime UpdatedAt);

public sealed record RecordInputDto(
    Guid? FamilyMemberId,
    Guid? AppointmentId,
    RecordKind? Kind,
    string? Title,
    string? Body,
    bool? VisibleToPatient);

public sealed record PatientDetailDto(
    PatientRefDto Patient,
    IReadOnlyList<FamilyMemberDto> FamilyMembers,
    MedicalInfoDto? MedicalInfo,
    IReadOnlyList<DashboardAppointmentDto> Appointments,
    IReadOnlyList<RecordDto> Records);

public sealed record PatientRecordDto(Guid Id, Guid? FamilyMemberId, RecordKind Kind, string Title, string? Body, string? AttachmentUrl, string? DoctorName, DateTime CreatedAt);

public sealed record MyRecordsDto(IReadOnlyList<PatientRecordDto> Entries, MedicalInfoDto? MedicalInfo);

public sealed record AttachmentUrlDto(string AttachmentUrl);

public sealed record AttachmentFileResult(Stream Content, string ContentType, string FileName);

public sealed partial class RecordsService(
    MoonlightDbContext db,
    CurrentUser current,
    DashboardScope scope,
    AppointmentMapper mapper,
    Language lang,
    IRecordFileStorage storage,
    IOptions<StorageOptions> storageOptions,
    TimeProvider clock)
{
    public const int MaxAttachmentBytes = 10 * 1024 * 1024;

    [GeneratedRegex("^(A|B|AB|O)[+-]$")]
    private static partial Regex BloodTypePattern();

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    // ---- Dashboard: patients -------------------------------------------------------------------------------------

    public async Task<(List<PatientListItemDto> Items, int Total)> ListPatientsAsync(string? search, PageRequest page, CancellationToken ct)
    {
        var appointments = scope.Appointments().AsNoTracking();
        var users = db.Users.AsNoTracking().Where(u => appointments.Any(a => a.PatientUserId == u.Id));
        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = "%" + search.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            users = users.Where(u => EF.Functions.ILike(u.Phone, pattern) || (u.FullName != null && EF.Functions.ILike(u.FullName, pattern)));
        }

        var total = await users.CountAsync(ct);
        var rows = await users
            .Select(u => new
            {
                u.Id,
                u.FullName,
                u.Phone,
                LastVisitAt = appointments.Where(a => a.PatientUserId == u.Id && a.Status == AppointmentStatus.Completed).Max(a => (DateTime?)a.Slot!.StartsAt),
                Count = appointments.Count(a => a.PatientUserId == u.Id && a.Status != AppointmentStatus.Cancelled),
            })
            .OrderByDescending(x => x.LastVisitAt).ThenBy(x => x.FullName).ThenBy(x => x.Id)
            .Skip(page.Skip).Take(page.PageSize)
            .ToListAsync(ct);
        return (rows.Select(x => new PatientListItemDto(x.Id, x.FullName, x.Phone, x.LastVisitAt, x.Count)).ToList(), total);
    }

    public async Task<PatientDetailDto> GetPatientAsync(Guid userId, Guid? familyMemberId, CancellationToken ct)
    {
        var patient = await RequirePatientAsync(userId, ct);
        if (familyMemberId is { } member)
        {
            await RequireMemberAsync(userId, member, ct);
        }

        var members = await db.FamilyMembers.AsNoTracking().Where(f => f.OwnerUserId == userId).OrderBy(f => f.CreatedAt).ToListAsync(ct);
        var info = await db.PatientMedicalInfos.AsNoTracking().FirstOrDefaultAsync(m => m.PatientUserId == userId && m.FamilyMemberId == familyMemberId, ct);

        var appointmentQuery = AppointmentMapper.WithDetails(scope.Appointments().AsNoTracking()).Where(a => a.PatientUserId == userId);
        if (familyMemberId is not null)
        {
            appointmentQuery = appointmentQuery.Where(a => a.FamilyMemberId == familyMemberId);
        }

        var appointments = await appointmentQuery.OrderByDescending(a => a.Slot!.StartsAt).Take(200).ToListAsync(ct);
        var records = await RecordQuery().Where(r => r.PatientUserId == userId && (familyMemberId == null || r.FamilyMemberId == familyMemberId))
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);

        return new PatientDetailDto(
            new PatientRefDto(patient.Id, patient.FullName, patient.Phone),
            members.Select(m => new FamilyMemberDto(m.Id, m.FullName, m.Relation, m.DateOfBirth, m.Gender, m.CreatedAt)).ToList(),
            info is null ? null : ToDto(info),
            appointments.Select(mapper.ToDashboardDto).ToList(),
            await ToRecordDtosAsync(records, ct));
    }

    public async Task<MedicalInfoDto> SaveMedicalInfoAsync(Guid userId, MedicalInfoInputDto request, CancellationToken ct)
    {
        var errors = new ValidationErrors()
            .RequireText(request.Allergies, "allergies", 2000, required: false)
            .RequireText(request.ChronicConditions, "chronicConditions", 2000, required: false)
            .RequireText(request.Medications, "medications", 2000, required: false)
            .Require(string.IsNullOrWhiteSpace(request.BloodType) || BloodTypePattern().IsMatch(request.BloodType.Trim().ToUpperInvariant()), "bloodType", "bloodType must be one of A+, A-, B+, B-, AB+, AB-, O+, O-.");
        errors.ThrowIfAny();
        await RequirePatientAsync(userId, ct);
        if (request.FamilyMemberId is { } member)
        {
            await RequireMemberAsync(userId, member, ct);
        }

        var info = await db.PatientMedicalInfos.FirstOrDefaultAsync(m => m.PatientUserId == userId && m.FamilyMemberId == request.FamilyMemberId, ct);
        if (info is null)
        {
            info = new PatientMedicalInfo { PatientUserId = userId, FamilyMemberId = request.FamilyMemberId };
            db.PatientMedicalInfos.Add(info);
        }

        info.BloodType = string.IsNullOrWhiteSpace(request.BloodType) ? null : request.BloodType.Trim().ToUpperInvariant();
        info.Allergies = Clean(request.Allergies);
        info.ChronicConditions = Clean(request.ChronicConditions);
        info.Medications = Clean(request.Medications);
        info.UpdatedAt = Now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.IsUniqueViolation())
        {
            throw new ApiException(ErrorCodes.InvalidTransition, "The medical info was changed by someone else. Reload and try again.");
        }

        return ToDto(info);
    }

    // ---- Dashboard: record entries -------------------------------------------------------------------------------

    public async Task<RecordDto> CreateRecordAsync(Guid userId, RecordInputDto request, CancellationToken ct)
    {
        ValidateRecord(request);
        await RequirePatientAsync(userId, ct);
        if (request.FamilyMemberId is { } member)
        {
            await RequireMemberAsync(userId, member, ct);
        }

        Guid? doctorId = scope.IsDoctor ? current.DoctorId : null;
        if (request.AppointmentId is { } appointmentId)
        {
            var appointment = await scope.Appointments().AsNoTracking()
                .Where(a => a.Id == appointmentId && a.PatientUserId == userId)
                .Select(a => new { a.Slot!.DoctorId }).FirstOrDefaultAsync(ct)
                ?? throw ApiException.Validation("appointmentId", "The appointment does not belong to this patient.");
            doctorId ??= appointment.DoctorId;
        }

        var now = Now;
        var entry = new PatientRecordEntry
        {
            PatientUserId = userId,
            FamilyMemberId = request.FamilyMemberId,
            AppointmentId = request.AppointmentId,
            DoctorId = doctorId,
            ClinicId = scope.ClinicId,
            AuthorUserId = current.Id,
            Kind = request.Kind!.Value,
            Title = request.Title!.Trim(),
            Body = Clean(request.Body),
            VisibleToPatient = request.VisibleToPatient ?? false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.PatientRecordEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        return (await ToRecordDtosAsync([entry], ct))[0];
    }

    public async Task<RecordDto> UpdateRecordAsync(Guid id, RecordInputDto request, CancellationToken ct)
    {
        ValidateRecord(request);
        var entry = await LoadEditableAsync(id, ct);
        entry.Kind = request.Kind!.Value;
        entry.Title = request.Title!.Trim();
        entry.Body = Clean(request.Body);
        entry.VisibleToPatient = request.VisibleToPatient ?? entry.VisibleToPatient;
        entry.UpdatedAt = Now;
        await db.SaveChangesAsync(ct);
        return (await ToRecordDtosAsync([entry], ct))[0];
    }

    public async Task DeleteRecordAsync(Guid id, CancellationToken ct)
    {
        var entry = await LoadEditableAsync(id, ct);
        var file = entry.AttachmentFile;
        db.PatientRecordEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        if (file is not null)
        {
            storage.Delete(file);
        }
    }

    public async Task<string> UploadAttachmentAsync(Guid id, IFormFile? file, CancellationToken ct)
    {
        var entry = await LoadEditableAsync(id, ct);
        if (file is null || file.Length == 0)
        {
            throw ApiException.Validation("file", "A pdf, jpg or png file is required.");
        }

        if (file.Length > MaxAttachmentBytes)
        {
            throw ApiException.Validation("file", "The file must be 10 MB or smaller.");
        }

        await using var stream = file.OpenReadStream();
        var header = new byte[8];
        var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, ct);
        var extension = Detect(header.AsSpan(0, read)) ?? throw ApiException.Validation("file", "Only pdf, jpg and png files are accepted.");
        stream.Position = 0;

        var previous = entry.AttachmentFile;
        var key = await storage.SaveAsync($"{entry.Id:N}-{Guid.NewGuid():N}.{extension}", stream, ct);
        entry.AttachmentFile = key;
        entry.AttachmentContentType = ContentTypeOf(extension);
        entry.UpdatedAt = Now;
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch
        {
            storage.Delete(key);
            throw;
        }

        if (previous is not null)
        {
            storage.Delete(previous);
        }

        return DashboardUrl(entry.Id);
    }

    // The attachment of an entry in the caller's clinic, for staff and for doctors who have the patient in scope.
    public async Task<AttachmentFileResult> OpenDashboardAttachmentAsync(Guid id, CancellationToken ct)
    {
        var entry = await RecordQuery().FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw ApiException.NotFound();
        if (!await scope.Appointments().AnyAsync(a => a.PatientUserId == entry.PatientUserId, ct))
        {
            throw ApiException.NotFound();
        }

        return Open(entry);
    }

    // The patient's own entry, only when the clinic shared it.
    public async Task<AttachmentFileResult> OpenMineAttachmentAsync(Guid id, CancellationToken ct)
    {
        var userId = current.Id;
        var entry = await db.PatientRecordEntries.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == id && r.PatientUserId == userId && r.VisibleToPatient, ct) ?? throw ApiException.NotFound();
        return Open(entry);
    }

    private AttachmentFileResult Open(PatientRecordEntry entry)
    {
        var stream = entry.AttachmentFile is null ? null : storage.Open(entry.AttachmentFile);
        return stream is null
            ? throw ApiException.NotFound()
            : new AttachmentFileResult(stream, entry.AttachmentContentType ?? "application/octet-stream", entry.AttachmentFile!);
    }

    private string DashboardUrl(Guid id) => Absolute($"/api/v1/dashboard/records/{id}/attachment");

    private string PatientUrl(Guid id) => Absolute($"/api/v1/me/records/{id}/attachment");

    // Relative unless a public base URL is configured: the request's Host header is never trusted.
    private string Absolute(string path) => storageOptions.Value.PublicBaseUrl.TrimEnd('/') + path;

    private static string ContentTypeOf(string extension) => extension switch
    {
        "pdf" => "application/pdf",
        "jpg" => "image/jpeg",
        _ => "image/png",
    };

    // ---- Patient ---------------------------------------------------------------------------------------------------

    public async Task<MyRecordsDto> MineAsync(Guid? familyMemberId, CancellationToken ct)
    {
        var userId = current.Id;
        if (familyMemberId is { } member && !await db.FamilyMembers.AnyAsync(f => f.Id == member && f.OwnerUserId == userId && f.DeletedAt == null, ct))
        {
            throw ApiException.NotFound();
        }

        var entries = await db.PatientRecordEntries.AsNoTracking()
            .Where(r => r.PatientUserId == userId && r.VisibleToPatient && (familyMemberId == null || r.FamilyMemberId == familyMemberId))
            .OrderByDescending(r => r.CreatedAt).ThenBy(r => r.Id).ToListAsync(ct);
        var doctorIds = entries.Where(e => e.DoctorId != null).Select(e => e.DoctorId!.Value).Distinct().ToList();
        var doctors = await db.Doctors.AsNoTracking().Where(d => doctorIds.Contains(d.Id)).ToDictionaryAsync(d => d.Id, ct);
        var info = await db.PatientMedicalInfos.AsNoTracking().FirstOrDefaultAsync(m => m.PatientUserId == userId && m.FamilyMemberId == familyMemberId, ct);
        return new MyRecordsDto(
            entries.Select(e => new PatientRecordDto(
                e.Id,
                e.FamilyMemberId,
                e.Kind,
                e.Title,
                e.Body,
                e.AttachmentFile is null ? null : PatientUrl(e.Id),
                e.DoctorId is { } d && doctors.TryGetValue(d, out var doctor) ? lang.Pick(doctor.NameAr, doctor.NameEn) : null,
                e.CreatedAt)).ToList(),
            info is null ? null : ToDto(info));
    }

    // ---- Helpers ---------------------------------------------------------------------------------------------------

    private IQueryable<PatientRecordEntry> RecordQuery()
    {
        var clinicId = scope.ClinicId;
        return db.PatientRecordEntries.AsNoTracking().Where(r => r.ClinicId == clinicId);
    }

    // The author or clinic staff may change an entry; other doctors may read it only.
    private async Task<PatientRecordEntry> LoadEditableAsync(Guid id, CancellationToken ct)
    {
        var clinicId = scope.ClinicId;
        var entry = await db.PatientRecordEntries.FirstOrDefaultAsync(r => r.Id == id && r.ClinicId == clinicId, ct) ?? throw ApiException.NotFound();
        if (scope.IsDoctor && entry.AuthorUserId != current.Id)
        {
            throw new ApiException(ErrorCodes.Forbidden);
        }

        return entry;
    }

    private async Task<User> RequirePatientAsync(Guid userId, CancellationToken ct)
    {
        var visible = await scope.Appointments().AnyAsync(a => a.PatientUserId == userId, ct);
        return visible
            ? await db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, ct)
            : throw ApiException.NotFound();
    }

    private async Task RequireMemberAsync(Guid userId, Guid memberId, CancellationToken ct)
    {
        if (!await db.FamilyMembers.AnyAsync(f => f.Id == memberId && f.OwnerUserId == userId, ct))
        {
            throw ApiException.Validation("familyMemberId", "The family member does not belong to this patient.");
        }
    }

    private async Task<List<RecordDto>> ToRecordDtosAsync(IReadOnlyCollection<PatientRecordEntry> entries, CancellationToken ct)
    {
        var authorIds = entries.Select(e => e.AuthorUserId).Distinct().ToList();
        var authors = await db.Users.AsNoTracking().Where(u => authorIds.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.FullName, ct);
        return entries.Select(e => new RecordDto(
            e.Id, e.PatientUserId, e.FamilyMemberId, e.AppointmentId, e.DoctorId, e.AuthorUserId, authors.GetValueOrDefault(e.AuthorUserId),
            e.Kind, e.Title, e.Body, e.AttachmentFile is null ? null : DashboardUrl(e.Id), e.VisibleToPatient, e.CreatedAt, e.UpdatedAt)).ToList();
    }

    private static void ValidateRecord(RecordInputDto request) =>
        new ValidationErrors()
            .Require(request.Kind is not null, "kind", "kind must be Note, Diagnosis, Prescription or Attachment.")
            .RequireText(request.Title, "title", 200)
            .RequireText(request.Body, "body", 8000, required: false)
            .ThrowIfAny();

    private static MedicalInfoDto ToDto(PatientMedicalInfo m) => new(m.FamilyMemberId, m.BloodType, m.Allergies, m.ChronicConditions, m.Medications, m.UpdatedAt);

    private static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    // Sniffs the file signature rather than trusting the client's content type.
    private static string? Detect(ReadOnlySpan<byte> h)
    {
        if (h.Length >= 4 && h[0] == 0x25 && h[1] == 0x50 && h[2] == 0x44 && h[3] == 0x46)
        {
            return "pdf";
        }

        if (h.Length >= 3 && h[0] == 0xFF && h[1] == 0xD8 && h[2] == 0xFF)
        {
            return "jpg";
        }

        return h.Length >= 8 && h[0] == 0x89 && h[1] == 0x50 && h[2] == 0x4E && h[3] == 0x47 && h[4] == 0x0D && h[5] == 0x0A && h[6] == 0x1A && h[7] == 0x0A
            ? "png"
            : null;
    }
}

[ApiController]
[Authorize(Roles = Roles.Dashboard)]
[RequireFeature(FeatureKeys.PatientRecords)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardPatientsController(RecordsService records) : ControllerBase
{
    /// <summary>Patients with an appointment in the caller's scope. The total is in the <c>X-Total-Count</c> header.</summary>
    [HttpGet("api/v1/dashboard/patients")]
    [ProducesResponseType(typeof(List<PatientListItemDto>), StatusCodes.Status200OK)]
    public async Task<List<PatientListItemDto>> List(string? search, int? page, int? pageSize, CancellationToken ct)
    {
        var (items, total) = await records.ListPatientsAsync(search, PageRequest.Create(page, pageSize), ct);
        Response.Headers["X-Total-Count"] = total.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return items;
    }

    /// <summary>Patient overview: family members, medical info, appointments and record entries. <c>familyMemberId</c> scopes it to one family member.</summary>
    [HttpGet("api/v1/dashboard/patients/{userId:guid}")]
    [ProducesResponseType(typeof(PatientDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<PatientDetailDto> Get(Guid userId, Guid? familyMemberId, CancellationToken ct) => records.GetPatientAsync(userId, familyMemberId, ct);

    /// <summary>Create or replace the medical info of the patient (or of the family member in the body).</summary>
    [HttpPut("api/v1/dashboard/patients/{userId:guid}/medical-info")]
    [ProducesResponseType(typeof(MedicalInfoDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<MedicalInfoDto> SaveMedicalInfo(Guid userId, MedicalInfoInputDto request, CancellationToken ct) => records.SaveMedicalInfoAsync(userId, request, ct);

    /// <summary>Add a record entry (note, diagnosis, prescription, attachment). Hidden from the patient unless <c>visibleToPatient</c>.</summary>
    [HttpPost("api/v1/dashboard/patients/{userId:guid}/records")]
    [ProducesResponseType(typeof(RecordDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateRecord(Guid userId, RecordInputDto request, CancellationToken ct)
    {
        var created = await records.CreateRecordAsync(userId, request, ct);
        return Created($"/api/v1/dashboard/records/{created.Id}", created);
    }

    /// <summary>Edit an entry (its author, or staff).</summary>
    [HttpPut("api/v1/dashboard/records/{id:guid}")]
    [ProducesResponseType(typeof(RecordDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<RecordDto> UpdateRecord(Guid id, RecordInputDto request, CancellationToken ct) => records.UpdateRecordAsync(id, request, ct);

    /// <summary>Delete an entry (its author, or staff).</summary>
    [HttpDelete("api/v1/dashboard/records/{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteRecord(Guid id, CancellationToken ct)
    {
        await records.DeleteRecordAsync(id, ct);
        return NoContent();
    }

    /// <summary>Attach a file (multipart field <c>file</c>; pdf, jpg or png, at most 10 MB) to an entry.</summary>
    [HttpPut("api/v1/dashboard/records/{id:guid}/attachment")]
    [Consumes("multipart/form-data")]
    [ProducesResponseType(typeof(AttachmentUrlDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<AttachmentUrlDto> UploadAttachment(Guid id, IFormFile? file, CancellationToken ct) =>
        new(await records.UploadAttachmentAsync(id, file, ct));

    /// <summary>Download the attachment (needs the bearer token; staff, or a doctor who has the patient in scope). The previous file is deleted when it is replaced.</summary>
    [HttpGet("api/v1/dashboard/records/{id:guid}/attachment")]
    [Produces("application/pdf", "image/jpeg", "image/png")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(Guid id, CancellationToken ct) => AttachmentResponse.Create(this, await records.OpenDashboardAttachmentAsync(id, ct));
}

[ApiController]
[Route("api/v1/me/records")]
[Authorize(Roles = Roles.Patient)]
[RequireFeature(FeatureKeys.PatientRecords)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class MyRecordsController(RecordsService records) : ControllerBase
{
    /// <summary>Entries the clinic shared with me (<c>visibleToPatient</c>) plus my own medical info. <c>familyMemberId</c> scopes it to a family member.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(MyRecordsDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public Task<MyRecordsDto> Mine(Guid? familyMemberId, CancellationToken ct) => records.MineAsync(familyMemberId, ct);

    /// <summary>Download the attachment of an entry the clinic shared with me (needs the bearer token).</summary>
    [HttpGet("{id:guid}/attachment")]
    [Produces("application/pdf", "image/jpeg", "image/png")]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DownloadAttachment(Guid id, CancellationToken ct) => AttachmentResponse.Create(this, await records.OpenMineAttachmentAsync(id, ct));
}

internal static class AttachmentResponse
{
    public static IActionResult Create(ControllerBase controller, AttachmentFileResult file)
    {
        controller.Response.Headers.CacheControl = "private, no-store";
        controller.Response.Headers.XContentTypeOptions = "nosniff";
        return controller.File(file.Content, file.ContentType);
    }
}
