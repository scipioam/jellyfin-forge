using Jellyfin.Plugin.Danmuku.Storage;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Import;

public sealed partial class ImportService
{
    // The shared writer owns this cursor. Cleanup remains bounded even if many failed
    // terminal uploads are retained, and failed paths cannot starve later candidates.
    private string _cleanupBatchCursor = "";
    private int _cleanupSlotCursor = -1;
    public async Task SweepAsync(CancellationToken ct = default)
    {
        await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            var expired = new List<(string Batch, int Slot, string Status, string Code)>();
            using (var cmd = Command(c, t, """
                SELECT s.BatchId,s.Slot,s.Status,t.DeadlineAtUtcMs FROM ImportSlots s
                LEFT JOIN ImportTasks t ON t.TaskId=s.TaskId
                WHERE (s.Status='PendingUpload' AND s.UploadDeadlineAtUtcMs<=$now)
                   OR (s.Status='Receiving' AND (s.ReceivingDeadlineAtUtcMs<=$now OR s.LastProgressAtUtcMs<=$idle))
                   OR (s.Status='Accepted' AND t.DeadlineAtUtcMs<=$now)
                """, ("$now", Now), ("$idle", Now - (long)ImportLifecycleLimits.ReceiveIdle.TotalMilliseconds)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) expired.Add((r.GetString(0), r.GetInt32(1), r.GetString(2) == "Receiving" ? "Failed" : "Expired",
                    r.GetString(2) == "Receiving" ? "ReceiveTimeout" : "ImportExpired"));
            foreach (var item in expired) EndSlot(c, t, item.Batch, item.Slot, item.Status, item.Code);
            FinishBatches(c, t, Now);
            t.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
        await CleanupTerminalAsync(ct).ConfigureAwait(false);
        await writes.EnqueueAsync((c, _) =>
        {
            using var t = c.BeginTransaction();
            var cutoff = Now - (long)ImportLifecycleLimits.Retention.TotalMilliseconds;
            // Do not erase ownership evidence while disk cleanup is still pending.
            Execute(c, t, $"""
                UPDATE ImportSlots SET TaskId=NULL WHERE TaskId IN
                (SELECT t.TaskId FROM ImportTasks t JOIN ImportSlots s ON s.BatchId=t.BatchId AND s.Slot=t.Slot
                 WHERE t.Status IN ({Terminal}) AND t.FinishedAtUtcMs<=$cutoff AND s.UploadPath IS NULL AND s.TemporaryBytes=0
                   AND (t.ErrorCode IS NULL OR t.ErrorCode<>'RecoveryCleanupFailed'));
                DELETE FROM ImportTasks WHERE Status IN ({Terminal}) AND FinishedAtUtcMs<=$cutoff
                    AND (ErrorCode IS NULL OR ErrorCode<>'RecoveryCleanupFailed')
                    AND NOT EXISTS(SELECT 1 FROM ImportSlots s WHERE s.TaskId=ImportTasks.TaskId);
                DELETE FROM ImportBatches WHERE Status='Finished' AND FinishedAtUtcMs<=$cutoff
                    AND NOT EXISTS(SELECT 1 FROM ImportSlots s WHERE s.BatchId=ImportBatches.BatchId AND (s.UploadPath IS NOT NULL OR s.TemporaryBytes>0 OR s.TaskId IS NOT NULL));
                DELETE FROM BindingCheckJobs WHERE FinishedAtUtcMs<=$cutoff;
                """, ("$cutoff", cutoff));
            t.Commit();
            return Task.CompletedTask;
        }, ct).ConfigureAwait(false);
    }

    private Task CleanupTerminalAsync(CancellationToken ct) => writes.EnqueueAsync((c, _) =>
    {
        var pending = new List<(string Batch, int Slot, string Path, string? Task, string? Target, string? Name)>();
        for (var attempt = 0; attempt < 2 && pending.Count == 0; attempt++)
        {
            using (var cmd = Command(c, null, $"""
                SELECT s.BatchId,s.Slot,s.UploadPath,s.TaskId,t.TargetFileId,t.TargetStoredFileName
                FROM ImportSlots s LEFT JOIN ImportTasks t ON t.TaskId=s.TaskId
                WHERE s.Status IN ({Terminal}) AND s.UploadPath IS NOT NULL
                  AND (s.BatchId>$cursor OR (s.BatchId=$cursor AND s.Slot>$slot))
                ORDER BY s.BatchId,s.Slot LIMIT 100
                """, ("$cursor", _cleanupBatchCursor), ("$slot", _cleanupSlotCursor)))
            using (var r = cmd.ExecuteReader())
                while (r.Read()) pending.Add((r.GetString(0), r.GetInt32(1), r.GetString(2), NullableText(r, 3), NullableText(r, 4), NullableText(r, 5)));
            if (pending.Count == 0) { _cleanupBatchCursor = ""; _cleanupSlotCursor = -1; }
        }
        foreach (var item in pending)
        {
            _cleanupBatchCursor = item.Batch;
            _cleanupSlotCursor = item.Slot;
            if (_operations.TryGetValue(item.Path, out var operation))
            {
                try { operation.Cancel(); } catch (ObjectDisposedException) { }
                continue; // The owning operation cleans up after disposing its streams.
            }
            var cleaned = files.TryDeleteStagingFile(item.Path);
            cleaned &= files.TryDeleteStagingFile(item.Path + ".original");
            cleaned &= files.TryDeleteStagedAsset(item.Path + ".comments");
            if (item.Target is not null && item.Name is not null && Long(c, null,
                "SELECT COUNT(*) FROM Files WHERE FileId=$id OR StoredFileName=$name", ("$id", item.Target), ("$name", item.Name)) == 0)
                cleaned &= files.TryDeleteOriginal(item.Name);
            Execute(c, null, "UPDATE ImportSlots SET CleanupError=$error WHERE BatchId=$batch AND Slot=$slot",
                ("$error", cleaned ? null : "StagingCleanupFailed"), ("$batch", item.Batch), ("$slot", item.Slot));
            if (cleaned)
            {
                Execute(c, null, "UPDATE ImportSlots SET TemporaryBytes=0,UploadPath=NULL WHERE BatchId=$batch AND Slot=$slot",
                    ("$batch", item.Batch), ("$slot", item.Slot));
                if (item.Task is not null)
                    Execute(c, null, "UPDATE ImportTasks SET StagedOriginalPath=NULL,StagedAssetPath=NULL,ErrorCode=CASE WHEN ErrorCode='RecoveryCleanupFailed' THEN NULL ELSE ErrorCode END WHERE TaskId=$id", ("$id", item.Task));
            }
        }
        return Task.CompletedTask;
    }, ct);
}
