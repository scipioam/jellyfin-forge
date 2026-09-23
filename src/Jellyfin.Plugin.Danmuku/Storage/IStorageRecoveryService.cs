namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Outcome of a startup recovery scan.
/// </summary>
public sealed record StorageRecoveryReport(
    int InterruptedTasks,
    int InterruptedSlots,
    int FinishedBatches,
    int CleanedStagingFiles,
    int CleanedOrphanedOriginals,
    int RetriedCleanups,
    int CleanupFailures,
    int InconsistentTasks);

/// <summary>
/// Reconciles interrupted import work with the persisted transaction results on startup.
/// </summary>
public interface IStorageRecoveryService
{
    Task<StorageRecoveryReport> RunAsync(CancellationToken cancellationToken = default);
}
