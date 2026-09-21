using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Xunit;
using static Jellyfin.Plugin.Danmuku.Tests.StorageTestSql;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class PublishRecoveryContractTests
{
    private static readonly SchemaMigration FailingMigration = new(
        3,
        "Failing test migration",
        "CREATE TABLE MigrationProbe (Id INTEGER PRIMARY KEY); INSERT INTO MissingTable (Id) VALUES (1);");

    [Fact]
    public async Task PublishCommitsFileCommentsBindingAndCompletionAtomically()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        var staged = StageOriginal(storage, "sample.xml");
        await services.Publish.RegisterIntentAsync(CreateIntent("task-1", "media-1", "file-1", "sample.xml", staged));

        var outcome = await services.Publish.PublishAsync("task-1", Comments(3));

        Assert.Equal(PublishOutcomeKind.Published, outcome.Kind);
        Assert.True(outcome.BindingCreated);
        Assert.True(outcome.ActiveFileChanged);
        Assert.Equal(3, outcome.ImportedComments);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(3L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments WHERE FileId = 'file-1';"));
            Assert.Equal(3L, ScalarLong(connection, "SELECT CommentCount FROM Files WHERE FileId = 'file-1';"));
            Assert.Equal(3L, ScalarLong(connection, "SELECT ImportedComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("file-1", ScalarString(connection, "SELECT ActiveFileId FROM MediaState WHERE MediaId = 'media-1';"));
            Assert.Equal("Completed", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("Completed", ScalarString(connection, "SELECT Status FROM ImportSlots WHERE BatchId = 'batch-1' AND Slot = 0;"));
            Assert.Equal("Finished", ScalarString(connection, "SELECT Status FROM ImportBatches WHERE BatchId = 'batch-1';"));
            Assert.False(ScalarIsNull(connection, "SELECT FinishedAtUtcMs FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("file-1", ScalarString(connection, "SELECT FileId FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.True(ScalarIsNull(connection, "SELECT TargetFileId FROM ImportTasks WHERE TaskId = 'task-1';"));
        }

        Assert.True(File.Exists(Path.Combine(storage.Paths.OriginalFilesPath, "sample.xml")));
        Assert.False(File.Exists(Path.Combine(storage.Paths.StagingPath, "sample.xml")));

        var republish = await services.Publish.PublishAsync("task-1");
        Assert.Equal(PublishOutcomeKind.AlreadyPublished, republish.Kind);
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(3L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments WHERE FileId = 'file-1';"));
        }
    }

    [Fact]
    public async Task UncommittedPublishIsInterruptedAndCleanedAfterRestart()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        await using (var services = storage.CreateServices())
        {
            using (var connection = storage.Factory.CreateOpenConnection())
            {
                InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
            }

            var staged = StageOriginal(storage, "sample.xml");
            await services.Publish.RegisterIntentAsync(CreateIntent("task-1", "media-1", "file-1", "sample.xml", staged));

            // Crash after the external file move but before the publish transaction.
            services.FileStore.MoveStagedOriginalToOriginals(staged, "sample.xml");
        }

        using var reopened = storage.Reopen();
        await using var recoveryServices = reopened.CreateServices();
        var report = await recoveryServices.Recovery.RunAsync();

        Assert.Equal(1, report.InterruptedTasks);
        Assert.Equal(1, report.InterruptedSlots);
        Assert.Equal(1, report.FinishedBatches);
        Assert.Equal(1, report.CleanedOrphanedOriginals);
        Assert.Equal(0, report.CleanupFailures);
        Assert.Equal(0, report.InconsistentTasks);

        using (var connection = reopened.Factory.CreateOpenConnection())
        {
            Assert.Equal("Interrupted", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.False(ScalarIsNull(connection, "SELECT FinishedAtUtcMs FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("Interrupted", ScalarString(connection, "SELECT Status FROM ImportSlots WHERE BatchId = 'batch-1' AND Slot = 0;"));
            Assert.Equal("Finished", ScalarString(connection, "SELECT Status FROM ImportBatches WHERE BatchId = 'batch-1';"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Files;"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments;"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaBindings;"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaState;"));
        }

        Assert.False(File.Exists(Path.Combine(reopened.Paths.OriginalFilesPath, "sample.xml")));
        Assert.False(File.Exists(Path.Combine(reopened.Paths.StagingPath, "sample.xml")));

        // Repeated restarts must not repeat cleanup or change the interrupted task.
        var secondReport = await recoveryServices.Recovery.RunAsync();
        Assert.Equal(0, secondReport.InterruptedTasks);
        Assert.Equal(0, secondReport.InterruptedSlots);
        Assert.Equal(0, secondReport.FinishedBatches);
        Assert.Equal(0, secondReport.CleanedOrphanedOriginals);
        Assert.Equal(0, secondReport.CleanupFailures);
    }

    [Fact]
    public async Task CommittedPublishSurvivesRestartAndIsNotRepublished()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        await using (var services = storage.CreateServices())
        {
            using (var connection = storage.Factory.CreateOpenConnection())
            {
                InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
            }

            var staged = StageOriginal(storage, "sample.xml");
            await services.Publish.RegisterIntentAsync(CreateIntent("task-1", "media-1", "file-1", "sample.xml", staged));

            // The commit succeeds; only the caller's success response is lost.
            var outcome = await services.Publish.PublishAsync("task-1", Comments(3));
            Assert.Equal(PublishOutcomeKind.Published, outcome.Kind);
        }

        using var reopened = storage.Reopen();
        await using var recoveryServices = reopened.CreateServices();
        var report = await recoveryServices.Recovery.RunAsync();

        Assert.Equal(0, report.InterruptedTasks);
        Assert.Equal(0, report.InterruptedSlots);
        Assert.Equal(0, report.CleanedOrphanedOriginals);
        Assert.Equal(0, report.CleanupFailures);
        Assert.Equal(0, report.InconsistentTasks);

        using (var connection = reopened.Factory.CreateOpenConnection())
        {
            Assert.Equal("Completed", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
            Assert.Equal(3L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments WHERE FileId = 'file-1';"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaBindings WHERE MediaId = 'media-1' AND FileId = 'file-1';"));
            Assert.Equal("file-1", ScalarString(connection, "SELECT ActiveFileId FROM MediaState WHERE MediaId = 'media-1';"));
        }

        Assert.True(File.Exists(Path.Combine(reopened.Paths.OriginalFilesPath, "sample.xml")));

        var secondReport = await recoveryServices.Recovery.RunAsync();
        Assert.Equal(0, secondReport.InterruptedTasks);
        Assert.Equal(0, secondReport.CleanedOrphanedOriginals);

        var republish = await recoveryServices.Publish.PublishAsync("task-1");
        Assert.Equal(PublishOutcomeKind.AlreadyPublished, republish.Kind);
        using (var connection = reopened.Factory.CreateOpenConnection())
        {
            Assert.Equal(3L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments WHERE FileId = 'file-1';"));
        }
    }

    [Fact]
    public async Task CrashInsideTransactionLeavesNoPartialStateAndRecoversAsUncommitted()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        await using (var services = storage.CreateServices())
        {
            using (var connection = storage.Factory.CreateOpenConnection())
            {
                InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
            }

            var staged = StageOriginal(storage, "sample.xml");
            await services.Publish.RegisterIntentAsync(CreateIntent("task-1", "media-1", "file-1", "sample.xml", staged));
            services.FileStore.MoveStagedOriginalToOriginals(staged, "sample.xml");
        }

        // Simulate a process that died inside the publish transaction: rows are written
        // on an open transaction and the connection is abandoned without committing.
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                """
                INSERT INTO Files (FileId, OriginalFileName, StoredFileName, DisplayName, Format, ContentHash, ImportedAtUtcMs, CommentCount, LastCommentTimeMs, ParseDataVersion, Status, DeleteRequestedAtUtcMs)
                VALUES ('file-1', 'sample.xml', 'sample.xml', 'Sample', 'xml', 'hash-1', $now, 3, 1000, 'm1-v1', 'Published', NULL);
                """,
                ("$now", NowMs));
            Execute(
                connection,
                transaction,
                "INSERT INTO MediaBindings (MediaId, FileId, BoundAtUtcMs) VALUES ('media-1', 'file-1', $now);",
                ("$now", NowMs));
            // No commit: the connection is dropped at the end of this scope.
        }

        using var reopened = storage.Reopen();
        await using var recoveryServices = reopened.CreateServices();
        var report = await recoveryServices.Recovery.RunAsync();

        Assert.Equal(1, report.InterruptedTasks);
        Assert.Equal(0, report.InconsistentTasks);

        using (var connection = reopened.Factory.CreateOpenConnection())
        {
            // The transaction result is authoritative: nothing was committed, so no
            // half binding or file record may remain.
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Files;"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaBindings;"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaState;"));
            Assert.Equal("Interrupted", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
        }

        Assert.False(File.Exists(Path.Combine(reopened.Paths.OriginalFilesPath, "sample.xml")));
    }

    [Fact]
    public async Task CompletedMarkerWithoutFileRecordIsReportedWithoutDataLoss()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1", taskStatus: "Completed");
            Execute(
                connection,
                "UPDATE ImportTasks SET TargetFileId = 'file-missing', TargetStoredFileName = 'file-missing.xml', Status = 'Completed' WHERE TaskId = 'task-1';");
        }

        var orphanPath = Path.Combine(storage.Paths.OriginalFilesPath, "file-missing.xml");
        File.WriteAllText(orphanPath, "unowned");

        await using var services = storage.CreateServices();
        var report = await services.Recovery.RunAsync();

        Assert.Equal(1, report.InconsistentTasks);
        Assert.Equal(0, report.InterruptedTasks);
        Assert.Equal(0, report.CleanedOrphanedOriginals);

        // Recovery never deletes an original it cannot attribute to an uncommitted task.
        Assert.True(File.Exists(orphanPath));
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal("Completed", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
        }
    }

    [Fact]
    public async Task DeletionMarksThenCleanupRemovesFileAndRecord()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1", storedFileName: "file-1.xml");
            Execute(
                connection,
                "INSERT INTO Comments (FileId, SourceOrdinal, TimeMs, Text, Color, Mode, FontSize) VALUES ('file-1', 0, 1000, 'x', 1, 1, 25);");
        }

        var originalPath = Path.Combine(storage.Paths.OriginalFilesPath, "file-1.xml");
        File.WriteAllText(originalPath, "payload");

        var mark = await services.Deletion.MarkForDeletionAsync("file-1");
        Assert.Equal(FileDeletionMarkStatus.Marked, mark.Status);
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal("Deleting", ScalarString(connection, "SELECT Status FROM Files WHERE FileId = 'file-1';"));
        }

        Assert.True(File.Exists(originalPath));

        var cleanup = await services.Deletion.CleanupAsync();
        Assert.Equal(1, cleanup.Deleted);
        Assert.Equal(0, cleanup.Failed);
        Assert.False(File.Exists(originalPath));
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments WHERE FileId = 'file-1';"));
        }

        var retry = await services.Deletion.CleanupAsync();
        Assert.Equal(0, retry.Deleted);
        Assert.Equal(0, retry.Failed);
    }

    [Fact]
    public async Task DeletionRefusesReferencedFile()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1");
            InsertBinding(connection, "media-1", "file-1");
        }

        var mark = await services.Deletion.MarkForDeletionAsync("file-1");
        Assert.Equal(FileDeletionMarkStatus.Referenced, mark.Status);
        Assert.Equal(1L, mark.BindingCount);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal("Published", ScalarString(connection, "SELECT Status FROM Files WHERE FileId = 'file-1';"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaBindings WHERE FileId = 'file-1';"));
        }
    }

    [Fact]
    public async Task DeletionFailureKeepsMarkAndSucceedsOnRetry()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1", storedFileName: "blocked.xml");
        }

        // A directory at the expected file path makes the disk delete fail deterministically.
        var blockedPath = Path.Combine(storage.Paths.OriginalFilesPath, "blocked.xml");
        Directory.CreateDirectory(blockedPath);

        var mark = await services.Deletion.MarkForDeletionAsync("file-1");
        Assert.Equal(FileDeletionMarkStatus.Marked, mark.Status);

        var failedCleanup = await services.Deletion.CleanupAsync();
        Assert.Equal(0, failedCleanup.Deleted);
        Assert.Equal(1, failedCleanup.Failed);
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal("DeleteFailed", ScalarString(connection, "SELECT Status FROM Files WHERE FileId = 'file-1';"));
        }

        Directory.Delete(blockedPath);
        var retry = await services.Deletion.CleanupAsync();
        Assert.Equal(1, retry.Deleted);
        Assert.Equal(0, retry.Failed);
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
        }
    }

    [Fact]
    public async Task PublishRejectsBindingToFileMarkedForDeletion()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1");
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        var mark = await services.Deletion.MarkForDeletionAsync("file-1");
        Assert.Equal(FileDeletionMarkStatus.Marked, mark.Status);

        await Assert.ThrowsAsync<InvalidOperationException>(() => services.Publish.RegisterIntentAsync(
            CreateIntent("task-1", "media-1", "file-1", "file-1.xml", stagedOriginalPath: null)));

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM MediaBindings WHERE FileId = 'file-1';"));
        }
    }

    [Fact]
    public async Task StartupInitializerMigratesAndRunsRecovery()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        await using (var services = storage.CreateServices())
        {
            using (var connection = storage.Factory.CreateOpenConnection())
            {
                InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
            }

            var staged = StageOriginal(storage, "sample.xml");
            await services.Publish.RegisterIntentAsync(CreateIntent("task-1", "media-1", "file-1", "sample.xml", staged));
        }

        var fileStore = new PublishFileStore(storage.Paths);
        await using var coordinator = new SqliteWriteCoordinator(storage.Factory);
        var deletion = new FileDeletionCoordinator(storage.Factory, fileStore, coordinator);
        var logger = new RecordingLogger<StorageStartupInitializer>();
        var initializer = new StorageStartupInitializer(
            storage.CreateMigrator(),
            new StorageRecoveryService(storage.Factory, fileStore, coordinator),
            deletion,
            new SqliteNativeLibraryProbe(),
            logger);

        await initializer.StartAsync(CancellationToken.None);

        Assert.False(logger.HasErrors, logger.LastError?.ToString());
        Assert.Contains(LogLevel.Information, logger.Levels);
        Assert.Contains(logger.Messages, message => message.Contains("resolved from its own package", StringComparison.Ordinal));
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(
                SchemaMigrations.CurrentVersion,
                (int)ScalarLong(connection, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
            Assert.Equal("Interrupted", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
        }

        Assert.False(File.Exists(Path.Combine(storage.Paths.StagingPath, "sample.xml")));
    }

    [Fact]
    public async Task StartupInitializerSwallowsMigrationFailureAndKeepsExistingData()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertFile(connection, "file-1");
        }

        var failingMigrator = storage.CreateMigrator([.. SchemaMigrations.Default, FailingMigration]);
        var fileStore = new PublishFileStore(storage.Paths);
        await using var coordinator = new SqliteWriteCoordinator(storage.Factory);
        var logger = new RecordingLogger<StorageStartupInitializer>();
        var initializer = new StorageStartupInitializer(
            failingMigrator,
            new StorageRecoveryService(storage.Factory, fileStore, coordinator),
            new FileDeletionCoordinator(storage.Factory, fileStore, coordinator),
            new SqliteNativeLibraryProbe(),
            logger);

        // The hosted service must never let a migration failure take down the server.
        await initializer.StartAsync(CancellationToken.None);

        Assert.True(logger.HasErrors);
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(
                SchemaMigrations.CurrentVersion,
                (int)ScalarLong(connection, "SELECT Version FROM SchemaVersion WHERE Id = 1;"));
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
        }

        Assert.Single(Directory.GetFiles(storage.Paths.MigrationsPath, "*.db"));
    }

    [Fact]
    public async Task StartupInitializerRetriesPendingFileDeletions()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();

        await using (var services = storage.CreateServices())
        {
            using (var connection = storage.Factory.CreateOpenConnection())
            {
                InsertFile(connection, "file-1", storedFileName: "blocked.xml");
            }

            var blockedPath = Path.Combine(storage.Paths.OriginalFilesPath, "blocked.xml");
            Directory.CreateDirectory(blockedPath);

            var mark = await services.Deletion.MarkForDeletionAsync("file-1");
            Assert.Equal(FileDeletionMarkStatus.Marked, mark.Status);
            var failedCleanup = await services.Deletion.CleanupAsync();
            Assert.Equal(1, failedCleanup.Failed);

            // The blocking condition disappears before the next server start.
            Directory.Delete(blockedPath);
        }

        var fileStore = new PublishFileStore(storage.Paths);
        await using var coordinator = new SqliteWriteCoordinator(storage.Factory);
        var logger = new RecordingLogger<StorageStartupInitializer>();
        var initializer = new StorageStartupInitializer(
            storage.CreateMigrator(),
            new StorageRecoveryService(storage.Factory, fileStore, coordinator),
            new FileDeletionCoordinator(storage.Factory, fileStore, coordinator),
            new SqliteNativeLibraryProbe(),
            logger);

        await initializer.StartAsync(CancellationToken.None);

        Assert.False(logger.HasErrors, logger.LastError?.ToString());
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
        }
    }

    [Fact]
    public async Task FileReferencedOnlyByImportTaskHistoryCanBeDeleted()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        var staged = StageOriginal(storage, "sample.xml");
        await services.Publish.RegisterIntentAsync(CreateIntent("task-1", "media-1", "file-1", "sample.xml", staged));
        await services.Publish.PublishAsync("task-1", Comments(1));

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal("file-1", ScalarString(connection, "SELECT FileId FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.True(ScalarIsNull(connection, "SELECT TargetFileId FROM ImportTasks WHERE TaskId = 'task-1';"));

            // Unbind the way the management API will: clear the active selection and
            // remove the binding in one transaction.
            using var transaction = connection.BeginTransaction();
            Execute(
                connection,
                transaction,
                "UPDATE MediaState SET ActiveFileId = NULL, Version = Version + 1 WHERE MediaId = 'media-1';");
            Execute(
                connection,
                transaction,
                "DELETE FROM MediaBindings WHERE MediaId = 'media-1' AND FileId = 'file-1';");
            transaction.Commit();
        }

        var mark = await services.Deletion.MarkForDeletionAsync("file-1");
        Assert.Equal(FileDeletionMarkStatus.Marked, mark.Status);
        var cleanup = await services.Deletion.CleanupAsync();
        Assert.Equal(1, cleanup.Deleted);
        Assert.Equal(0, cleanup.Failed);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = 'file-1';"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM Comments WHERE FileId = 'file-1';"));
            Assert.True(ScalarIsNull(connection, "SELECT FileId FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("Completed", ScalarString(connection, "SELECT Status FROM ImportTasks WHERE TaskId = 'task-1';"));
        }
    }

    [Fact]
    public async Task StartupInitializerSkipsMigrationAndRecoveryWhenBundledNativeAssetIsUnavailable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "danmuku-native-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var migrator = new RecordingMigrator();
            var recovery = new RecordingRecoveryService();
            var deletion = new RecordingDeletionCoordinator();
            var logger = new RecordingLogger<StorageStartupInitializer>();
            var initializer = new StorageStartupInitializer(
                migrator,
                recovery,
                deletion,
                new UnavailableNativeLibraryProbe(directory),
                logger);

            // The hosted service must not create a database on the Server-provided library.
            await initializer.StartAsync(CancellationToken.None);

            Assert.Equal(0, migrator.MigrateCalls);
            Assert.Equal(0, recovery.RunCalls);
            Assert.Equal(0, deletion.CleanupCalls);
            Assert.True(logger.HasErrors);
            var error = Assert.IsType<SqliteNativeLibraryUnavailableException>(logger.LastError);
            Assert.Equal(SqliteNativeResolutionState.AssetNotFound, error.State);
            Assert.Contains(Path.Combine(directory, "runtimes"), error.Message, StringComparison.Ordinal);
        }
        finally
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // Best effort cleanup only.
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup only.
            }
        }
    }

    private static string StageOriginal(StorageTestContext storage, string fileName)
    {
        File.WriteAllText(Path.Combine(storage.Paths.StagingPath, fileName), "<i></i>");
        return $"staging/{fileName}";
    }

    private static PublishIntentRequest CreateIntent(
        string taskId,
        string mediaId,
        string fileId,
        string storedFileName,
        string? stagedOriginalPath) =>
        new(
            taskId,
            mediaId,
            fileId,
            storedFileName,
            storedFileName,
            "Sample",
            "xml",
            "hash-1",
            1000,
            "m1-v1",
            stagedOriginalPath);

    private static async IAsyncEnumerable<CommentRecord> Comments(int count)
    {
        for (var index = 0; index < count; index++)
        {
            await Task.CompletedTask;
            yield return new CommentRecord(index, $"source-{index}", 1000 + index, $"text-{index}", 16777215, 1, 25);
        }
    }

    private sealed class RecordingMigrator : ISqliteSchemaMigrator
    {
        public int CurrentVersion => 0;

        public int MigrateCalls { get; private set; }

        public SqliteMigrationResult Migrate()
        {
            MigrateCalls++;
            return new SqliteMigrationResult(0, 0, null);
        }
    }

    private sealed class RecordingRecoveryService : IStorageRecoveryService
    {
        public int RunCalls { get; private set; }

        public Task<StorageRecoveryReport> RunAsync(CancellationToken cancellationToken = default)
        {
            RunCalls++;
            return Task.FromResult(new StorageRecoveryReport(0, 0, 0, 0, 0, 0, 0, 0));
        }
    }

    private sealed class RecordingDeletionCoordinator : IFileDeletionCoordinator
    {
        public int CleanupCalls { get; private set; }

        public Task<FileDeletionMarkResult> MarkForDeletionAsync(string fileId, CancellationToken cancellationToken = default) =>
            Task.FromResult(new FileDeletionMarkResult(FileDeletionMarkStatus.NotFound, 0));

        public Task<FileDeletionCleanupReport> CleanupAsync(CancellationToken cancellationToken = default)
        {
            CleanupCalls++;
            return Task.FromResult(new FileDeletionCleanupReport(0, 0));
        }
    }

    private sealed class UnavailableNativeLibraryProbe : ISqliteNativeLibraryProbe
    {
        private readonly string _pluginDirectory;

        public UnavailableNativeLibraryProbe(string pluginDirectory)
        {
            _pluginDirectory = pluginDirectory;
        }

        public string EnsureAvailable() => SqliteNativeLibraryResolver.LoadBundledAsset(_pluginDirectory);
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogLevel> Levels { get; } = [];

        public List<string> Messages { get; } = [];

        public bool HasErrors => Levels.Contains(LogLevel.Error);

        public Exception? LastError { get; private set; }

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            Levels.Add(logLevel);
            Messages.Add(formatter(state, exception));
            if (logLevel == LogLevel.Error)
            {
                LastError = exception;
            }
        }
    }
}
