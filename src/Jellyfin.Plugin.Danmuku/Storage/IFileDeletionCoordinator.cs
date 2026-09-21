namespace Jellyfin.Plugin.Danmuku.Storage;

public enum FileDeletionMarkStatus
{
    Marked,
    AlreadyMarked,
    Referenced,
    NotFound
}

public sealed record FileDeletionMarkResult(FileDeletionMarkStatus Status, long BindingCount);

public sealed record FileDeletionCleanupReport(int Deleted, int Failed);

/// <summary>
/// Coordinates file deletion: the database row is marked first, the disk file is removed
/// by a retryable cleanup routine, and only then is the record removed.
/// </summary>
public interface IFileDeletionCoordinator
{
    Task<FileDeletionMarkResult> MarkForDeletionAsync(string fileId, CancellationToken cancellationToken = default);

    Task<FileDeletionCleanupReport> CleanupAsync(CancellationToken cancellationToken = default);
}
