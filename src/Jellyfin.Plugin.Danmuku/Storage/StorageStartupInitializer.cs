using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Runs the Danmuku storage migration and recovery scan when the host starts.
/// The plugin-bundled SQLite native library is verified first; when it is unavailable the
/// storage initialization is aborted (no database is created) instead of continuing on the
/// Server-provided library. Failures are logged and swallowed so a storage problem never
/// prevents the server from starting; existing data is left untouched for a later retry.
/// </summary>
public sealed class StorageStartupInitializer : IHostedService
{
    private readonly ISqliteSchemaMigrator _migrator;
    private readonly IStorageRecoveryService _recoveryService;
    private readonly IFileDeletionCoordinator _deletionCoordinator;
    private readonly ISqliteNativeLibraryProbe _nativeLibraryProbe;
    private readonly ILogger<StorageStartupInitializer> _logger;
    private readonly StorageInitializationState? _state;

    public StorageStartupInitializer(
        ISqliteSchemaMigrator migrator,
        IStorageRecoveryService recoveryService,
        IFileDeletionCoordinator deletionCoordinator,
        ISqliteNativeLibraryProbe nativeLibraryProbe,
        ILogger<StorageStartupInitializer> logger, StorageInitializationState? state = null)
    {
        _state = state;
        _migrator = migrator ?? throw new ArgumentNullException(nameof(migrator));
        _recoveryService = recoveryService ?? throw new ArgumentNullException(nameof(recoveryService));
        _deletionCoordinator = deletionCoordinator ?? throw new ArgumentNullException(nameof(deletionCoordinator));
        _nativeLibraryProbe = nativeLibraryProbe ?? throw new ArgumentNullException(nameof(nativeLibraryProbe));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var succeeded = false;
        try
        {
            string nativeLibraryPath;
            try
            {
                nativeLibraryPath = _nativeLibraryProbe.EnsureAvailable();
            }
            catch (SqliteNativeLibraryUnavailableException exception)
            {
                _logger.LogError(
                    exception,
                    "Danmuku storage initialization aborted before migration: the plugin-bundled SQLite native library is required and the Server-provided library is not used.");
                return;
            }

            _logger.LogInformation(
                "Danmuku native SQLite resolved from its own package: {NativeLibraryPath}",
                nativeLibraryPath);

            var migration = _migrator.Migrate();
            if (migration.BackupPath is not null)
            {
                _logger.LogInformation(
                    "Danmuku database migrated from schema version {FromVersion} to {ToVersion}; consistency backup: {BackupPath}",
                    migration.FromVersion,
                    migration.ToVersion,
                    migration.BackupPath);
            }
            else
            {
                _logger.LogInformation("Danmuku database is at schema version {Version}", migration.ToVersion);
            }

            var report = await _recoveryService.RunAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Danmuku storage recovery finished: interruptedTasks={InterruptedTasks}, interruptedSlots={InterruptedSlots}, finishedBatches={FinishedBatches}, cleanedStaging={CleanedStagingFiles}, cleanedOriginals={CleanedOrphanedOriginals}, retriedCleanups={RetriedCleanups}, cleanupFailures={CleanupFailures}, inconsistentTasks={InconsistentTasks}",
                report.InterruptedTasks,
                report.InterruptedSlots,
                report.FinishedBatches,
                report.CleanedStagingFiles,
                report.CleanedOrphanedOriginals,
                report.RetriedCleanups,
                report.CleanupFailures,
                report.InconsistentTasks);

            // Retry deletions that failed before this restart.
            var cleanup = await _deletionCoordinator.CleanupAsync(cancellationToken).ConfigureAwait(false);
            if (cleanup.Deleted > 0 || cleanup.Failed > 0)
            {
                _logger.LogInformation(
                    "Danmuku deletion cleanup finished: deleted={Deleted}, failed={Failed}",
                    cleanup.Deleted,
                    cleanup.Failed);
            }
            succeeded = true;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Danmuku storage initialization failed; the plugin continues without storage and existing data is kept.");
        }
        finally { _state?.Complete(succeeded); }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
