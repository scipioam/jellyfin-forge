using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;
using static Jellyfin.Plugin.Danmuku.Storage.ImportSql;

namespace Jellyfin.Plugin.Danmuku.Import;

public sealed partial class ImportService
{
    /// <summary>Called by the host worker; request cancellation never owns this token.</summary>
    public async Task ProcessPendingAsync(CancellationToken ct = default)
    {
        await _processor.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await SweepAsync(ct).ConfigureAwait(false);
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                string? task;
                using (var c = factory.CreateOpenConnection())
                    task = Text(c, null, "SELECT TaskId FROM ImportTasks WHERE Status='Queued' AND Stage='Received' ORDER BY CreatedAtUtcMs,Slot LIMIT 1");
                if (task is not null) await ParseTaskAsync(task, ct).ConfigureAwait(false);
                await PublishReadyAsync(ct).ConfigureAwait(false);
                if (task is null) break;
            }
        }
        finally { _processor.Release(); }
    }

    private async Task ParseTaskAsync(string taskId, CancellationToken ct)
    {
        var batch = FindBatch(TaskBatch(taskId))!;
        var slot = batch.Slots.Single(s => s.TaskId == taskId);
        string path;
        using (var c = factory.CreateOpenConnection())
            path = Text(c, null, "SELECT UploadPath FROM ImportSlots WHERE TaskId=$id", ("$id", taskId))!;
        if (path is null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _operations[path] = operation;
        try
        {
            var claimed = await writes.EnqueueAsync((c, _) => Task.FromResult(Execute(c, null, """
                UPDATE ImportTasks SET Status='Processing',Stage='Parsing',StagePercent=0,StartedAtUtcMs=$now
                WHERE TaskId=$id AND Status='Queued' AND Stage='Received'
                """, ("$now", Now), ("$id", taskId)) == 1), ct).ConfigureAwait(false);
            if (!claimed) return;
            await ReserveAsync(batch.BatchId, slot.Slot, slot.ReceivedBytes, "Accepted", operation.Token).ConfigureAwait(false);
            using var input = File.OpenRead(files.GetStagingPath(path));
            ParseOutcome outcome;
            long lastTime = 0;
            long assetCredit = 0;
            using (var asset = new FileStream(files.GetStagingPath(path + ".comments"), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536))
            {
                outcome = await new DanmukuFileParser().ParseAsync(input, files.GetStagingPath(path + ".original"), async (comments, token) =>
                {
                    foreach (var comment in comments)
                    {
                        token.ThrowIfCancellationRequested();
                        var bytes = JsonSerializer.SerializeToUtf8Bytes(comment);
                        if (assetCredit < bytes.Length + 1)
                        {
                            var reserve = Math.Max(65536, bytes.Length + 1 - assetCredit);
                            await ReserveAsync(batch.BatchId, slot.Slot, reserve, "Accepted", token).ConfigureAwait(false);
                            assetCredit += reserve;
                        }
                        assetCredit -= bytes.Length + 1;
                        asset.Write(bytes); asset.WriteByte((byte)'\n');
                        lastTime = Math.Max(lastTime, comment.TimeMs);
                    }
                    await RecordParseProgressAsync(taskId, input, comments.Count, 0, token).ConfigureAwait(false);
                }, async (details, token) =>
                {
                    // Reserve conservatively for SQLite rows and their index pages, in addition
                    // to staged originals and exact serialized comment bytes.
                    var reserve = details.Sum(e => 4096L + 2L * Encoding.UTF8.GetByteCount(e.TextSummary ?? "") + e.ReasonCodes.Length * 2L);
                    await ReserveAsync(batch.BatchId, slot.Slot, reserve, "Accepted", token, errorStorage: true).ConfigureAwait(false);
                    await errors.AppendErrorsAsync(taskId, details, token).ConfigureAwait(false);
                    await RecordParseProgressAsync(taskId, input, 0, details.Count, token).ConfigureAwait(false);
                }, operation.Token).ConfigureAwait(false);
                asset.Flush(flushToDisk: true);
            }
            if (!outcome.Accepted)
            {
                await FailSlotAsync(batch.BatchId, slot.Slot, outcome.RejectReason!.Value.ToString()).ConfigureAwait(false);
                return;
            }
            using (var original = new FileStream(files.GetStagingPath(path + ".original"), FileMode.Open, FileAccess.Write)) original.Flush(true);
            await writes.EnqueueAsync((c, _) =>
            {
                Execute(c, null, """
                    UPDATE ImportTasks SET Status=$status,Stage=$stage,StagePercent=NULL,
                        TotalComments=$total,ProcessedComments=$total,NormalComments=$normal,AbnormalComments=$abnormal,
                        DeadlineAtUtcMs=CASE WHEN $abnormal>0 THEN COALESCE(DeadlineAtUtcMs,$deadline) ELSE DeadlineAtUtcMs END,
                        ParseStatisticsJson=$stats,TargetFormat=$format,TargetContentHash=$hash,TargetLastCommentTimeMs=$last
                    WHERE TaskId=$id AND Status='Processing'
                    """, ("$status", outcome.RequiresConfirmation ? "AwaitingConfirmation" : "Queued"),
                    ("$stage", outcome.RequiresConfirmation ? null : "Ready"), ("$total", outcome.Statistics.TotalSourceEntries),
                    ("$normal", outcome.Statistics.NormalEntries), ("$abnormal", outcome.Statistics.AbnormalEntries),
                    ("$deadline", Now + (long)ImportLifecycleLimits.Confirmation.TotalMilliseconds),
                    ("$stats", JsonSerializer.Serialize(outcome.Statistics)), ("$format", outcome.Format!.Value.ToString().ToLowerInvariant()),
                    ("$hash", outcome.Statistics.ContentSha256), ("$last", lastTime), ("$id", taskId));
                return Task.CompletedTask;
            }, operation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            await FailSlotAsync(batch.BatchId, slot.Slot, ex is ImportOperationException business ? business.Code : "ParseFailed").ConfigureAwait(false);
        }
        finally
        {
            _operations.TryRemove(path, out _);
            await CleanupTerminalAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private Task RecordParseProgressAsync(string taskId, FileStream input, int normal, int abnormal, CancellationToken ct) =>
        writes.EnqueueAsync((c, _) =>
        {
            Execute(c, null, """
                UPDATE ImportTasks SET StagePercent=$percent,ProcessedComments=ProcessedComments+$normal+$abnormal,
                    NormalComments=NormalComments+$normal,AbnormalComments=AbnormalComments+$abnormal
                WHERE TaskId=$id AND Status='Processing'
                """, ("$percent", input.Length == 0 ? 0 : Math.Min(99.9, input.Position * 100.0 / input.Length)),
                ("$normal", normal), ("$abnormal", abnormal), ("$id", taskId));
            return Task.CompletedTask;
        }, ct);

    private async Task PublishReadyAsync(CancellationToken ct)
    {
        while (true)
        {
            await SweepAsync(ct).ConfigureAwait(false);
            string? task;
            using (var c = factory.CreateOpenConnection())
                task = Text(c, null, $"""
                    SELECT t.TaskId FROM ImportTasks t WHERE t.Status='Queued' AND t.Stage='Ready'
                    AND NOT EXISTS(SELECT 1 FROM ImportSlots s WHERE s.BatchId=t.BatchId AND s.Slot<t.Slot AND s.Status NOT IN ({Terminal}))
                    ORDER BY t.CreatedAtUtcMs,t.Slot LIMIT 1
                    """);
            if (task is null) return;
            var batch = FindBatch(TaskBatch(task))!;
            var slot = batch.Slots.Single(s => s.TaskId == task);
            try
            {
                await bindings.RequireExistingAsync(batch.MediaId, ct).ConfigureAwait(false);
                var intent = await PrepareIntentAsync(task, ct).ConfigureAwait(false);
                if (intent is null) continue;
                await publish.RegisterIntentAsync(intent, ct).ConfigureAwait(false);
                await writes.EnqueueAsync((c, _) =>
                {
                    Execute(c, null, "UPDATE ImportTasks SET Stage='Saving',StagePercent=0 WHERE TaskId=$id AND Status='Queued' AND Stage='Ready'", ("$id", task));
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
                await publish.PublishAsync(task, ReadCommentsAsync(files.GetStagingPath(intent.StagedAssetPath!), ct), ct).ConfigureAwait(false);
            }
            catch (ImportOperationException ex) when (ex.Code is "MediaVersionConflict" or "ReplaceTargetUnbound")
            {
                await writes.EnqueueAsync((c, _) =>
                {
                    Execute(c, null, """
                        UPDATE ImportTasks SET Status='AwaitingConflictResolution',Stage=NULL,StagePercent=NULL,ErrorCode=$code,
                            DeadlineAtUtcMs=COALESCE(DeadlineAtUtcMs,$deadline) WHERE TaskId=$id AND Status='Queued'
                        """, ("$id", task), ("$code", ex.Code), ("$deadline", Now + (long)ImportLifecycleLimits.Confirmation.TotalMilliseconds));
                    return Task.CompletedTask;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // If commit succeeded but its response was lost, EndSlot's terminal guard keeps
                // Completed and its published file. Never infer rollback from an exception alone.
                await FailSlotAsync(batch.BatchId, slot.Slot, ex is ImportOperationException business ? business.Code : "PublishFailed").ConfigureAwait(false);
            }
            await CleanupTerminalAsync(ct).ConfigureAwait(false);
        }
    }

    private Task<PublishIntentRequest?> PrepareIntentAsync(string taskId, CancellationToken ct) => writes.EnqueueAsync((c, _) =>
    {
        using var cmd = Command(c, null, """
            SELECT b.MediaId,s.OriginalFileName,t.TargetFormat,t.TargetContentHash,t.TargetLastCommentTimeMs,
                t.StagedOriginalPath,t.StagedAssetPath,t.TargetFileId,t.TargetStoredFileName,t.TargetOriginalFileName,t.TargetDisplayName
            FROM ImportTasks t JOIN ImportBatches b ON b.BatchId=t.BatchId JOIN ImportSlots s ON s.BatchId=t.BatchId AND s.Slot=t.Slot
            WHERE t.TaskId=$id AND t.Status='Queued' AND t.Stage='Ready'
            """, ("$id", taskId));
        string media, original, format, hash, staged, asset;
        string? target, stored, targetOriginal, display;
        long? last;
        using (var r = cmd.ExecuteReader())
        {
            if (!r.Read()) return Task.FromResult<PublishIntentRequest?>(null);
            media = r.GetString(0); original = r.GetString(1); format = r.GetString(2); hash = r.GetString(3); last = NullableLong(r, 4);
            staged = r.GetString(5); asset = r.GetString(6); target = NullableText(r, 7); stored = NullableText(r, 8);
            targetOriginal = NullableText(r, 9); display = NullableText(r, 10);
        }
        if (target is not null)
            return Task.FromResult<PublishIntentRequest?>(new(taskId, media, target, targetOriginal!, stored!, display!, format, hash, last, "m1-v1", staged, asset));
        using (var reuse = Command(c, null, "SELECT FileId,StoredFileName,Status FROM Files WHERE ContentHash=$hash ORDER BY ImportedAtUtcMs,FileId LIMIT 1", ("$hash", hash)))
        using (var r = reuse.ExecuteReader())
            if (r.Read())
            {
                if (r.GetString(2) != "Published")
                    throw new ImportOperationException("FileUnavailable", 409, "The matching file is being deleted.");
                target = r.GetString(0);
                stored = r.GetString(1);
            }
        if (target is null)
        {
            target = Guid.NewGuid().ToString("N");
            var safe = SafeFileName(original, format);
            stored = safe;
            var suffix = 0;
            while (File.Exists(files.GetOriginalPath(stored)) || Long(c, null, "SELECT COUNT(*) FROM Files WHERE StoredFileName=$name COLLATE NOCASE", ("$name", stored)) > 0
                || Long(c, null, $"SELECT COUNT(*) FROM ImportTasks WHERE TargetStoredFileName=$name COLLATE NOCASE AND Status NOT IN ({Terminal})", ("$name", stored)) > 0)
                stored = $"{Path.GetFileNameWithoutExtension(safe)} ({++suffix}){Path.GetExtension(safe)}";
        }
        return Task.FromResult<PublishIntentRequest?>(new(taskId, media, target, original, stored!, stored!, format, hash, last, "m1-v1", staged, asset));
    }, ct);

    private static string SafeFileName(string original, string format)
    {
        var name = original.Replace('\\', '/').Split('/')[^1];
        var builder = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            var value = rune.Value < 32 || "<>:\"/\\|?*".Contains(rune.ToString(), StringComparison.Ordinal) ? "_" : rune.ToString();
            if (Encoding.UTF8.GetByteCount(builder.ToString()) + Encoding.UTF8.GetByteCount(value) > 180) break;
            builder.Append(value);
        }
        name = builder.ToString().Trim().TrimEnd('.');
        if (string.IsNullOrWhiteSpace(name)) name = "danmuku." + format;
        return name;
    }

    private static async IAsyncEnumerable<CommentRecord> ReadCommentsAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            yield return JsonSerializer.Deserialize<CommentRecord>(line) ?? throw new InvalidDataException("Missing staged comment.");
    }
}
