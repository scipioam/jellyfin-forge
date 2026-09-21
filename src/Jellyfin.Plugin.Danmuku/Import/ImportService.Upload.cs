using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Import;

public sealed partial class ImportService
{
    public async Task<ImportSlotSnapshot> ReceiveAsync(string batchId, int slot, Stream source, long? totalBytes = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (totalBytes < 0) throw new ImportOperationException("InvalidLength", 422, "The file length must be nonnegative.");
        await SweepAsync(ct).ConfigureAwait(false);
        var path = await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            var status = Text(c, t, "SELECT Status FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot", ("$batch", batchId), ("$slot", slot))
                ?? throw new KeyNotFoundException("Import position not found.");
            if (status != "PendingUpload") return Task.FromResult<string?>(null);
            if (Long(c, t, "SELECT UploadDeadlineAtUtcMs FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot", ("$batch", batchId), ("$slot", slot)) <= Now)
            {
                EndSlot(c, t, batchId, slot, "Expired", "UploadExpired"); FinishBatches(c, t, Now); t.Commit();
                return Task.FromResult<string?>(null);
            }
            if (totalBytes > DanmukuImportLimits.MaxFileBytes)
            {
                EndSlot(c, t, batchId, slot, "Failed", "FileTooLarge"); FinishBatches(c, t, Now); t.Commit();
                return Task.FromResult<string?>(null);
            }
            if (Long(c, t, "SELECT COUNT(*) FROM ImportSlots WHERE Status='Receiving'") >= 2)
                throw new ImportOperationException("ReceiveCapacity", 503, "Both receive positions are busy; retry later.");
            var upload = Text(c, t, "SELECT UploadPath FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot", ("$batch", batchId), ("$slot", slot))!;
            Execute(c, t, """
                UPDATE ImportSlots SET Status='Receiving',TotalBytes=$total,ReceivingStartedAtUtcMs=$now,
                    LastProgressAtUtcMs=$now,ReceivingDeadlineAtUtcMs=$deadline
                WHERE BatchId=$batch AND Slot=$slot
                """, ("$total", totalBytes), ("$now", Now), ("$deadline", Now + (long)ImportLifecycleLimits.Receive.TotalMilliseconds),
                ("$batch", batchId), ("$slot", slot));
            t.Commit();
            return Task.FromResult<string?>(upload);
        }, ct).ConfigureAwait(false);
        if (path is null) return FindBatch(batchId)!.Slots.Single(s => s.Slot == slot);

        using var deadline = new CancellationTokenSource(ImportLifecycleLimits.Receive, clock);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        _operations[path] = operation;
        try
        {
            // Cancellation may have won after the Receiving transaction but before this
            // method registered its in-process owner. Recheck before creating any file.
            var stillReceiving = await writes.EnqueueAsync((c, _) => Task.FromResult(
                Text(c, null, "SELECT Status FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot",
                    ("$batch", batchId), ("$slot", slot)) == "Receiving"), operation.Token).ConfigureAwait(false);
            if (!stillReceiving) return FindBatch(batchId)!.Slots.Single(s => s.Slot == slot);
            using (var destination = new FileStream(files.GetStagingPath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
            {
                var buffer = new byte[65536];
                long received = 0;
                while (true)
                {
                    using var idle = new CancellationTokenSource(ImportLifecycleLimits.ReceiveIdle, clock);
                    using var readToken = CancellationTokenSource.CreateLinkedTokenSource(operation.Token, idle.Token);
                    var read = await source.ReadAsync(buffer, readToken.Token).AsTask()
                        .WaitAsync(ImportLifecycleLimits.ReceiveIdle, clock, operation.Token).ConfigureAwait(false);
                    if (read == 0) break;
                    if (received + read > DanmukuImportLimits.MaxFileBytes)
                        throw new ImportOperationException("FileTooLarge", 413, "The file exceeds 50 MiB.");
                    if (totalBytes is not null && received + read > totalBytes)
                        throw new ImportOperationException("LengthMismatch", 422, "Received more bytes than declared.");
                    await ReserveAsync(batchId, slot, read, "Receiving", operation.Token).ConfigureAwait(false);
                    await destination.WriteAsync(buffer.AsMemory(0, read), operation.Token).ConfigureAwait(false);
                    received += read;
                    await writes.EnqueueAsync((c, _) =>
                    {
                        if (Execute(c, null, """
                            UPDATE ImportSlots SET ReceivedBytes=$received,LastProgressAtUtcMs=$now
                            WHERE BatchId=$batch AND Slot=$slot AND Status='Receiving' AND ReceivingDeadlineAtUtcMs>$now
                            """, ("$received", received), ("$now", Now), ("$batch", batchId), ("$slot", slot)) != 1)
                            throw new OperationCanceledException("The upload position is no longer receiving.");
                        return Task.CompletedTask;
                    }, operation.Token).ConfigureAwait(false);
                }
                if (totalBytes is not null && received != totalBytes)
                    throw new ImportOperationException("UploadIncomplete", 422, "The upload ended before its declared length.");
                destination.Flush(flushToDisk: true);
            }

            // Once the source is complete, client disconnect no longer owns accepted work.
            await writes.EnqueueAsync((c, _) =>
            {
                using var t = c.BeginTransaction();
                if (Text(c, t, "SELECT Status FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot", ("$batch", batchId), ("$slot", slot)) != "Receiving")
                    return Task.CompletedTask;
                if (Long(c, t, "SELECT ReceivingDeadlineAtUtcMs FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot", ("$batch", batchId), ("$slot", slot)) <= Now)
                { EndSlot(c, t, batchId, slot, "Failed", "ReceiveTimeout"); FinishBatches(c, t, Now); t.Commit(); return Task.CompletedTask; }
                var task = Guid.NewGuid().ToString("N");
                Execute(c, t, """
                    INSERT INTO ImportTasks(TaskId,BatchId,Slot,Status,Stage,CreatedAtUtcMs,StagedOriginalPath,StagedAssetPath)
                    VALUES($task,$batch,$slot,'Queued','Received',$now,$original,$asset);
                    UPDATE ImportSlots SET Status='Accepted',TaskId=$task WHERE BatchId=$batch AND Slot=$slot;
                    """, ("$task", task), ("$batch", batchId), ("$slot", slot), ("$now", Now),
                    ("$original", path + ".original"), ("$asset", path + ".comments"));
                t.Commit();
                return Task.CompletedTask;
            }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or TimeoutException or ImportOperationException or UnauthorizedAccessException)
        {
            await FailSlotAsync(batchId, slot, ex is ImportOperationException business ? business.Code
                : deadline.IsCancellationRequested || ex is TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested && !operation.IsCancellationRequested) ? "ReceiveTimeout" : "UploadInterrupted").ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(path, out _);
            await CleanupTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return FindBatch(batchId)!.Slots.Single(s => s.Slot == slot);
    }

    private Task ReserveAsync(string batch, int slot, long bytes, string slotStatus, CancellationToken ct, bool errorStorage = false) =>
        writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            if (Text(c, t, "SELECT Status FROM ImportSlots WHERE BatchId=$batch AND Slot=$slot", ("$batch", batch), ("$slot", slot)) != slotStatus)
                throw new OperationCanceledException("This upload position is terminal.");
            if (Long(c, t, "SELECT COALESCE((SELECT SUM(TemporaryBytes) FROM ImportSlots),0)+COALESCE((SELECT SUM(ErrorStorageBytes) FROM ImportTasks),0)") + bytes > ImportLifecycleLimits.TemporaryBytes)
                throw new ImportOperationException("StagingCapacity", 503, "The staging storage budget is temporarily exhausted.");
            if (errorStorage)
                Execute(c, t, "UPDATE ImportTasks SET ErrorStorageBytes=ErrorStorageBytes+$bytes WHERE BatchId=$batch AND Slot=$slot",
                    ("$bytes", bytes), ("$batch", batch), ("$slot", slot));
            else
                Execute(c, t, "UPDATE ImportSlots SET TemporaryBytes=TemporaryBytes+$bytes WHERE BatchId=$batch AND Slot=$slot",
                    ("$bytes", bytes), ("$batch", batch), ("$slot", slot));
            t.Commit();
            return Task.CompletedTask;
        }, ct);

    private Task FailSlotAsync(string batch, int slot, string code) => writes.EnqueueAsync((c, _) =>
    {
        using var t = c.BeginTransaction();
        EndSlot(c, t, batch, slot, "Failed", code); FinishBatches(c, t, Now); t.Commit();
        return Task.CompletedTask;
    });
}
