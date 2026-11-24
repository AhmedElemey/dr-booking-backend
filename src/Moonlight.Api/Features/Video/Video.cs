using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;
using Moonlight.Api.Infrastructure.Video;

namespace Moonlight.Api.Features.Video;

public sealed record VideoJoinDto(string JoinUrl, DateTime OpensAt, DateTime ClosesAt);

public sealed class VideoService(MoonlightDbContext db, CurrentUser current, DashboardScope scope, IVideoProvider provider, TimeProvider clock)
{
    public static readonly TimeSpan OpensBefore = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ClosesAfter = TimeSpan.FromMinutes(30);

    public async Task<VideoJoinDto> PatientJoinAsync(Guid appointmentId, CancellationToken ct)
    {
        var userId = current.Id;
        var appointment = await db.Appointments.Include(a => a.Slot)
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.PatientUserId == userId, ct) ?? throw ApiException.NotFound();
        var room = await EnsureRoomAsync(appointment, ct);
        return new VideoJoinDto(room.PatientUrl, Opens(appointment), Closes(appointment));
    }

    public async Task<VideoJoinDto> DashboardJoinAsync(Guid appointmentId, CancellationToken ct)
    {
        var appointment = await scope.Appointments().Include(a => a.Slot).FirstOrDefaultAsync(a => a.Id == appointmentId, ct) ?? throw ApiException.NotFound();
        var room = await EnsureRoomAsync(appointment, ct);
        return new VideoJoinDto(room.HostUrl, Opens(appointment), Closes(appointment));
    }

    private static DateTime Opens(Appointment a) => a.Slot!.StartsAt - OpensBefore;

    private static DateTime Closes(Appointment a) => a.Slot!.EndsAt + ClosesAfter;

    // The room is created on the first join inside the window; concurrent first joins converge on one room.
    private async Task<VideoRoom> EnsureRoomAsync(Appointment appointment, CancellationToken ct)
    {
        if (appointment.Mode != AppointmentMode.Video)
        {
            throw ApiException.NotFound();
        }

        var now = clock.GetUtcNow().UtcDateTime;
        // A visit already with the doctor (InProgress) always opens its video link, whatever the slot clock says.
        var open = appointment.Status == AppointmentStatus.InProgress
            || (appointment.Status is AppointmentStatus.Confirmed or AppointmentStatus.Completed && now >= Opens(appointment) && now <= Closes(appointment));
        if (!open)
        {
            throw new ApiException(
                ErrorCodes.VideoNotOpen,
                extensions: new Dictionary<string, object?> { ["opensAt"] = Opens(appointment), ["closesAt"] = Closes(appointment) });
        }

        if (appointment.VideoRoomName is null)
        {
            var created = await provider.CreateRoomAsync(appointment, ct);
            var id = appointment.Id;
            await db.Appointments.Where(a => a.Id == id && a.VideoRoomName == null)
                .ExecuteUpdateAsync(u => u
                    .SetProperty(a => a.VideoRoomName, created.RoomName)
                    .SetProperty(a => a.VideoPatientUrl, created.PatientUrl)
                    .SetProperty(a => a.VideoHostUrl, created.HostUrl), ct);
            var stored = await db.Appointments.AsNoTracking().Where(a => a.Id == id)
                .Select(a => new VideoRoom(a.VideoRoomName!, a.VideoPatientUrl!, a.VideoHostUrl!)).FirstAsync(ct);
            return stored;
        }

        return new VideoRoom(appointment.VideoRoomName, appointment.VideoPatientUrl!, appointment.VideoHostUrl!);
    }
}

[ApiController]
[Authorize]
[RequireFeature(FeatureKeys.VideoConsultation)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class VideoController(VideoService video) : ControllerBase
{
    /// <summary>Join URL of a video appointment, from 15 minutes before it starts to 30 minutes after it ends (VIDEO_NOT_OPEN otherwise). The room is created on the first join.</summary>
    [HttpGet("api/v1/appointments/{id:guid}/video")]
    [Authorize(Roles = Roles.Patient)]
    [ProducesResponseType(typeof(VideoJoinDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<VideoJoinDto> PatientJoin(Guid id, CancellationToken ct) => video.PatientJoinAsync(id, ct);

    /// <summary>Host join URL for the doctor / staff, same window.</summary>
    [HttpGet("api/v1/dashboard/appointments/{id:guid}/video")]
    [Authorize(Roles = Roles.Dashboard)]
    [ProducesResponseType(typeof(VideoJoinDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status409Conflict)]
    public Task<VideoJoinDto> DashboardJoin(Guid id, CancellationToken ct) => video.DashboardJoinAsync(id, ct);
}
