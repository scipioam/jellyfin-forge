using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Creates connection-ready SQLite connections for the Danmuku database.
/// </summary>
public interface ISqliteConnectionFactory
{
    string DatabasePath { get; }

    /// <summary>
    /// Opens a connection with the per-connection pragmas required by the design:
    /// foreign keys on, WAL journal mode and a five second busy timeout.
    /// </summary>
    SqliteConnection CreateOpenConnection();
}
