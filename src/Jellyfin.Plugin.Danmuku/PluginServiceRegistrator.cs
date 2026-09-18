using Jellyfin.Plugin.Danmuku.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Danmuku;

/// <summary>
/// Registers Danmuku services with the Jellyfin DI container.
/// </summary>
public sealed class PluginServiceRegistrator : IPluginServiceRegistrator
{
    /// <inheritdoc />
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        // Resolve the current configuration per request: saving the plugin
        // configuration replaces the configuration instance on the plugin.
        serviceCollection.AddTransient(static services => GetPlugin(services).Configuration);
    }

    private static Plugin GetPlugin(IServiceProvider services) =>
        services.GetRequiredService<IPluginManager>().Plugins
            .Select(static localPlugin => localPlugin.Instance)
            .OfType<Plugin>()
            .Single();
}
