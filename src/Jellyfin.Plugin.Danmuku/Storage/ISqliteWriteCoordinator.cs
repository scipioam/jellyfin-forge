using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Serializes all Danmuku database writes through a single background writer, so
/// publish and media state changes never interleave on the SQLite file.
/// </summary>
public interface ISqliteWriteCoordinator : IAsyncDisposable
{
    /// <summary>Queues a write operation and returns its result.</summary>
    Task<TResult> EnqueueAsync<TResult>(
        Func<SqliteConnection, CancellationToken, Task<TResult>> work,
        CancellationToken cancellationToken = default);

    /// <summary>Queues a write operation.</summary>
    Task EnqueueAsync(
        Func<SqliteConnection, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
