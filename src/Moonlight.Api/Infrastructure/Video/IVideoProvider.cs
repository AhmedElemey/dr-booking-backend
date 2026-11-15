using System.Security.Cryptography;

using Microsoft.Extensions.Options;

using Moonlight.Api.Common;
using Moonlight.Api.Domain;

namespace Moonlight.Api.Infrastructure.Video;

public sealed record VideoRoom(string RoomName, string PatientUrl, string HostUrl);

public interface IVideoProvider
{
    Task<VideoRoom> CreateRoomAsync(Appointment appointment, CancellationToken ct = default);
}

// Jitsi needs no API call: an unguessable room name on the configured Jitsi server is the room.
public sealed class JitsiVideoProvider(IOptions<VideoOptions> options) : IVideoProvider
{
    public Task<VideoRoom> CreateRoomAsync(Appointment appointment, CancellationToken ct = default)
    {
        var baseUrl = options.Value.Jitsi.BaseUrl.TrimEnd('/');
        var room = "moonlight-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        return Task.FromResult(new VideoRoom(room, $"{baseUrl}/{room}", $"{baseUrl}/{room}#config.prejoinPageEnabled=false"));
    }
}

// Deterministic fake room for development and tests.
public sealed class MockVideoProvider : IVideoProvider
{
    public Task<VideoRoom> CreateRoomAsync(Appointment appointment, CancellationToken ct = default)
    {
        var room = "mock-room-" + appointment.Id.ToString("N");
        return Task.FromResult(new VideoRoom(room, $"https://video.mock.invalid/{room}", $"https://video.mock.invalid/{room}?host=1"));
    }
}
