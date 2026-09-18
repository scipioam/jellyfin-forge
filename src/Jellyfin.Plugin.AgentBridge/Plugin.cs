using Jellyfin.Plugin.AgentBridge.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.AgentBridge;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginId = "8c8f013d-21b4-467c-8dac-c6446b97c0ea";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "AgentBridge";
    public override Guid Id => Guid.Parse(PluginId);
    public override string Description => "Stable Jellyfin contracts for external agents; no AI inference.";

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "AgentBridge",
            EmbeddedResourcePath = "Jellyfin.Plugin.AgentBridge.Configuration.configPage.html"
        }
    ];
}
