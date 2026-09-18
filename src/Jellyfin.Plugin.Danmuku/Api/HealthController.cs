using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Danmuku.Api;

[ApiController]
[Route("Danmuku/Health")]
[Authorize(Policy = Policies.RequiresElevation)]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public ActionResult<HealthResponse> Get() => new HealthResponse(
        "Danmuku", "ok", typeof(Plugin).Assembly.GetName().Version!.ToString());
}

public sealed record HealthResponse(string Plugin, string Status, string Version);
