using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Jellyfin.Plugin.Danmuku.Tests.StorageTestSql;

namespace Jellyfin.Plugin.Danmuku.Tests;

[Collection("Import boundaries")]
public sealed class ImportRecoveryAndBindingContractTests
{
    [Theory]
    [InlineData("before")]
    [InlineData("during")]
    [InlineData("after")]
    public async Task PublishFailureUsesPersistedCompletionInsteadOfResponseDelivery(string point)
    {
        await using var x = new ImportLifecycleTestContext(inner => new FaultPublish(inner, point));
        var batch = await x.BatchAsync();
        await x.UploadAsync(batch.BatchId);
        await x.Imports.ProcessPendingAsync();
        var slot = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0];
        var committed = point == "after";
        Assert.Equal(committed ? "Completed" : "Failed", slot.Status);
        Assert.Equal(committed ? 1 : 0, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(committed ? 1 : 0, x.Scalar("SELECT COUNT(*) FROM Comments"));
        Assert.Equal(committed ? 1 : 0, x.Scalar("SELECT COUNT(*) FROM MediaBindings"));
        Assert.Equal(committed ? 1 : 0, Directory.GetFiles(x.Storage.Paths.OriginalFilesPath).Length);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task MissingMediaAndQueryFailureAreDistinctAndAllBindingCheckDoesNotRebind()
    {
        await using var x = new ImportLifecycleTestContext();
        var one = await x.PublishAsync("one", "one");
        var two = await x.PublishAsync("two", "two");
        x.Lookup.Results["one"] = MediaPresence.Missing;
        x.Lookup.Results["two"] = MediaPresence.CheckFailed;
        var job = await x.Bindings.StartCheckAllAsync();
        Assert.Equal(job, await x.Bindings.StartCheckAllAsync());
        await x.Bindings.ProcessCheckJobAsync();
        var result = x.Bindings.GetCheckJob(job);
        Assert.Equal("Completed", result.Status);
        Assert.Equal(2, result.Processed);
        Assert.Equal(1, result.Missing);
        Assert.Equal(1, result.Failed);
        var missing = await x.Bindings.ReadAsync("one");
        var failed = await x.Bindings.ReadAsync("two");
        Assert.Equal("Missing", missing.CheckStatus);
        Assert.Equal("CheckFailed", failed.CheckStatus);
        Assert.Equal(one, missing.ActiveFileId);
        Assert.Equal(two, failed.ActiveFileId);
        var refused = await Assert.ThrowsAsync<ImportOperationException>(() => x.BatchAsync(media: "two"));
        Assert.Equal(503, refused.StatusCode);
        await x.Bindings.UpdateAsync("one", missing.Version, [], null);
        Assert.True((await x.Bindings.ReadAsync("one")).IsDeactivated);
        Assert.Equal(2, x.Scalar("SELECT COUNT(*) FROM Files"));
    }

    [Fact]
    public async Task BindingVersionAndDeleteMarkPreventLostUpdatesAndInvalidReferences()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("one");
        var before = await x.Bindings.ReadAsync("media");
        var after = await x.Bindings.UpdateAsync("media", before.Version, [], null);
        var stale = await Assert.ThrowsAsync<ImportOperationException>(() => x.Bindings.UpdateAsync("media", before.Version, [file], file));
        Assert.Equal("MediaVersionConflict", stale.Code);
        var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
        await deletion.MarkForDeletionAsync(file);
        var deleting = await Assert.ThrowsAsync<ImportOperationException>(() => x.Bindings.UpdateAsync("media", after.Version, [file], file));
        Assert.Equal("FileUnavailable", deleting.Code);
        Assert.Empty((await x.Bindings.ReadAsync("media")).FileIds);
    }

    [Fact]
    public async Task VersionTwoMigrationPreservesCyclicSlotTaskReferencesAndReplacementIntent()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator(SchemaMigrations.Default.Take(2).ToArray()).Migrate();
        using (var c = storage.Factory.CreateOpenConnection())
        {
            InsertFile(c, "A"); InsertBinding(c, "media", "A");
            InsertBatchAndTask(c, "batch", "media", "task", operation: "replace", replaceFileId: "A");
            Execute(c, "UPDATE ImportSlots SET TaskId='task'");
            Execute(c, "INSERT INTO ImportErrors(TaskId,SourceOrdinal,ReasonCodes,CreatedAtUtcMs) VALUES('task',1,'MissingTime',0)");
        }
        var migrated = storage.CreateMigrator().Migrate();
        Assert.Equal(2, migrated.FromVersion);
        Assert.Equal(3, migrated.ToVersion);
        Assert.NotNull(migrated.BackupPath);
        using var connection = storage.Factory.CreateOpenConnection();
        Assert.Equal("task", ScalarString(connection, "SELECT TaskId FROM ImportSlots"));
        Assert.Equal("A", ScalarString(connection, "SELECT ReplaceFileId FROM ImportBatches"));
        Assert.Equal(1, ScalarLong(connection, "SELECT COUNT(*) FROM ImportErrors"));
        Assert.Equal(1, ScalarLong(connection, "PRAGMA foreign_keys"));
        Assert.Empty(QueryNames(connection, "SELECT \"table\" FROM pragma_foreign_key_check"));
        Execute(connection, "DELETE FROM MediaBindings; DELETE FROM Files;");
        Assert.Equal("A", ScalarString(connection, "SELECT ReplaceFileId FROM ImportBatches"));
        using var backup = OpenReadOnly(migrated.BackupPath!);
        Assert.Equal(2, ScalarLong(backup, "SELECT Version FROM SchemaVersion"));
        Assert.Equal(1, ScalarLong(backup, "SELECT COUNT(*) FROM Files"));
    }

    [Fact]
    public void InvalidLegacyDuplicateContentFailsMigrationWithoutDeletingData()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator(SchemaMigrations.Default.Take(2).ToArray()).Migrate();
        using (var c = storage.Factory.CreateOpenConnection()) { InsertFile(c, "one"); InsertFile(c, "two"); }
        Assert.Throws<SqliteException>(() => storage.CreateMigrator().Migrate());
        using var current = storage.Factory.CreateOpenConnection();
        Assert.Equal(2, ScalarLong(current, "SELECT Version FROM SchemaVersion"));
        Assert.Equal(2, ScalarLong(current, "SELECT COUNT(*) FROM Files"));
        Assert.Equal(1, ScalarLong(current, "PRAGMA foreign_keys"));
        Assert.Single(Directory.GetFiles(storage.Paths.MigrationsPath));
    }

    [Fact]
    public async Task FailedTemporaryCleanupKeepsOwnershipAndRetriesWithoutErasingHistory()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync();
        var path = x.ScalarText("SELECT UploadPath FROM ImportSlots")!;
        Directory.CreateDirectory(x.Files.GetStagingPath(path));
        await x.Imports.CancelBatchAsync(batch.BatchId);
        Assert.Equal("StagingCleanupFailed", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].ErrorCode);
        x.Clock.Advance(TimeSpan.FromDays(8));
        await x.Imports.SweepAsync();
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM ImportBatches"));
        Directory.Delete(x.Files.GetStagingPath(path));
        await x.Imports.SweepAsync();
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportBatches"));
    }

    [Fact]
    public async Task PendingUploadExpiresAndReleasesAlreadyParsedFollowingFile()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync(["empty", "later"]);
        await x.UploadAsync(batch.BatchId, 1);
        await x.Imports.ProcessPendingAsync();
        x.Clock.Advance(TimeSpan.FromHours(1));
        await x.Imports.ProcessPendingAsync();
        Assert.Equal(new[] { "Expired", "Completed" }, (await x.Imports.GetBatchAsync(batch.BatchId)).Slots.Select(s => s.Status));
    }

    [Fact]
    public async Task ReimportWhileMatchingFileIsDeletingDoesNotPublishAnotherCopy()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("same");
        var state = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", state.Version, [], null);
        var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
        await deletion.MarkForDeletionAsync(file);
        var batch = await x.BatchAsync();
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("same"));
        await x.Imports.ProcessPendingAsync();
        var result = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0];
        Assert.Equal("Failed", result.Status);
        Assert.Equal("FileUnavailable", result.ErrorCode);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Empty((await x.Bindings.ReadAsync("media")).FileIds);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    private sealed class FaultPublish(IPublishService inner, string point) : IPublishService
    {
        public Task<PublishIntentSnapshot> RegisterIntentAsync(PublishIntentRequest request, CancellationToken ct = default) => inner.RegisterIntentAsync(request, ct);
        public Task<PublishOutcome> PublishAsync(string id, CancellationToken ct = default) => throw new NotSupportedException();
        public async Task<PublishOutcome> PublishAsync(string id, IAsyncEnumerable<CommentRecord> comments, CancellationToken ct = default)
        {
            if (point == "before") throw new IOException("Injected before publication");
            var result = await inner.PublishAsync(id, point == "during" ? BrokenComments() : comments, ct);
            if (point == "after") throw new IOException("Injected response loss after commit");
            return result;
        }
        private static async IAsyncEnumerable<CommentRecord> BrokenComments()
        {
            await Task.CompletedTask;
            yield return new(1, null, 1, "partial", 0, 1, 25);
            throw new IOException("Injected midway through the transaction");
        }
    }
}
