using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Outcome of a schema migration run.
/// </summary>
/// <param name="FromVersion">Schema version found before migrating.</param>
/// <param name="ToVersion">Schema version after migrating.</param>
/// <param name="BackupPath">Verified consistency backup path, or <see langword="null"/> for a fresh database.</param>
public sealed record SqliteMigrationResult(int FromVersion, int ToVersion, string? BackupPath);

/// <summary>
/// Creates and upgrades the Danmuku SQLite schema.
/// </summary>
public interface ISqliteSchemaMigrator
{
    /// <summary>Gets the schema version this instance expects.</summary>
    int CurrentVersion { get; }

    /// <summary>
    /// Ensures the database exists at the current version. Upgrading an existing database
    /// always creates and verifies a consistency backup first; a failed migration keeps the
    /// backup and the original database untouched.
    /// </summary>
    SqliteMigrationResult Migrate();
}

/// <summary>
/// Applies versioned schema migrations with pre-migration consistency backups.
/// </summary>
public sealed class SqliteSchemaMigrator : ISqliteSchemaMigrator
{
    private const string SchemaVersionTable = "SchemaVersion";

    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly DanmukuDataPaths _paths;
    private readonly IReadOnlyList<SchemaMigration> _migrations;

    public SqliteSchemaMigrator(ISqliteConnectionFactory connectionFactory, DanmukuDataPaths paths)
        : this(connectionFactory, paths, SchemaMigrations.Default)
    {
    }

    /// <summary>
    /// Initializes a new instance with an explicit migration list. Production uses
    /// <see cref="SchemaMigrations.Default"/>; the overload exists so tests can drive
    /// upgrade and failure scenarios deterministically.
    /// </summary>
    public SqliteSchemaMigrator(
        ISqliteConnectionFactory connectionFactory,
        DanmukuDataPaths paths,
        IReadOnlyList<SchemaMigration> migrations)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ArgumentNullException.ThrowIfNull(migrations);

        var ordered = migrations.OrderBy(migration => migration.Version).ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Version != index + 1)
            {
                throw new ArgumentException(
                    "Schema migrations must be contiguous and start at version 1.",
                    nameof(migrations));
            }
        }

        _migrations = ordered;
    }

    public int CurrentVersion => _migrations.Count == 0 ? 0 : _migrations[^1].Version;

    public SqliteMigrationResult Migrate()
    {
        _paths.EnsureCreated();

        using var connection = _connectionFactory.CreateOpenConnection();
        var fromVersion = ReadSchemaVersion(connection);
        var hasExistingTables = HasAnyUserTables(connection);
        var targetVersion = CurrentVersion;

        if (!hasExistingTables && fromVersion == 0)
        {
            ApplyMigrations(connection, 0, targetVersion);
            return new SqliteMigrationResult(0, targetVersion, null);
        }

        if (fromVersion > targetVersion)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"The Danmuku database schema version {fromVersion} is newer than the version {targetVersion} supported by this plugin."));
        }

        if (fromVersion == targetVersion)
        {
            return new SqliteMigrationResult(fromVersion, targetVersion, null);
        }

        var backupPath = CreateVerifiedBackup(connection, fromVersion);
        ApplyMigrations(connection, fromVersion, targetVersion);
        return new SqliteMigrationResult(fromVersion, targetVersion, backupPath);
    }

    private static int ReadSchemaVersion(SqliteConnection connection)
    {
        if (!TableExists(connection, SchemaVersionTable))
        {
            return 0;
        }

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaVersion WHERE Id = 1;";
        var value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = $name;";
        command.Parameters.AddWithValue("$name", tableName);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static bool HasAnyUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture) > 0;
    }

    private static long CountUserTables(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%';";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private void ApplyMigrations(SqliteConnection connection, int fromVersion, int targetVersion)
    {
        foreach (var migration in _migrations.Where(migration => migration.Version > fromVersion && migration.Version <= targetVersion))
        {
            using var transaction = connection.BeginTransaction();

            using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = migration.Sql;
                command.ExecuteNonQuery();
            }

            using (var versionCommand = connection.CreateCommand())
            {
                versionCommand.Transaction = transaction;
                versionCommand.CommandText = """
                    INSERT INTO SchemaVersion (Id, Version, AppliedAtUtcMs)
                    VALUES (1, $version, $appliedAt)
                    ON CONFLICT (Id) DO UPDATE SET Version = excluded.Version, AppliedAtUtcMs = excluded.AppliedAtUtcMs;
                    """;
                versionCommand.Parameters.AddWithValue("$version", migration.Version);
                versionCommand.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
                versionCommand.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    private string CreateVerifiedBackup(SqliteConnection source, int fromVersion)
    {
        var fileName = FormattableString.Invariant(
            $"danmuku-v{fromVersion}-{DateTime.UtcNow:yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.db");
        var backupPath = Path.Combine(_paths.MigrationsPath, fileName);

        using (var destination = OpenRaw(backupPath))
        {
            source.BackupDatabase(destination);
        }

        VerifyBackup(source, backupPath, fromVersion);
        return backupPath;
    }

    private static SqliteConnection OpenRaw(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static void VerifyBackup(SqliteConnection source, string backupPath, int fromVersion)
    {
        using var backup = OpenRaw(backupPath);

        using (var integrityCommand = backup.CreateCommand())
        {
            integrityCommand.CommandText = "PRAGMA integrity_check;";
            var integrity = integrityCommand.ExecuteScalar() as string;
            if (!string.Equals(integrity, "ok", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    FormattableString.Invariant($"Danmuku migration backup '{backupPath}' failed the SQLite integrity check."));
            }
        }

        if (CountUserTables(backup) != CountUserTables(source))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Danmuku migration backup '{backupPath}' does not contain the same table count as the source database."));
        }

        if (fromVersion > 0 && ReadSchemaVersion(backup) != fromVersion)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"Danmuku migration backup '{backupPath}' does not report schema version {fromVersion}."));
        }
    }
}
