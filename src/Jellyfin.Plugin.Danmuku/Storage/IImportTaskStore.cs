namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Import task persistence for abnormal entry details and task counters. All writes run through
/// the single SQLite write coordinator; reads open their own connection.
/// </summary>
public interface IImportTaskStore
{
    /// <summary>
    /// Appends abnormal entry details for one task in a single transaction.
    /// </summary>
    /// <returns>The number of appended rows; zero for an empty list.</returns>
    Task<long> AppendErrorsAsync(
        string taskId,
        IReadOnlyList<ImportErrorRecord> errors,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads abnormal entry details ordered by <c>(SourceOrdinal, ErrorId)</c>.
    /// </summary>
    Task<IReadOnlyList<PersistedImportError>> ReadErrorsAsync(
        string taskId,
        long offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Updates the provided task counters and stage values with a parameterized statement.
    /// </summary>
    /// <returns>True when the task exists and was updated.</returns>
    Task<bool> UpdateCountersAsync(
        string taskId,
        ImportTaskCounterUpdate update,
        CancellationToken cancellationToken = default);
}
