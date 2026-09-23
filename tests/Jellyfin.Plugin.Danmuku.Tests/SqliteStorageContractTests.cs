using Jellyfin.Plugin.Danmuku.Storage;
using MediaBrowser.Common.Configuration;
using Microsoft.Data.Sqlite;
using Xunit;
using static Jellyfin.Plugin.Danmuku.Tests.StorageTestSql;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class SqliteStorageContractTests
{
    private const int SqliteConstraintCheck = 275;
    private const int SqliteConstraintForeignKey = 787;

    private static readonly SchemaMigration ProbeMigration = new(
        SchemaMigrations.CurrentVersion + 1,
        "Test probe migration",
        "CREATE TABLE MigrationProbe (Id INTEGER PRIMARY KEY, Note TEXT NOT NULL); INSERT INTO MigrationProbe (Id, Note) VALUES (1, 'probe');");

    private static readonly SchemaMigration FailingMigration = new(
        SchemaMigrations.CurrentVersion + 1,
        "Failing test migration",
        "CREATE TABLE MigrationProbe (Id INTEGER PRIMARY KEY); INSERT INTO MissingTable (Id) VALUES (1);");

    [Fact]
    public void DataPathsUseTheDanmukuSubdirectoryOfTheJellyfinDataPath()
    {
        var dataPath = Path.Combine(Path.GetTempPath(), "danmuku-tests", Guid.NewGuid().ToString("N"), "data");
        try
        {
            var paths = new DanmukuDataPaths(new TestApplicationPaths { DataPath = dataPath });

            var root = Path.Combine(dataPath, DanmukuDataPaths.RootFolderName);
            Assert.Equal(root, paths.RootPath);
            Assert.Equal(Path.Combine(root, "danmuku.db"), paths.DatabasePath);
            Assert.Equal(Path.Combine(root, "migrations"), paths.MigrationsPath);
            Assert.Equal(Path.Combine(root, "originals"), paths.OriginalFilesPath);
            Assert.Equal(Path.Combine(root, "staging"), paths.StagingPath);
            Assert.Equal(Path.Combine(root, "playback-cache"), paths.PlaybackCachePath);

            // Never write the database next to the Jellyfin data directory root.
            Assert.NotEqual(dataPath, paths.RootPath);
            Assert.True(Directory.Exists(paths.RootPath));
        }
        finally
        {
            var parent = Path.GetDirectoryName(dataPath);
            if (parent is not null && Directory.Exists(parent))
            {
                Directory.Delete(parent, recursive: true);
            }
        }
    }

    [Fact]
    public void ConnectionFactoryAppliesRequiredPragmas()
    {
        using var storage = new StorageTestContext();
        using var connection = storage.Factory.CreateOpenConnection();

        Assert.Equal(1L, ScalarLong(connection, "PRAGMA foreign_keys;"));
        Assert.Equal("wal", ScalarString(connection, "PRAGMA journal_mode;"));
        Assert.Equal(5000L, ScalarLong(connection, "PRAGMA busy_timeout;"));
    }

    [Fact]
    public void MigrationCreatesCompleteSchemaAtCurrentVersion()
    {
        using var storage = new StorageTestContext();
        var migrator = storage.CreateMigrator();
        var result = migrator.Migrate();

        Assert.Equal(0, result.FromVersion);
        Assert.Equal(SchemaMigrations.CurrentVersion, result.ToVersion);
        Assert.Equal(SchemaMigrations.CurrentVersion, migrator.CurrentVersion);
        Assert.Null(result.BackupPath);
        Assert.Empty(Directory.GetFiles(storage.Paths.MigrationsPath));

        using var connection = storage.Factory.CreateOpenConnection();
        Assert.Equal(
            new[]
            {
                "BindingCheckJobs", "CombinePlans", "CombineSegments", "Comments", "Files", "ImportBatches", "ImportErrors", "ImportSlots",
                "ImportTasks", "MediaBindings", "MediaState", "PlaybackRequests", "SchemaVersion"
            },
            QueryNames(connection, "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
        Assert.Equal(
            new[]
            {
                "IX_BindingCheckJobs_Active", "IX_CombineSegments_FileId", "IX_Comments_File_Time", "IX_Files_ContentHash", "IX_Files_StoredFileName", "IX_ImportBatches_FinishedAt",
                "IX_ImportBatches_MediaId", "IX_ImportErrors_Task_SourceOrdinal", "IX_ImportTasks_FinishedAt",
                "IX_MediaBindings_FileId", "IX_PlaybackRequests_ExpiresAt", "IX_PlaybackRequests_SessionHash",
                "IX_PlaybackRequests_UserMedia"
            },
            QueryNames(connection, "SELECT name FROM sqlite_master WHERE type = 'index' AND name LIKE 'IX_%' ORDER BY name;"));
        Assert.Equal(
            SchemaMigrations.CurrentVersion,
            (int)ScalarLong(connection, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
        Assert.Equal(
            11L,
            ScalarLong(connection, "SELECT COUNT(*) FROM pragma_table_info('ImportTasks') WHERE name IN ('TargetFileId', 'TargetOriginalFileName', 'TargetStoredFileName', 'TargetDisplayName', 'TargetFormat', 'TargetContentHash', 'TargetLastCommentTimeMs', 'TargetParseDataVersion', 'StagedOriginalPath', 'StagedAssetPath', 'IntentCreatedAtUtcMs');"));

        var secondRun = migrator.Migrate();
        Assert.Equal(SchemaMigrations.CurrentVersion, secondRun.FromVersion);
        Assert.Null(secondRun.BackupPath);
        Assert.Empty(Directory.GetFiles(storage.Paths.MigrationsPath));
    }

    [Fact]
    public void ForeignKeysAreEnforcedOnEveryConnection()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();

        var exception = Assert.Throws<SqliteException>(() => Execute(
            connection,
            "INSERT INTO Comments (FileId, SourceOrdinal, TimeMs, Text, Color, Mode, FontSize) VALUES ('missing-file', 0, 1000, 'hello', 16777215, 1, 25);"));
        Assert.Equal(SqliteConstraintForeignKey, exception.SqliteExtendedErrorCode);

        InsertFile(connection, "file-1");
        Execute(
            connection,
            "INSERT INTO Comments (FileId, SourceOrdinal, TimeMs, Text, Color, Mode, FontSize) VALUES ('file-1', 0, 1000, 'hello', 16777215, 1, 25);");
        Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments;"));
    }

    [Fact]
    public void DeletingReferencedFileIsRejected()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();
        InsertFile(connection, "file-1");
        InsertBinding(connection, "media-1", "file-1");

        var exception = Assert.Throws<SqliteException>(() => Execute(connection, "DELETE FROM Files WHERE FileId = 'file-1';"));
        // SQLite implements ON DELETE RESTRICT through an internal trigger, so the
        // extended code is SQLITE_CONSTRAINT_TRIGGER while the primary code stays
        // SQLITE_CONSTRAINT and the message is the foreign key failure.
        Assert.Equal(19, exception.SqliteErrorCode);
        Assert.Contains("FOREIGN KEY", exception.Message, StringComparison.Ordinal);
        Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
    }

    [Fact]
    public void DeferredActiveFileReferenceAllowsLaterBindingInSameTransaction()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();
        InsertFile(connection, "file-1");

        using (var transaction = connection.BeginTransaction())
        {
            Execute(
                connection,
                transaction,
                "INSERT INTO MediaState (MediaId, ActiveFileId, IsDeactivated, Version) VALUES ('media-1', 'file-1', 0, 3);");
            InsertBinding(connection, "media-1", "file-1", transaction);
            transaction.Commit();
        }

        Assert.Equal("file-1", ScalarString(connection, "SELECT ActiveFileId FROM MediaState WHERE MediaId = 'media-1';"));
        Assert.Equal(3L, ScalarLong(connection, "SELECT Version FROM MediaState WHERE MediaId = 'media-1';"));
    }

    [Fact]
    public void DeferredActiveFileReferenceRejectsUnboundFileOnCommit()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();
        InsertFile(connection, "file-1");

        using (var transaction = connection.BeginTransaction())
        {
            Execute(
                connection,
                transaction,
                "INSERT INTO MediaState (MediaId, ActiveFileId, IsDeactivated, Version) VALUES ('media-1', 'file-1', 0, 1);");
            var exception = Assert.Throws<SqliteException>(() => transaction.Commit());
            Assert.Equal(SqliteConstraintForeignKey, exception.SqliteExtendedErrorCode);
        }

        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaState;"));
    }

    [Fact]
    public void ActiveFileMustBelongToSameMediaBinding()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();
        InsertFile(connection, "file-1");
        InsertFile(connection, "file-2", contentHash: "hash-2");
        InsertBinding(connection, "media-1", "file-1");
        InsertBinding(connection, "media-2", "file-2");

        using (var transaction = connection.BeginTransaction())
        {
            Execute(
                connection,
                transaction,
                "INSERT INTO MediaState (MediaId, ActiveFileId, IsDeactivated, Version) VALUES ('media-1', 'file-2', 0, 1);");
            var exception = Assert.Throws<SqliteException>(() => transaction.Commit());
            Assert.Equal(SqliteConstraintForeignKey, exception.SqliteExtendedErrorCode);
        }

        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaState;"));
    }

    [Fact]
    public void EmptyActiveFileMeansNoActiveFileAndDeactivationSurvivesUnbind()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();
        InsertFile(connection, "file-1");

        Execute(connection, "INSERT INTO MediaState (MediaId, ActiveFileId, IsDeactivated, Version) VALUES ('media-1', NULL, 1, 1);");
        Assert.Equal(
            1L,
            ScalarLong(connection, "SELECT COUNT(*) FROM MediaState WHERE MediaId = 'media-1' AND IsDeactivated = 1 AND ActiveFileId IS NULL;"));

        InsertBinding(connection, "media-1", "file-1");
        Execute(connection, "DELETE FROM MediaBindings WHERE MediaId = 'media-1' AND FileId = 'file-1';");

        Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaBindings WHERE MediaId = 'media-1';"));
        Assert.Equal(
            1L,
            ScalarLong(connection, "SELECT COUNT(*) FROM MediaState WHERE MediaId = 'media-1' AND IsDeactivated = 1 AND ActiveFileId IS NULL;"));
    }

    [Fact]
    public void DeactivatedMediaCannotKeepAnActiveFile()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        using var connection = storage.Factory.CreateOpenConnection();
        InsertFile(connection, "file-1");
        InsertBinding(connection, "media-1", "file-1");

        var exception = Assert.Throws<SqliteException>(() => Execute(
            connection,
            "INSERT INTO MediaState (MediaId, ActiveFileId, IsDeactivated, Version) VALUES ('media-1', 'file-1', 1, 1);"));
        Assert.Equal(SqliteConstraintCheck, exception.SqliteExtendedErrorCode);
    }

    [Fact]
    public void MigrationBacksUpExistingDatabaseBeforeUpgrade()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1");
        }

        var migrator = storage.CreateMigrator([.. SchemaMigrations.Default, ProbeMigration]);
        var result = migrator.Migrate();

        Assert.Equal(SchemaMigrations.CurrentVersion, result.FromVersion);
        Assert.Equal(SchemaMigrations.CurrentVersion + 1, result.ToVersion);
        Assert.NotNull(result.BackupPath);
        Assert.True(File.Exists(result.BackupPath));
        Assert.StartsWith(storage.Paths.MigrationsPath, result.BackupPath!, StringComparison.Ordinal);

        using (var backup = OpenReadOnly(result.BackupPath!))
        {
            Assert.Equal("ok", ScalarString(backup, "PRAGMA integrity_check;"));
            Assert.Equal(
                SchemaMigrations.CurrentVersion,
                (int)ScalarLong(backup, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
            Assert.Equal(1L, ScalarLong(backup, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
        }

        using var current = storage.Factory.CreateOpenConnection();
        Assert.Equal(SchemaMigrations.CurrentVersion + 1L, ScalarLong(current, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
        Assert.Equal("probe", ScalarString(current, "SELECT Note FROM MigrationProbe WHERE Id = 1;"));
        Assert.Equal(1L, ScalarLong(current, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
    }

    [Fact]
    public void FailedMigrationKeepsOriginalDatabaseAndVerifiedBackup()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1");
        }

        var migrator = storage.CreateMigrator([.. SchemaMigrations.Default, FailingMigration]);

        Assert.Throws<SqliteException>(() => migrator.Migrate());

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(
                SchemaMigrations.CurrentVersion,
                (int)ScalarLong(connection, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM sqlite_master WHERE name = 'MigrationProbe';"));
        }

        Assert.True(new FileInfo(storage.Paths.DatabasePath).Length > 0);

        var backupPath = Assert.Single(Directory.GetFiles(storage.Paths.MigrationsPath, "*.db"));
        using var backup = OpenReadOnly(backupPath);
        Assert.Equal("ok", ScalarString(backup, "PRAGMA integrity_check;"));
        Assert.Equal(
            SchemaMigrations.CurrentVersion,
            (int)ScalarLong(backup, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
        Assert.Equal(1L, ScalarLong(backup, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
    }

    [Fact]
    public async Task WriteCoordinatorSerializesConcurrentWrites()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Execute(connection, "CREATE TABLE WriteProbe (Id INTEGER NOT NULL PRIMARY KEY);");
        }

        await using var coordinator = new SqliteWriteCoordinator(storage.Factory);

        var active = 0;
        var maxActive = 0;
        var sequence = new List<string>();

        var writes = Enumerable.Range(0, 8)
            .Select(index => coordinator.EnqueueAsync(async (connection, token) =>
            {
                var current = Interlocked.Increment(ref active);
                maxActive = Math.Max(maxActive, current);
                sequence.Add($"start{index}");
                await Task.Delay(20, token);
                Execute(connection, "INSERT INTO WriteProbe (Id) VALUES ($id);", ("$id", index));
                sequence.Add($"end{index}");
                Interlocked.Decrement(ref active);
            }))
            .ToArray();

        await Task.WhenAll(writes);

        Assert.Equal(1, maxActive);
        Assert.Equal(8, sequence.Count(entry => entry.StartsWith("start", StringComparison.Ordinal)));
        for (var i = 0; i < sequence.Count; i += 2)
        {
            var start = sequence[i];
            var end = sequence[i + 1];
            Assert.StartsWith("start", start, StringComparison.Ordinal);
            Assert.StartsWith("end", end, StringComparison.Ordinal);
            Assert.Equal(start[5..], end[3..]);
        }

        using var verify = storage.Factory.CreateOpenConnection();
        Assert.Equal(8L, ScalarLong(verify, "SELECT COUNT(*) FROM WriteProbe;"));
    }

    private sealed class TestApplicationPaths : IApplicationPaths
    {
        public string DataPath { get; set; } = string.Empty;

        public string ProgramDataPath => string.Empty;

        public string WebPath => string.Empty;

        public string ProgramSystemPath => string.Empty;

        public string ImageCachePath => string.Empty;

        public string PluginsPath => string.Empty;

        public string PluginConfigurationsPath => string.Empty;

        public string LogDirectoryPath => string.Empty;

        public string ConfigurationDirectoryPath => string.Empty;

        public string SystemConfigurationFilePath => string.Empty;

        public string CachePath => string.Empty;

        public string TempDirectory => string.Empty;

        public string VirtualDataPath => string.Empty;

        public string TrickplayPath => string.Empty;

        public string BackupPath => string.Empty;

        public void MakeSanityCheckOrThrow()
        {
        }

        public void CreateAndCheckMarker(string path, string markerName, bool recursive)
        {
        }
    }
}
