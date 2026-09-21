using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

[Collection("Import boundaries")]
public sealed class ImportOwnershipContractTests
{
    [Fact]
    public async Task CancellationBetweenReceivingCommitAndOwnerRegistrationCannotCreateAnOrphan()
    {
        await using var x = new ImportLifecycleTestContext();
        var gate = new ReceiveCommitGate(x.Writes);
        var imports = new ImportService(x.Storage.Factory, gate, x.Files, x.Publisher,
            new ImportTaskStore(x.Storage.Factory, x.Writes), x.Bindings, x.Clock);
        var batch = await x.BatchAsync();
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(ImportLifecycleTestContext.Json("synthetic")));
        var receive = imports.ReceiveAsync(batch.BatchId, 0, source);
        await gate.Committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await imports.CancelBatchAsync(batch.BatchId);
        gate.Release.TrySetResult();
        Assert.Equal("Cancelled", (await receive).Status);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportTasks"));
    }

    [Fact]
    public async Task BoundedCleanupPagesRetryLaterCandidatesDespiteEarlierFailures()
    {
        await using var x = new ImportLifecycleTestContext();
        var candidates = new List<(string Batch, int Slot, string Path)>();
        for (var b = 0; b < 11; b++)
        {
            var batch = await x.BatchAsync(Enumerable.Repeat("sample", 10).ToArray());
            using (var c = x.Storage.Factory.CreateOpenConnection())
            using (var cmd = c.CreateCommand())
            {
                cmd.CommandText = "SELECT Slot,UploadPath FROM ImportSlots WHERE BatchId=$id";
                cmd.Parameters.AddWithValue("$id", batch.BatchId);
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var path = r.GetString(1);
                    candidates.Add((batch.BatchId, r.GetInt32(0), path));
                    Directory.CreateDirectory(x.Files.GetStagingPath(path));
                }
            }
            await x.Imports.CancelBatchAsync(batch.BatchId);
        }
        var last = candidates.OrderBy(c => c.Batch, StringComparer.Ordinal).ThenBy(c => c.Slot).Last();
        Directory.Delete(x.Files.GetStagingPath(last.Path));
        for (var scan = 0; scan < 3; scan++) await x.Imports.SweepAsync();
        using var connection = x.Storage.Factory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT UploadPath FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot";
        command.Parameters.AddWithValue("$batch", last.Batch);
        command.Parameters.AddWithValue("$slot", last.Slot);
        Assert.IsType<DBNull>(command.ExecuteScalar());
    }

    [Fact]
    public async Task LegacyRecoveryCleanupFailureRetainsOwnershipPastSevenDays()
    {
        await using var x = new ImportLifecycleTestContext();
        using (var c = x.Storage.Factory.CreateOpenConnection())
        {
            StorageTestSql.InsertBatchAndTask(c, "legacy", "media", "legacy-task", taskStatus: "Interrupted", slotStatus: "Interrupted");
            StorageTestSql.Execute(c, """
                UPDATE ImportTasks SET ErrorCode='RecoveryCleanupFailed',FinishedAtUtcMs=0,
                    TargetFileId='orphan',TargetStoredFileName='orphan.json',StagedOriginalPath='staging/blocked';
                UPDATE ImportSlots SET TaskId='legacy-task',FinishedAtUtcMs=0;
                UPDATE ImportBatches SET Status='Finished',FinishedAtUtcMs=0;
                """);
        }
        Directory.CreateDirectory(x.Files.GetStagingPath("staging/blocked"));
        var recovery = new StorageRecoveryService(x.Storage.Factory, x.Files, x.Writes, x.Clock);
        await recovery.RunAsync();
        await x.Imports.SweepAsync();
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM ImportTasks"));
        Directory.Delete(x.Files.GetStagingPath("staging/blocked"));
        await recovery.RunAsync();
        await x.Imports.SweepAsync();
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportTasks"));
    }

    private sealed class ReceiveCommitGate(ISqliteWriteCoordinator inner) : ISqliteWriteCoordinator
    {
        private int _armed = 1;
        public TaskCompletionSource Committed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<T> EnqueueAsync<T>(Func<SqliteConnection, CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
        {
            var result = await inner.EnqueueAsync(work, cancellationToken);
            if (result is string path && path.StartsWith("staging/", StringComparison.Ordinal) && Interlocked.Exchange(ref _armed, 0) == 1)
            {
                Committed.TrySetResult();
                await Release.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }
            return result;
        }
        public Task EnqueueAsync(Func<SqliteConnection, CancellationToken, Task> work, CancellationToken cancellationToken = default) =>
            inner.EnqueueAsync(work, cancellationToken);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
