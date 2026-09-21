using Jellyfin.Plugin.Danmuku.Configuration;
using Jellyfin.Plugin.Danmuku.Storage;
using MediaBrowser.Common.Configuration;
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

        serviceCollection.AddSingleton(static services =>
            new DanmukuDataPaths(services.GetRequiredService<IApplicationPaths>()));
        serviceCollection.AddSingleton<ISqliteNativeLibraryProbe, SqliteNativeLibraryProbe>();
        serviceCollection.AddSingleton<ISqliteConnectionFactory>(static services =>
            new SqliteConnectionFactory(
                services.GetRequiredService<DanmukuDataPaths>(),
                services.GetRequiredService<ISqliteNativeLibraryProbe>()));
        serviceCollection.AddSingleton<ISqliteSchemaMigrator>(static services =>
            new SqliteSchemaMigrator(
                services.GetRequiredService<ISqliteConnectionFactory>(),
                services.GetRequiredService<DanmukuDataPaths>()));
        serviceCollection.AddSingleton<ISqliteWriteCoordinator, SqliteWriteCoordinator>();
        serviceCollection.AddSingleton<IPublishFileStore>(static services =>
            new PublishFileStore(services.GetRequiredService<DanmukuDataPaths>()));
        serviceCollection.AddSingleton<IPublishService, PublishService>();
        serviceCollection.AddSingleton<IFileDeletionCoordinator, FileDeletionCoordinator>();
        serviceCollection.AddSingleton<IStorageRecoveryService, StorageRecoveryService>();

        // Migrates and recovers the Danmuku database when the Jellyfin host starts.
        serviceCollection.AddHostedService<StorageStartupInitializer>();
    }

    private static Plugin GetPlugin(IServiceProvider services) =>
        services.GetRequiredService<IPluginManager>().Plugins
            .Select(static localPlugin => localPlugin.Instance)
            .OfType<Plugin>()
            .Single();
}
