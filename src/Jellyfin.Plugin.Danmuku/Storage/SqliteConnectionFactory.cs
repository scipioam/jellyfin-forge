using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="ISqliteConnectionFactory" />
public sealed class SqliteConnectionFactory : ISqliteConnectionFactory
{
    private const int BusyTimeoutMilliseconds = 5000;

    private readonly string _connectionString;
    private readonly ISqliteNativeLibraryProbe _nativeLibraryProbe;

    static SqliteConnectionFactory()
    {
        // Safety net for the module initializer: the resolver must be attached before the
        // first SQLitePCLRaw provider call, which cannot happen before a factory exists.
        SqliteNativeLibraryResolver.EnsureRegistered();
    }

    public SqliteConnectionFactory(DanmukuDataPaths paths)
        : this(paths?.DatabasePath ?? throw new ArgumentNullException(nameof(paths)))
    {
    }

    public SqliteConnectionFactory(string databasePath)
        : this(databasePath, new SqliteNativeLibraryProbe())
    {
    }

    public SqliteConnectionFactory(DanmukuDataPaths paths, ISqliteNativeLibraryProbe nativeLibraryProbe)
        : this(paths?.DatabasePath ?? throw new ArgumentNullException(nameof(paths)), nativeLibraryProbe)
    {
    }

    public SqliteConnectionFactory(string databasePath, ISqliteNativeLibraryProbe nativeLibraryProbe)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _nativeLibraryProbe = nativeLibraryProbe ?? throw new ArgumentNullException(nameof(nativeLibraryProbe));
        DatabasePath = Path.GetFullPath(databasePath);

        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            Pooling = true
        }.ToString();
    }

    public string DatabasePath { get; }

    public SqliteConnection CreateOpenConnection()
    {
        // Fail fast with the same error as the startup probe: a connection must never be
        // served by the Server-provided SQLite library.
        _nativeLibraryProbe.EnsureAvailable();

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode = WAL;";
        command.ExecuteNonQuery();
        command.CommandText = $"PRAGMA busy_timeout = {BusyTimeoutMilliseconds};";
        command.ExecuteNonQuery();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();

        return connection;
    }
}
