using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Danmuku.Configuration;
using Jellyfin.Plugin.Danmuku.Playback;
using Jellyfin.Plugin.Danmuku.Storage;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Danmuku.Api;

public sealed class JellyfinPlaybackSessions(IDeviceManager devices) : IPlaybackSessionLookup
{
    public Task<bool> IsActiveAsync(string userId, string sessionHash)
    {
        var result = devices.GetDevices(new Jellyfin.Data.Queries.DeviceQuery { UserId = Guid.Parse(userId) });
        return Task.FromResult(result.Items.Any(d => PlaybackService.SessionHash(d.AccessToken) == sessionHash));
    }
}

[ApiController, Route("Danmuku/Playback"), Authorize, DanmukuErrors]
public sealed class PlaybackController(IAuthorizationContext authorization, ILibraryManager library,
    IDeviceManager devices, PlaybackService playback, PluginConfiguration configuration) : ControllerBase
{
    [HttpGet("{itemId}")]
    public async Task<IActionResult> Get(Guid itemId, string playbackId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        var auth = await authorization.GetAuthorizationInfo(HttpContext).ConfigureAwait(false);
        if (auth.User is null || auth.IsApiKey || string.IsNullOrEmpty(auth.Token)) return Unauthorized();
        var user = auth.User;
        // Validate the persisted authentication device, not a WebSocket presence or client-supplied user id.
        var active = devices.GetDevices(new Jellyfin.Data.Queries.DeviceQuery { AccessToken = auth.Token });
        if (!active.Items.Any(d => d.UserId == user.Id)) return Unauthorized();
        if (user.HasPermission(PermissionKind.IsDisabled) || !user.HasPermission(PermissionKind.EnableMediaPlayback)) return Forbid();
        var item = library.GetItemById(itemId);
        if (item is not (Movie or Episode) || !item.IsVisibleStandalone(user) || !item.IsParentalAllowed(user, false)) return Forbid();
        var identity = new PlaybackIdentity(user.Id.ToString("N"), PlaybackService.SessionHash(auth.Token), itemId.ToString("N"), item.RunTimeTicks is > 0 ? item.RunTimeTicks / TimeSpan.TicksPerMillisecond : null);
        var stream = await playback.GetAsync(playbackId, identity, configuration, ct).ConfigureAwait(false);
        return File(stream, "application/json");
    }
}
