using System.Reflection;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.Danmuku.Configuration;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Danmuku.Api;

[ApiController]
[Route("Danmuku/Web")]
public sealed class WebResourceController : ControllerBase
{
    /// <summary>
    /// Static version of the fixed web resources. Bump it whenever the embedded
    /// bootstrap/danmuku assets change so clients revalidate cached copies.
    /// </summary>
    public const string ResourceVersion = "m1-v2";

    public const string BootstrapResourceName = "Jellyfin.Plugin.Danmuku.Web.bootstrap.js";
    public const string ScriptResourceName = "Jellyfin.Plugin.Danmuku.Web.danmuku.js";
    public const string StylesheetResourceName = "Jellyfin.Plugin.Danmuku.Web.danmuku.css";

    private const string JavaScriptContentType = "text/javascript";
    private const string StylesheetContentType = "text/css";
    private const string CacheControlHeaderValue = "public, max-age=3600";

    private static readonly Assembly PluginAssembly = typeof(Plugin).Assembly;

    private readonly PluginConfiguration _configuration;

    public WebResourceController(PluginConfiguration configuration)
    {
        _configuration = configuration;
    }

    /// <summary>
    /// Anonymous status endpoint. The response is the entire public whitelist:
    /// a single enabled flag plus the fixed resource version, no other data.
    /// </summary>
    [HttpGet("Status")]
    [HttpGet("BootstrapState")]
    [AllowAnonymous]
    public ActionResult<WebSupportStatus> GetStatus() =>
        new WebSupportStatus(_configuration.EnableWebSupport, ResourceVersion);

    [HttpGet("Bootstrap.js")]
    [AllowAnonymous]
    public ActionResult GetBootstrap() => GetEmbeddedResource(BootstrapResourceName, JavaScriptContentType);

    [HttpGet("Danmuku.js")]
    [AllowAnonymous]
    public ActionResult GetScript() => GetEmbeddedResource(ScriptResourceName, JavaScriptContentType);

    [HttpGet("Danmuku.css")]
    [AllowAnonymous]
    public ActionResult GetStylesheet() => GetEmbeddedResource(StylesheetResourceName, StylesheetContentType);

    [HttpGet("Assets/{assetName}")]
    [AllowAnonymous]
    public ActionResult Asset(string assetName) => assetName switch
    {
        "bootstrap.js" => GetBootstrap(),
        "danmuku.js" => GetScript(),
        "danmuku.css" => GetStylesheet(),
        _ => NotFound()
    };

    private ActionResult GetEmbeddedResource(string resourceName, string contentType)
    {
        var stream = PluginAssembly.GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            return NotFound();
        }

        Response.Headers.CacheControl = CacheControlHeaderValue;
        Response.Headers.ETag = $"\"{ResourceVersion}\"";
        return new FileStreamResult(stream, contentType);
    }
}

/// <summary>
/// Public web support status. Keep this contract limited to the two fields the
/// bootstrap script needs.
/// </summary>
public sealed record WebSupportStatus(
    [property: JsonPropertyName("enabled")] bool Enabled,
    [property: JsonPropertyName("resourceVersion")] string ResourceVersion);
