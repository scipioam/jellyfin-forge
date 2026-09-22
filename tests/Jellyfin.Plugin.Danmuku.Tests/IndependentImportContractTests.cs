using Jellyfin.Plugin.Danmuku.Api;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Jellyfin.Plugin.Danmuku.Tests.StorageTestSql;

namespace Jellyfin.Plugin.Danmuku.Tests;

[Collection("Import boundaries")]
public sealed class IndependentImportContractTests
{
    private static ImportBatchRequest Request(string[]? names = null) => new(Guid.NewGuid().ToString("N"), null, null, names ?? ["standalone.json"], "import");

    [Fact]
    public async Task NewAndReusedFilesNeverChangeExistingMediaAndRetriesKeepTheirIntent()
    {
        await using var x = new ImportLifecycleTestContext();
        var bound = await x.PublishAsync("bound", "active");
        await x.PublishAsync("stopped", "stopped");
        var stopped = await x.Bindings.ReadAsync("stopped");
        await x.Bindings.UpdateAsync("stopped", stopped.Version, stopped.FileIds, null);
        var before = Snapshot(x);
        foreach (var (text, expected) in new[] { ("new", "Imported"), ("bound", "Reused"), ("new", "Reused") })
        {
            var request = Request();
            var batch = await x.Imports.CreateBatchAsync(request);
            await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json(text));
            await x.Imports.ProcessPendingAsync();
            var result = await x.Imports.GetBatchAsync(batch.BatchId);
            Assert.Null(result.MediaId); Assert.Null(result.ExpectedVersion);
            Assert.Equal("Completed", result.Slots[0].Status);
            Assert.Equal(expected, result.Slots[0].ResultCode);
            Assert.Equal(before, Snapshot(x));
            Assert.Equal(result.Slots[0].FileId, (await x.Imports.CreateBatchAsync(request)).Slots[0].FileId);
            Assert.Equal("BatchIdConflict", (await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.CreateBatchAsync(request with { Operation = "append", MediaId = "active", ExpectedVersion = 0 }))).Code);
            Assert.Equal("InvalidOperation", (await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.ResumeAsync(result.Slots[0].TaskId!, 0))).Code);
            var query = new ManagementQueries(x.Storage.Factory, x.Files).Task(result.Slots[0].TaskId!);
            Assert.Equal("import", query["Operation"]); Assert.Null(query["MediaId"]);
        }
        Assert.Equal(3, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(1, new ManagementQueries(x.Storage.Factory, x.Files).Files(null, true, 0, 50).TotalCount);
        Assert.Equal(bound, (await x.Bindings.ReadAsync("active")).ActiveFileId);
    }

    [Theory]
    [InlineData("import", "media", null, null)]
    [InlineData("import", "", null, null)]
    [InlineData("import", null, 0L, null)]
    [InlineData("import", null, null, "file")]
    [InlineData("append", null, 0L, null)]
    [InlineData("append", "media", null, null)]
    [InlineData("replace", "media", 0L, null)]
    [InlineData("unknown", null, null, null)]
    public async Task InvalidIntentCannotCreatePositions(string operation, string? media, long? version, string? replace)
    {
        await using var x = new ImportLifecycleTestContext();
        Assert.Equal(422, (await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.CreateBatchAsync(Request() with { Operation = operation, MediaId = media, ExpectedVersion = version, ReplaceFileId = replace }))).StatusCode);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportSlots"));
    }

    [Fact]
    public async Task ConfirmationOrderingCancellationAndRestartWorkWithoutMedia()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.Imports.CreateBatchAsync(Request(["bad.json", "next.json"]));
        await x.UploadAsync(batch.BatchId, content: "[{\"progress\":1,\"content\":\"ok\"},{\"progress\":2,\"content\":\"\"}]");
        await x.UploadAsync(batch.BatchId, 1, ImportLifecycleTestContext.Json("later"));
        await x.Imports.ProcessPendingAsync();
        var waiting = await x.Imports.GetBatchAsync(batch.BatchId);
        Assert.Equal("AwaitingConfirmation", waiting.Slots[0].TaskStatus);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM Files"));
        await x.Imports.ConfirmBatchSkipAsync(batch.BatchId); await x.Imports.ProcessPendingAsync();
        Assert.All((await x.Imports.GetBatchAsync(batch.BatchId)).Slots, s => Assert.Equal("Completed", s.Status));
        var cancel = await x.Imports.CreateBatchAsync(Request());
        await x.UploadAsync(cancel.BatchId); await x.Imports.CancelBatchAsync(cancel.BatchId); await x.Imports.ProcessPendingAsync();
        Assert.Equal("Cancelled", (await x.Imports.GetBatchAsync(cancel.BatchId)).Slots[0].Status);
        var pending = await x.Imports.CreateBatchAsync(Request()); await x.UploadAsync(pending.BatchId);
        await new StorageRecoveryService(x.Storage.Factory, x.Files, x.Writes, x.Clock).RunAsync();
        Assert.Equal("Interrupted", (await x.Imports.GetBatchAsync(pending.BatchId)).Slots[0].Status);
        Assert.Equal(2, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM MediaState")); Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM MediaBindings"));
    }

    [Theory]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task PublishFaultDoesNotLeaveHalfFileOrBinding(string point)
    {
        await using var x = new ImportLifecycleTestContext(inner => new FaultPublish(inner, point));
        var batch = await x.Imports.CreateBatchAsync(Request()); await x.UploadAsync(batch.BatchId); await x.Imports.ProcessPendingAsync();
        Assert.Equal(point == "after" ? "Completed" : "Failed", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].Status);
        Assert.Equal(point == "after" ? 1 : 0, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(point == "after" ? 1 : 0, Directory.GetFiles(x.Storage.Paths.OriginalFilesPath).Length);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM MediaState")); Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM MediaBindings"));
    }

    [Fact]
    public void MigrationPreservesV3DataAndBackupCanRestoreOldProgramCompatibility()
    {
        using var storage = new StorageTestContext(); var old = SchemaMigrations.Default.Take(3).ToArray();
        storage.CreateMigrator(old).Migrate();
        using (var c = storage.Factory.CreateOpenConnection())
        {
            InsertFile(c, "file"); InsertBinding(c, "media", "file");
            InsertBatchAndTask(c, "batch", "media", "task", operation: "replace", replaceFileId: "file");
        }
        var result = storage.CreateMigrator().Migrate(); Assert.Equal(3, result.FromVersion); Assert.Equal(4, result.ToVersion); Assert.NotNull(result.BackupPath);
        using (var c = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM ImportSlots WHERE BatchId='batch'"));
            Assert.Equal("file", ScalarString(c, "SELECT ReplaceFileId FROM ImportBatches WHERE BatchId='batch'"));
            Assert.Equal(1, ScalarLong(c, "SELECT COUNT(*) FROM MediaBindings"));
            Assert.Equal(0, ScalarLong(c, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
            foreach (var values in new[] { "NULL,'append',NULL,0", "'media','import',NULL,NULL", "NULL,'import',NULL,0", "'media','replace',NULL,0", "'media','append',NULL,NULL" })
                Assert.Throws<SqliteException>(() => Execute(c, "INSERT INTO ImportBatches(BatchId,MediaId,Operation,ReplaceFileId,ExpectedMediaVersion,CreatedAtUtcMs) VALUES('bad'," + values + ",0)"));
            using var backup = new SqliteConnection("Data Source=" + result.BackupPath); backup.Open();
            Assert.Equal(3, ScalarLong(backup, "SELECT Version FROM SchemaVersion"));
            backup.BackupDatabase(c);
        }
        Assert.Equal(3, storage.CreateMigrator(old).Migrate().ToVersion);
        Assert.Equal(4, storage.CreateMigrator().Migrate().ToVersion);
    }

    [Fact]
    public void FailedV4TableRebuildRollsBackCyclicReferences()
    {
        using var storage = new StorageTestContext(); var old = SchemaMigrations.Default.Take(3).ToArray();
        storage.CreateMigrator(old).Migrate();
        using (var c = storage.Factory.CreateOpenConnection()) { InsertFile(c, "kept"); InsertBatchAndTask(c, "batch", "media", "task"); }
        var broken = SchemaMigrations.Default[3] with { Sql = SchemaMigrations.Default[3].Sql + " INSERT INTO MissingTable VALUES(1);" };
        Assert.Throws<SqliteException>(() => storage.CreateMigrator([.. old, broken]).Migrate());
        using var current = storage.Factory.CreateOpenConnection();
        Assert.Equal(3, ScalarLong(current, "SELECT Version FROM SchemaVersion"));
        Assert.Equal(1, ScalarLong(current, "SELECT COUNT(*) FROM ImportTasks WHERE TaskId='task'"));
        Assert.Equal(1, ScalarLong(current, "SELECT COUNT(*) FROM ImportSlots WHERE BatchId='batch'"));
        Assert.Equal(0, ScalarLong(current, "SELECT COUNT(*) FROM pragma_foreign_key_check"));
        var path = Assert.Single(Directory.GetFiles(storage.Paths.MigrationsPath, "*.db"));
        using var backup = new SqliteConnection("Data Source=" + path + ";Mode=ReadOnly"); backup.Open();
        Assert.Equal("ok", ScalarString(backup, "PRAGMA integrity_check"));
        Assert.Equal(1, ScalarLong(backup, "SELECT COUNT(*) FROM Files WHERE FileId='kept'"));
    }

    [Fact]
    public async Task ConcurrentIndependentBatchesReuseOneFileAndDeletionCannotBeReused()
    {
        await using var x = new ImportLifecycleTestContext();
        var batches = await Task.WhenAll(Enumerable.Range(0, 2).Select(_ => x.Imports.CreateBatchAsync(Request())));
        await Task.WhenAll(batches.Select(b => x.UploadAsync(b.BatchId)));
        await Task.WhenAll(x.Imports.ProcessPendingAsync(), x.Imports.ProcessPendingAsync());
        var first = (await x.Imports.GetBatchAsync(batches[0].BatchId)).Slots[0];
        var second = (await x.Imports.GetBatchAsync(batches[1].BatchId)).Slots[0];
        Assert.Equal(first.FileId, second.FileId); Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
        await deletion.MarkForDeletionAsync(first.FileId!);
        var blocked = await x.Imports.CreateBatchAsync(Request()); await x.UploadAsync(blocked.BatchId); await x.Imports.ProcessPendingAsync();
        var failed = (await x.Imports.GetBatchAsync(blocked.BatchId)).Slots[0];
        Assert.Equal("Failed", failed.Status); Assert.Equal("FileUnavailable", failed.ErrorCode);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files")); Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM MediaState"));
    }

    [Fact]
    public async Task IndependentPositionsShareCapacityAndExpireWithoutMedia()
    {
        await using var x = new ImportLifecycleTestContext();
        var requests = Enumerable.Range(0, 10).Select(_ => Request(Enumerable.Repeat("file.json", 10).ToArray())).ToArray();
        foreach (var r in requests) await x.Imports.CreateBatchAsync(r);
        Assert.Equal("ImportCapacity", (await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.CreateBatchAsync(Request()))).Code);
        x.Clock.Advance(TimeSpan.FromHours(1)); await x.Imports.SweepAsync();
        Assert.Equal(100, x.Scalar("SELECT COUNT(*) FROM ImportSlots WHERE Status='Expired'"));
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM MediaState"));
        await x.Imports.CreateBatchAsync(Request());
    }

    private static string Snapshot(ImportLifecycleTestContext x) => x.ScalarText("SELECT group_concat(MediaId||':'||coalesce(ActiveFileId,'')||':'||IsDeactivated||':'||Version,'|') FROM (SELECT * FROM MediaState ORDER BY MediaId)") + ";" + x.ScalarText("SELECT group_concat(MediaId||':'||FileId,'|') FROM (SELECT * FROM MediaBindings ORDER BY MediaId,FileId)");
    private sealed class FaultPublish(IPublishService inner, string point) : IPublishService
    {
        public Task<PublishIntentSnapshot> RegisterIntentAsync(PublishIntentRequest request, CancellationToken ct = default) => inner.RegisterIntentAsync(request, ct);
        public Task<PublishOutcome> PublishAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<PublishOutcome> PublishAsync(string id, IAsyncEnumerable<CommentRecord> comments, CancellationToken ct = default)
        {
            if (point == "before") throw new IOException("before commit");
            var result = await inner.PublishAsync(id, point == "during" ? Broken() : comments, ct);
            if (point == "after") throw new IOException("lost reply"); return result;
        }
        private static async IAsyncEnumerable<CommentRecord> Broken() { await Task.CompletedTask; yield return new(1, null, 1, "partial", 0, 1, 25); throw new IOException("during commit"); }
    }
}
