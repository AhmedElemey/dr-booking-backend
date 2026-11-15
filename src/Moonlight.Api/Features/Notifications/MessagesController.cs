using System.Net.Mime;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;
using Moonlight.Api.Features.Dashboard;
using Moonlight.Api.Features.Flags;
using Moonlight.Api.Infrastructure;

namespace Moonlight.Api.Features.Notifications;

public sealed record OutboundMessageDto(
    Guid Id,
    MessageChannel Channel,
    string To,
    Guid? UserId,
    string Kind,
    string Body,
    MessageStatus Status,
    string? ProviderRef,
    string? Error,
    DateTime CreatedAt);

[ApiController]
[Route("api/v1/dashboard/messages")]
[Authorize(Roles = Roles.ClinicStaff + "," + Roles.PlatformAdmin)]
[RequireFeature(FeatureKeys.WhatsappReminders)]
[Produces(MediaTypeNames.Application.Json)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status401Unauthorized)]
[ProducesResponseType(typeof(ApiProblem), StatusCodes.Status403Forbidden)]
public class DashboardMessagesController(MoonlightDbContext db, CurrentUser current, DashboardScope scope) : ControllerBase
{
    /// <summary>Outbox: every push, SMS and WhatsApp message the system sent, newest first. Staff see their clinic's messages, the platform admin sees all. <c>from</c>/<c>to</c> are clinic-local dates.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<OutboundMessageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ApiProblem), StatusCodes.Status400BadRequest)]
    public async Task<PagedResult<OutboundMessageDto>> List(MessageChannel? channel, DateOnly? from, DateOnly? to, int? page, int? pageSize, CancellationToken ct)
    {
        var request = PageRequest.Create(page, pageSize);
        var query = db.OutboundMessages.AsNoTracking();
        var timezone = Clinic.DefaultTimezone;
        if (current.Role == UserRole.ClinicStaff)
        {
            var clinic = await scope.RequireClinicAsync(ct);
            timezone = clinic.Timezone;
            query = query.Where(m => m.ClinicId == clinic.Id);
        }

        var zone = ClinicTime.FindZone(timezone);
        if (channel is { } c)
        {
            query = query.Where(m => m.Channel == c);
        }

        if (from is { } f)
        {
            var start = ClinicTime.StartOfDayUtc(f, zone);
            query = query.Where(m => m.CreatedAt >= start);
        }

        if (to is { } t)
        {
            var end = ClinicTime.StartOfDayUtc(t.AddDays(1), zone);
            query = query.Where(m => m.CreatedAt < end);
        }

        return await query.OrderByDescending(m => m.CreatedAt).ThenBy(m => m.Id).ToPagedAsync(
            request,
            rows => Task.FromResult<IReadOnlyList<OutboundMessageDto>>(rows.Select(m => new OutboundMessageDto(
                m.Id, m.Channel, m.To, m.UserId, m.Kind, m.Body, m.Status, m.ProviderRef, m.Error, m.CreatedAt)).ToList()),
            ct);
    }
}
