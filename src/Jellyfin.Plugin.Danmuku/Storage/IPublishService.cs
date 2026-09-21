namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Registers publish intents and atomically publishes staged imports.
/// All database writes go through <see cref="ISqliteWriteCoordinator"/>.
/// </summary>
public interface IPublishService
{
    /// <summary>
    /// Persists the intent for a task before the original file is promoted to the
    /// originals directory. Re-registering the same intent is idempotent.
    /// </summary>
    Task<PublishIntentSnapshot> RegisterIntentAsync(
        PublishIntentRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Promotes the staged original (an external operation not rolled back by SQLite) and commits the file,
    /// comments, binding, active selection and task completion in a single transaction.
    /// </summary>
    Task<PublishOutcome> PublishAsync(
        string taskId,
        IAsyncEnumerable<CommentRecord> comments,
        CancellationToken cancellationToken = default);

    /// <summary>Publishes a task that has no comment payload (for example a pure binding change).</summary>
    Task<PublishOutcome> PublishAsync(string taskId, CancellationToken cancellationToken = default);
}
