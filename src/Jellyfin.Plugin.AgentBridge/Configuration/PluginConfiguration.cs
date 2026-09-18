using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.AgentBridge.Configuration;

public sealed class PluginConfiguration : BasePluginConfiguration
{
    public string InstanceLabel { get; set; } = "AgentBridge";
}
