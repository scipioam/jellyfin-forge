using Jellyfin.Plugin.Danmuku.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Danmuku;

public sealed class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    public const string PluginId = "6f79690c-c1d0-4738-b241-09aaa2c570e7";

    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
    }

    public override string Name => "Danmuku";
    public override Guid Id => Guid.Parse(PluginId);
    public override string Description => "Independent danmuku management for Jellyfin.";

    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        ((PluginConfiguration)configuration).Validate();
        base.UpdateConfiguration(configuration);
    }

    public IEnumerable<PluginPageInfo> GetPages() =>
    [
        new PluginPageInfo
        {
            Name = "Danmuku",
            EmbeddedResourcePath = "Jellyfin.Plugin.Danmuku.Configuration.configPage.html"
        }
    ];
}
