using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Danmuku.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string InstanceLabel { get; set; } = "Danmuku";
}
