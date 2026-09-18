using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Danmuku.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string InstanceLabel { get; set; } = "Danmuku";

    /// <summary>
    /// Gets or sets a value indicating whether Jellyfin Web danmuku support is enabled.
    /// Defaults to <see langword="false"/> so the first Web exposure stays off until an
    /// administrator enables it.
    /// </summary>
    public bool EnableWebSupport { get; set; }
}
