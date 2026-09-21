using System.Collections.Concurrent;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>Durable import lifecycle. HTTP controllers delegate here; accepted work is owned by the host.</summary>
public sealed partial class ImportService(ISqliteConnectionFactory factory, ISqliteWriteCoordinator writes,
    IPublishFileStore files, IPublishService publish, IImportTaskStore errors, MediaBindingService bindings, TimeProvider clock)
{
    private readonly SemaphoreSlim _processor = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operations = new();
    private long Now => clock.GetUtcNow().ToUnixTimeMilliseconds();

    public async Task<ImportBatchSnapshot> CreateBatchAsync(ImportBatchRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.FileNames);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.BatchId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MediaId);
        if (request.FileNames.Count is < 1 or > ImportLifecycleLimits.BatchFiles || request.FileNames.Any(string.IsNullOrWhiteSpace)
            || request.Operation is not ("append" or "replace") || (request.Operation == "replace" && (request.FileNames.Count != 1 || request.ReplaceFileId is null))
            || (request.Operation == "append" && request.ReplaceFileId is not null))
            throw new ImportOperationException("InvalidBatch", 422, "Choose 1–10 files; replacement requires one file and a bound target.");
        await SweepAsync(ct).ConfigureAwait(false);
        // Idempotency is checked before fresh media/version validation: a lost response must
        // still return the original batch after it has changed the media version itself.
        var prior = FindBatch(request.BatchId);
        if (prior is not null) { RequireSameRequest(prior, request); return prior; }
        await bindings.RequireExistingAsync(request.MediaId, ct).ConfigureAwait(false);
        await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            var created = Now;
            if (Long(c, t, "SELECT COUNT(*) FROM ImportBatches WHERE BatchId=$id", ("$id", request.BatchId)) != 0)
                return Task.CompletedTask;
            MediaBindingService.RequireVersion(c, t, request.MediaId, request.ExpectedVersion);
            if (request.Operation == "replace" && Long(c, t, "SELECT COUNT(*) FROM MediaBindings WHERE MediaId=$media AND FileId=$file",
                ("$media", request.MediaId), ("$file", request.ReplaceFileId)) != 1)
                throw new ImportOperationException("ReplaceTargetUnbound", 409, "The replacement target is no longer bound.");
            if (Long(c, t, $"SELECT COUNT(*) FROM ImportSlots WHERE Status NOT IN ({Terminal})") + request.FileNames.Count > ImportLifecycleLimits.OpenSlots)
                throw new ImportOperationException("ImportCapacity", 503, "The import position capacity is temporarily exhausted.");
            Execute(c, t, """
                INSERT INTO ImportBatches(BatchId,MediaId,Operation,ReplaceFileId,ExpectedMediaVersion,CreatedAtUtcMs)
                VALUES($id,$media,$operation,$replace,$version,$now)
                """, ("$id", request.BatchId), ("$media", request.MediaId), ("$operation", request.Operation),
                ("$replace", request.ReplaceFileId), ("$version", request.ExpectedVersion), ("$now", created));
            for (var slot = 0; slot < request.FileNames.Count; slot++)
                Execute(c, t, """
                    INSERT INTO ImportSlots(BatchId,Slot,CreatedAtUtcMs,UploadDeadlineAtUtcMs,OriginalFileName,UploadPath)
                    VALUES($id,$slot,$now,$deadline,$name,$path)
                    """, ("$id", request.BatchId), ("$slot", slot), ("$now", created),
                    ("$deadline", created + (long)ImportLifecycleLimits.UploadWait.TotalMilliseconds),
                    ("$name", request.FileNames[slot]), ("$path", "staging/" + Guid.NewGuid().ToString("N") + ".upload"));
            t.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        var result = FindBatch(request.BatchId)!;
        RequireSameRequest(result, request);
        return result;
    }

    private static void RequireSameRequest(ImportBatchSnapshot batch, ImportBatchRequest request)
    {
        if (batch.MediaId != request.MediaId || batch.Operation != request.Operation || batch.ReplaceFileId != request.ReplaceFileId
            || !batch.Slots.Select(s => s.OriginalFileName).SequenceEqual(request.FileNames))
            throw new ImportOperationException("BatchIdConflict", 409, "This batch identifier already describes another request.");
    }

    public async Task<ImportBatchSnapshot> GetBatchAsync(string batchId, CancellationToken ct = default)
    {
        await SweepAsync(ct).ConfigureAwait(false);
        return FindBatch(batchId) ?? throw new KeyNotFoundException("Import batch not found.");
    }

    private ImportBatchSnapshot? FindBatch(string batchId)
    {
        using var c = factory.CreateOpenConnection();
        using var t = c.BeginTransaction(deferred: true);
        string media, operation, status;
        string? replace;
        long version;
        long? finished;
        using (var cmd = Command(c, t, "SELECT MediaId,Operation,ReplaceFileId,ExpectedMediaVersion,Status,FinishedAtUtcMs FROM ImportBatches WHERE BatchId=$id", ("$id", batchId)))
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return null;
            media = r.GetString(0); operation = r.GetString(1); replace = NullableText(r, 2);
            version = r.GetInt64(3); status = r.GetString(4); finished = NullableLong(r, 5);
        }
        var slots = new List<ImportSlotSnapshot>();
        using (var cmd = Command(c, t, """
            SELECT s.Slot,s.Status,s.TaskId,s.OriginalFileName,s.ReceivedBytes,s.TotalBytes,s.UploadDeadlineAtUtcMs,s.FinishedAtUtcMs,
                   COALESCE(s.CleanupError,s.ErrorCode,t.ErrorCode),t.Status,t.Stage,t.StagePercent,t.NormalComments,t.AbnormalComments,
                   t.ImportedComments,t.SkippedComments,t.DeadlineAtUtcMs,t.ResultCode,t.FileId
            FROM ImportSlots s LEFT JOIN ImportTasks t ON t.TaskId=s.TaskId WHERE s.BatchId=$id ORDER BY s.Slot
            """, ("$id", batchId)))
        using (var r = cmd.ExecuteReader())
            while (r.Read()) slots.Add(new(r.GetInt32(0), r.GetString(1), NullableText(r, 2), NullableText(r, 3) ?? "", r.GetInt64(4),
                NullableLong(r, 5), r.GetInt64(6), NullableLong(r, 7), NullableText(r, 8), NullableText(r, 9), NullableText(r, 10),
                r.IsDBNull(11) ? null : r.GetDouble(11), NullableLong(r, 12) ?? 0, NullableLong(r, 13) ?? 0,
                NullableLong(r, 14) ?? 0, NullableLong(r, 15) ?? 0, NullableLong(r, 16), NullableText(r, 17), NullableText(r, 18)));
        var waitingForPrevious = false;
        for (var index = 0; index < slots.Count; index++)
        {
            var item = slots[index];
            if (waitingForPrevious && item.TaskStatus == "Queued" && item.Stage == "Ready")
                slots[index] = item with { Stage = "WaitingForPrevious" };
            if (!StorageStatuses.Slots.Terminal.Contains(item.Status)) waitingForPrevious = true;
        }
        return new(batchId, media, operation, replace, version, status, finished, slots);
    }

    private static string? NullableText(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetString(i);
    private static long? NullableLong(SqliteDataReader r, int i) => r.IsDBNull(i) ? null : r.GetInt64(i);

    private string TaskBatch(string taskId)
    {
        using var c = factory.CreateOpenConnection();
        return Text(c, null, "SELECT BatchId FROM ImportTasks WHERE TaskId=$id", ("$id", taskId))
            ?? throw new KeyNotFoundException("Import task not found.");
    }

    public async Task ConfirmSkipAsync(string taskId, CancellationToken ct = default)
    {
        await ConfirmAsync(TaskBatch(taskId), taskId, ct).ConfigureAwait(false);
    }

    public Task ConfirmBatchSkipAsync(string batchId, CancellationToken ct = default) => ConfirmAsync(batchId, null, ct);

    private async Task ConfirmAsync(string batchId, string? taskId, CancellationToken ct)
    {
        await SweepAsync(ct).ConfigureAwait(false);
        if (FindBatch(batchId) is null) throw new KeyNotFoundException("Import batch not found.");
        await writes.EnqueueAsync((c, _) =>
        {
            // This statement captures only tasks already waiting now; it is never a batch-wide future permission.
            Execute(c, null, """
                UPDATE ImportTasks SET Status='Queued',Stage='Ready',StagePercent=NULL,SkipConfirmed=1,SkippedComments=AbnormalComments
                WHERE BatchId=$batch AND ($task IS NULL OR TaskId=$task) AND Status='AwaitingConfirmation' AND DeadlineAtUtcMs>$now
                """, ("$batch", batchId), ("$task", taskId), ("$now", Now));
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    public async Task ResumeAsync(string taskId, long expectedVersion, CancellationToken ct = default)
    {
        await SweepAsync(ct).ConfigureAwait(false);
        var batch = FindBatch(TaskBatch(taskId))!;
        await bindings.RequireExistingAsync(batch.MediaId, ct).ConfigureAwait(false);
        await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            if (Text(c, t, "SELECT Status FROM ImportTasks WHERE TaskId=$id", ("$id", taskId)) != "AwaitingConflictResolution") return Task.CompletedTask;
            MediaBindingService.RequireVersion(c, t, batch.MediaId, expectedVersion);
            if (batch.Operation == "replace" && Long(c, t, "SELECT COUNT(*) FROM MediaBindings WHERE MediaId=$media AND FileId=$file",
                ("$media", batch.MediaId), ("$file", batch.ReplaceFileId)) == 0)
                throw new ImportOperationException("ReplaceTargetUnbound", 409, "Cancel this task and select a new replacement target.");
            if (Long(c, t, "SELECT COUNT(*) FROM ImportTasks WHERE TaskId=$id AND DeadlineAtUtcMs>$now", ("$id", taskId), ("$now", Now)) != 1)
                throw new ImportOperationException("ImportExpired", 409, "The confirmation deadline has passed.");
            Execute(c, t, "UPDATE ImportBatches SET ExpectedMediaVersion=$version WHERE BatchId=$id", ("$version", expectedVersion), ("$id", batch.BatchId));
            Execute(c, t, "UPDATE ImportTasks SET Status='Queued',Stage='Ready',ErrorCode=NULL WHERE TaskId=$id", ("$id", taskId));
            t.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    public Task CancelTaskAsync(string taskId, CancellationToken ct = default)
    {
        var batch = FindBatch(TaskBatch(taskId))!;
        return CancelBatchAsync(batch.BatchId, [batch.Slots.Single(s => s.TaskId == taskId).Slot], ct);
    }

    public async Task CancelBatchAsync(string batchId, IReadOnlyList<int>? slots = null, CancellationToken ct = default)
    {
        await SweepAsync(ct).ConfigureAwait(false);
        var batch = FindBatch(batchId) ?? throw new KeyNotFoundException("Import batch not found.");
        if (slots is not null && slots.Any(s => s < 0 || s >= batch.Slots.Count))
            throw new ImportOperationException("InvalidSlot", 422, "An upload position is outside this batch.");
        await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            foreach (var slot in batch.Slots.Where(s => slots is null || slots.Contains(s.Slot)))
                EndSlot(c, t, batchId, slot.Slot, "Cancelled", null);
            FinishBatches(c, t, Now);
            t.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        await CleanupTerminalAsync(ct).ConfigureAwait(false);
    }

    private void EndSlot(SqliteConnection c, SqliteTransaction? t, string batch, int slot, string status, string? error)
    {
        Execute(c, t, $"""
            UPDATE ImportTasks SET Status=$status,Stage=NULL,StagePercent=NULL,ErrorCode=$error,FinishedAtUtcMs=COALESCE(FinishedAtUtcMs,$now)
            WHERE BatchId=$batch AND Slot=$slot AND Status NOT IN ({Terminal});
            UPDATE ImportSlots SET Status=$status,ErrorCode=$error,FinishedAtUtcMs=COALESCE(FinishedAtUtcMs,$now)
            WHERE BatchId=$batch AND Slot=$slot AND Status NOT IN ({Terminal});
            """, ("$status", status), ("$error", error), ("$now", Now), ("$batch", batch), ("$slot", slot));
    }
}
