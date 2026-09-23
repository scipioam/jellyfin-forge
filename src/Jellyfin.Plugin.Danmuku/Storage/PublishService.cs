using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="IPublishService" />
public sealed class PublishService : IPublishService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IPublishFileStore _fileStore;
    private readonly ISqliteWriteCoordinator _writeCoordinator;
    private readonly TimeProvider _clock;

    public PublishService(
        ISqliteConnectionFactory connectionFactory,
        IPublishFileStore fileStore,
        ISqliteWriteCoordinator writeCoordinator, TimeProvider? clock = null)
    {
        _clock = clock ?? TimeProvider.System;
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
    }

    public Task<PublishOutcome> PublishAsync(string taskId, CancellationToken cancellationToken = default) =>
        PublishAsync(taskId, EmptyComments(), cancellationToken);

    public Task<PublishIntentSnapshot> RegisterIntentAsync(
        PublishIntentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _writeCoordinator.EnqueueAsync(
            (connection, _) =>
            {
                using var transaction = connection.BeginTransaction();
                var task = LoadTask(connection, transaction, request.TaskId)
                    ?? throw new KeyNotFoundException($"Import task '{request.TaskId}' does not exist.");

                if (StorageStatuses.Tasks.IsTerminal(task.Status))
                {
                    throw new InvalidOperationException(
                        $"Import task '{request.TaskId}' is already terminal ({task.Status}).");
                }

                if (!string.Equals(task.MediaId, request.MediaId, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"Import task '{request.TaskId}' belongs to media '{task.MediaId}', not '{request.MediaId}'.");
                }

                var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
                if (task.TargetFileId is not null)
                {
                    if (IntentMatches(task, request))
                    {
                        return Task.FromResult(new PublishIntentSnapshot(
                            request.TaskId,
                            task.MediaId,
                            task.Operation,
                            task.TargetFileId,
                            task.TargetStoredFileName!,
                            task.ReplaceFileId,
                            task.StagedOriginalPath,
                            task.StagedAssetPath,
                            task.IntentCreatedAtUtcMs));
                    }

                    throw new InvalidOperationException(
                        $"Import task '{request.TaskId}' already has a different publish intent.");
                }

                var fileStatus = ReadFileStatus(connection, transaction, request.FileId);
                if (fileStatus is not null && !string.Equals(fileStatus, StorageStatuses.Files.Published, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"File '{request.FileId}' is not published and cannot receive new bindings.");
                }

                Execute(
                    connection,
                    transaction,
                    """
                    UPDATE ImportTasks
                    SET TargetFileId = $fileId,
                        TargetOriginalFileName = $originalFileName,
                        TargetStoredFileName = $storedFileName,
                        TargetDisplayName = $displayName,
                        TargetFormat = $format,
                        TargetContentHash = $contentHash,
                        TargetLastCommentTimeMs = $lastCommentTimeMs,
                        TargetParseDataVersion = $parseDataVersion,
                        StagedOriginalPath = $stagedOriginalPath,
                        StagedAssetPath = $stagedAssetPath,
                        IntentCreatedAtUtcMs = $now
                    WHERE TaskId = $taskId;
                    """,
                    ("$fileId", request.FileId),
                    ("$originalFileName", request.OriginalFileName),
                    ("$storedFileName", request.StoredFileName),
                    ("$displayName", request.DisplayName),
                    ("$format", request.Format),
                    ("$contentHash", request.ContentHash),
                    ("$lastCommentTimeMs", request.LastCommentTimeMs),
                    ("$parseDataVersion", request.ParseDataVersion),
                    ("$stagedOriginalPath", request.StagedOriginalPath),
                    ("$stagedAssetPath", request.StagedAssetPath),
                    ("$now", now),
                    ("$taskId", request.TaskId));

                transaction.Commit();
                return Task.FromResult(new PublishIntentSnapshot(
                    request.TaskId,
                    task.MediaId,
                    task.Operation,
                    request.FileId,
                    request.StoredFileName,
                    task.ReplaceFileId,
                    request.StagedOriginalPath,
                    request.StagedAssetPath,
                    now));
            },
            cancellationToken);
    }

    public async Task<PublishOutcome> PublishAsync(
        string taskId,
        IAsyncEnumerable<CommentRecord> comments,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(comments);

        var task = LoadTask(taskId) ?? throw new KeyNotFoundException($"Import task '{taskId}' does not exist.");
        if (string.Equals(task.Status, StorageStatuses.Tasks.Completed, StringComparison.Ordinal))
        {
            return AlreadyPublished(task);
        }

        if (StorageStatuses.Tasks.IsTerminal(task.Status))
        {
            throw new InvalidOperationException($"Import task '{taskId}' is terminal ({task.Status}) and cannot be published.");
        }

        var plan = RequirePlan(task);

        var outcome = await _writeCoordinator.EnqueueAsync(
            async (connection, token) =>
            {
                using var transaction = connection.BeginTransaction();
                var currentRecord = LoadTask(connection, transaction, taskId)
                    ?? throw new KeyNotFoundException($"Import task '{taskId}' no longer exists.");

                if (string.Equals(currentRecord.Status, StorageStatuses.Tasks.Completed, StringComparison.Ordinal))
                {
                    return AlreadyPublished(currentRecord);
                }

                if (StorageStatuses.Tasks.IsTerminal(currentRecord.Status))
                {
                    throw new InvalidOperationException($"Import task '{taskId}' became terminal ({currentRecord.Status}).");
                }

                var current = RequirePlan(currentRecord);

                if (current.Operation != "import")
                {
                    var expected = ImportSql.Long(connection, transaction,
                        "SELECT ExpectedMediaVersion FROM ImportBatches WHERE BatchId=$id", ("$id", current.BatchId));
                    MediaBindingService.RequireVersion(connection, transaction, current.MediaId!, expected);
                }
                if (currentRecord.Status is "AwaitingConfirmation" or "AwaitingConflictResolution")
                    throw new ImportOperationException("ConfirmationRequired", 409, "The import needs confirmation.");
                if (ImportSql.Long(connection, transaction,
                    "SELECT COUNT(*) FROM ImportTasks WHERE TaskId=$id AND DeadlineAtUtcMs<=$now",
                    ("$id", taskId), ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds())) > 0)
                    throw new ImportOperationException("ImportExpired", 409, "The import deadline has passed.");
                if (ImportSql.Long(connection, transaction,
                    $"SELECT COUNT(*) FROM ImportSlots WHERE BatchId=$id AND Slot<$slot AND Status NOT IN ({ImportSql.Terminal})",
                    ("$id", current.BatchId), ("$slot", current.Slot)) > 0)
                    throw new ImportOperationException("WaitingForPrevious", 409, "A preceding upload position has not finished.");
                if (IsReplacement(current) && ImportSql.Long(connection, transaction,
                    "SELECT COUNT(*) FROM MediaBindings WHERE MediaId=$media AND FileId=$file",
                    ("$media", current.MediaId), ("$file", current.ReplaceFileId)) == 0)
                    throw new ImportOperationException("ReplaceTargetUnbound", 409, "Cancel and choose a new replacement target.");

                var sameFileReplacement = IsReplacement(current) && current.ReplaceFileId == current.FileId;
                var existingStatus = ReadFileStatus(connection, transaction, current.FileId);
                var existingFile = existingStatus is not null;
                if (existingFile)
                {
                    if (existingStatus != StorageStatuses.Files.Published || ImportSql.Text(connection, transaction,
                        "SELECT ContentHash FROM Files WHERE FileId=$id", ("$id", current.FileId)) != current.ContentHash)
                        throw new ImportOperationException("FileUnavailable", 409, "The reusable file is no longer available.");
                }
                else
                {
                    if (sameFileReplacement || current.StagedOriginalPath is null)
                        throw new InvalidOperationException("No staged original is available for publication.");
                    // The intent is already durable. Version and terminal checks precede the move;
                    // the shared writer serializes cancellation, binding edits, deletion and publish.
                    _fileStore.MoveStagedOriginalToOriginals(current.StagedOriginalPath, current.StoredFileName);
                }

                if (!existingFile)
                {
                    InsertFile(connection, transaction, current, commentCount: 0);
                }

                var importedComments = !existingFile && !sameFileReplacement
                    ? await InsertCommentsAsync(connection, transaction, current.FileId, comments, token).ConfigureAwait(false)
                    : 0;

                if (!existingFile)
                {
                    UpdateFileCommentCount(connection, transaction, current.FileId, importedComments);
                }

                var sameFileReplacementInsideTransaction = IsReplacement(current)
                    && string.Equals(current.ReplaceFileId, current.FileId, StringComparison.Ordinal);
                var (bindingCreated, activeFileChanged) = ApplyBindingAndState(
                    connection,
                    transaction,
                    current,
                    sameFileReplacementInsideTransaction);

                if (current.Operation != "import") ImportSql.Execute(connection, transaction,
                    "UPDATE ImportBatches SET ExpectedMediaVersion=(SELECT Version FROM MediaState WHERE MediaId=$media) WHERE BatchId=$batch",
                    ("$media", current.MediaId), ("$batch", current.BatchId));
                if (existingFile)
                    importedComments = (int)ImportSql.Long(connection, transaction, "SELECT CommentCount FROM Files WHERE FileId=$id", ("$id", current.FileId));
                var resultCode = current.Operation == "import" ? (existingFile ? "Reused" : "Imported") : sameFileReplacement ? "Unchanged" : IsReplacement(current) ? "Replaced"
                    : !bindingCreated ? "AlreadyBound" : existingFile ? "Reused" : "Imported";
                CompleteTask(connection, transaction, current, importedComments, resultCode);
                CompleteSlotAndBatch(connection, transaction, current);

                transaction.Commit();
                return new PublishOutcome(
                    PublishOutcomeKind.Published,
                    taskId,
                    current.MediaId,
                    current.FileId,
                    bindingCreated,
                    activeFileChanged,
                    importedComments);
            },
            cancellationToken).ConfigureAwait(false);

        if (plan.StagedOriginalPath is not null)
        {
            // Any remaining staging copy is no longer needed after commit.
            _fileStore.TryDeleteStagingFile(plan.StagedOriginalPath);
        }

        if (plan.StagedAssetPath is not null) _fileStore.TryDeleteStagedAsset(plan.StagedAssetPath);
        return outcome;
    }

    private static bool IsReplacement(PublishPlan plan) =>
        string.Equals(plan.Operation, "replace", StringComparison.Ordinal) && plan.ReplaceFileId is not null;

    private static PublishOutcome AlreadyPublished(TaskRecord task) =>
        new(
            PublishOutcomeKind.AlreadyPublished,
            task.TaskId,
            task.MediaId,
            task.FileId ?? task.TargetFileId ?? string.Empty,
            false,
            false,
            0);

    private static bool IntentMatches(TaskRecord task, PublishIntentRequest request) =>
        string.Equals(task.TargetFileId, request.FileId, StringComparison.Ordinal)
        && string.Equals(task.TargetStoredFileName, request.StoredFileName, StringComparison.Ordinal)
        && string.Equals(task.TargetOriginalFileName, request.OriginalFileName, StringComparison.Ordinal)
        && string.Equals(task.TargetDisplayName, request.DisplayName, StringComparison.Ordinal)
        && string.Equals(task.TargetFormat, request.Format, StringComparison.Ordinal)
        && string.Equals(task.TargetContentHash, request.ContentHash, StringComparison.Ordinal)
        && string.Equals(task.TargetParseDataVersion, request.ParseDataVersion, StringComparison.Ordinal)
        && task.TargetLastCommentTimeMs == request.LastCommentTimeMs
        && string.Equals(task.StagedOriginalPath, request.StagedOriginalPath, StringComparison.Ordinal)
        && string.Equals(task.StagedAssetPath, request.StagedAssetPath, StringComparison.Ordinal);

    private static PublishPlan RequirePlan(TaskRecord task)
    {
        if (task.TargetFileId is null
            || task.TargetOriginalFileName is null
            || task.TargetStoredFileName is null
            || task.TargetDisplayName is null
            || task.TargetFormat is null
            || task.TargetContentHash is null
            || task.TargetParseDataVersion is null)
        {
            throw new InvalidOperationException($"Import task '{task.TaskId}' has no complete publish intent.");
        }

        return new PublishPlan(
            task.TaskId,
            task.BatchId,
            task.Slot,
            task.Status,
            task.MediaId,
            task.Operation,
            task.ReplaceFileId,
            task.TargetFileId,
            task.TargetOriginalFileName,
            task.TargetStoredFileName,
            task.TargetDisplayName,
            task.TargetFormat,
            task.TargetContentHash,
            task.TargetLastCommentTimeMs,
            task.TargetParseDataVersion,
            task.StagedOriginalPath,
            task.StagedAssetPath);
    }

    private TaskRecord? LoadTask(string taskId)
    {
        using var connection = _connectionFactory.CreateOpenConnection();
        return LoadTask(connection, null, taskId);
    }

    private static TaskRecord? LoadTask(SqliteConnection connection, SqliteTransaction? transaction, string taskId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT t.TaskId, t.BatchId, t.Slot, t.Status,
                   t.TargetFileId, t.TargetOriginalFileName, t.TargetStoredFileName, t.TargetDisplayName,
                   t.TargetFormat, t.TargetContentHash, t.TargetLastCommentTimeMs, t.TargetParseDataVersion,
                   t.StagedOriginalPath, t.StagedAssetPath, t.IntentCreatedAtUtcMs, t.FileId,
                   b.MediaId, b.Operation, b.ReplaceFileId
            FROM ImportTasks t
            INNER JOIN ImportBatches b ON b.BatchId = t.BatchId
            WHERE t.TaskId = $taskId;
            """;
        command.Parameters.AddWithValue("$taskId", taskId);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new TaskRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetInt32(2),
            reader.GetString(3),
            GetNullableString(reader, 4),
            GetNullableString(reader, 5),
            GetNullableString(reader, 6),
            GetNullableString(reader, 7),
            GetNullableString(reader, 8),
            GetNullableString(reader, 9),
            GetNullableInt64(reader, 10),
            GetNullableString(reader, 11),
            GetNullableString(reader, 12),
            GetNullableString(reader, 13),
            GetNullableInt64(reader, 14),
            GetNullableString(reader, 15),
            GetNullableString(reader, 16),
            reader.GetString(17),
            GetNullableString(reader, 18));
    }

    private static string? ReadFileStatus(SqliteConnection connection, SqliteTransaction transaction, string fileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Status FROM Files WHERE FileId = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId);
        return command.ExecuteScalar() as string;
    }

    private void InsertFile(SqliteConnection connection, SqliteTransaction transaction, PublishPlan plan, int commentCount)
    {
        Execute(
            connection,
            transaction,
            """
            INSERT INTO Files (FileId, OriginalFileName, StoredFileName, DisplayName, Format, ContentHash, ImportedAtUtcMs, CommentCount, LastCommentTimeMs, ParseDataVersion, Status, DeleteRequestedAtUtcMs)
            VALUES ($fileId, $originalFileName, $storedFileName, $displayName, $format, $contentHash, $now, $commentCount, $lastCommentTimeMs, $parseDataVersion, 'Published', NULL);
            """,
            ("$fileId", plan.FileId),
            ("$originalFileName", plan.OriginalFileName),
            ("$storedFileName", plan.StoredFileName),
            ("$displayName", plan.DisplayName),
            ("$format", plan.Format),
            ("$contentHash", plan.ContentHash),
            ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$commentCount", commentCount),
            ("$lastCommentTimeMs", plan.LastCommentTimeMs),
            ("$parseDataVersion", plan.ParseDataVersion));
    }

    private static void UpdateFileCommentCount(SqliteConnection connection, SqliteTransaction transaction, string fileId, int commentCount)
    {
        Execute(
            connection,
            transaction,
            "UPDATE Files SET CommentCount = $commentCount WHERE FileId = $fileId;",
            ("$commentCount", commentCount),
            ("$fileId", fileId));
    }

    private static async Task<int> InsertCommentsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string fileId,
        IAsyncEnumerable<CommentRecord> comments,
        CancellationToken cancellationToken)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Comments (FileId, SourceOrdinal, SourceId, TimeMs, Text, Color, Mode, FontSize, SourceTimeMs, SenderHash, SourceNumericId, Weight, Attr, Pool)
            VALUES ($fileId, $sourceOrdinal, $sourceId, $timeMs, $text, $color, $mode, $fontSize, $sourceTimeMs, $senderHash, $sourceNumericId, $weight, $attr, $pool);
            """;
        var fileIdParameter = command.Parameters.Add("$fileId", SqliteType.Text);
        var sourceOrdinalParameter = command.Parameters.Add("$sourceOrdinal", SqliteType.Integer);
        var sourceIdParameter = command.Parameters.Add("$sourceId", SqliteType.Text);
        var timeMsParameter = command.Parameters.Add("$timeMs", SqliteType.Integer);
        var textParameter = command.Parameters.Add("$text", SqliteType.Text);
        var colorParameter = command.Parameters.Add("$color", SqliteType.Integer);
        var modeParameter = command.Parameters.Add("$mode", SqliteType.Integer);
        var fontSizeParameter = command.Parameters.Add("$fontSize", SqliteType.Integer);
        var sourceTimeMsParameter = command.Parameters.Add("$sourceTimeMs", SqliteType.Integer);
        var senderHashParameter = command.Parameters.Add("$senderHash", SqliteType.Text);
        var sourceNumericIdParameter = command.Parameters.Add("$sourceNumericId", SqliteType.Text);
        var weightParameter = command.Parameters.Add("$weight", SqliteType.Integer);
        var attrParameter = command.Parameters.Add("$attr", SqliteType.Integer);
        var poolParameter = command.Parameters.Add("$pool", SqliteType.Integer);

        fileIdParameter.Value = fileId;
        var count = 0;
        await foreach (var comment in comments.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            sourceOrdinalParameter.Value = comment.SourceOrdinal;
            sourceIdParameter.Value = (object?)comment.SourceId ?? DBNull.Value;
            timeMsParameter.Value = comment.TimeMs;
            textParameter.Value = comment.Text;
            colorParameter.Value = comment.Color;
            modeParameter.Value = comment.Mode;
            fontSizeParameter.Value = comment.FontSize;
            sourceTimeMsParameter.Value = (object?)comment.SourceTimeMs ?? DBNull.Value;
            senderHashParameter.Value = (object?)comment.SenderHash ?? DBNull.Value;
            sourceNumericIdParameter.Value = (object?)comment.SourceNumericId ?? DBNull.Value;
            weightParameter.Value = (object?)comment.Weight ?? DBNull.Value;
            attrParameter.Value = (object?)comment.Attr ?? DBNull.Value;
            poolParameter.Value = (object?)comment.Pool ?? DBNull.Value;
            command.ExecuteNonQuery();
            count++;
        }

        return count;
    }

    private (bool BindingCreated, bool ActiveFileChanged) ApplyBindingAndState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        PublishPlan plan,
        bool sameFileReplacement)
    {
        if (plan.Operation == "import" || sameFileReplacement)
        {
            // B equals A: keep the binding, active selection and files untouched.
            return (false, false);
        }

        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        var priorBindings = ImportSql.Long(connection, transaction, "SELECT COUNT(*) FROM MediaBindings WHERE MediaId=$id", ("$id", plan.MediaId));
        var bindingCreated = InsertBinding(connection, transaction, plan.MediaId!, plan.FileId, now);

        if (!IsReplacement(plan))
        {
            var state = LoadMediaState(connection, transaction, plan.MediaId!);
            if (state is null)
            {
                InsertMediaState(connection, transaction, plan.MediaId!, plan.FileId, isDeactivated: 0, version: 1);
                return (bindingCreated, true);
            }
            var activate = priorBindings == 0 && state.IsDeactivated == 0 && state.ActiveFileId is null;
            if (bindingCreated)
                Execute(connection, transaction,
                    "UPDATE MediaState SET ActiveFileId=CASE WHEN $activate=1 THEN $file ELSE ActiveFileId END,Version=Version+1 WHERE MediaId=$media",
                    ("$activate", activate ? 1 : 0), ("$file", plan.FileId), ("$media", plan.MediaId));
            return (bindingCreated, bindingCreated && activate);
        }

        var removed = Execute(
            connection,
            transaction,
            "DELETE FROM MediaBindings WHERE MediaId = $mediaId AND FileId = $replaceFileId;",
            ("$mediaId", plan.MediaId),
            ("$replaceFileId", plan.ReplaceFileId));
        if (removed == 0)
        {
            throw new InvalidOperationException(
                $"Replace target '{plan.ReplaceFileId}' is no longer bound to media '{plan.MediaId}'.");
        }

        var replacedState = LoadMediaState(connection, transaction, plan.MediaId!);
        if (replacedState is null)
        {
            InsertMediaState(connection, transaction, plan.MediaId!, null, isDeactivated: 0, version: 1);
            return (bindingCreated, false);
        }

        if (string.Equals(replacedState.ActiveFileId, plan.ReplaceFileId, StringComparison.Ordinal))
        {
            Execute(
                connection,
                transaction,
                "UPDATE MediaState SET ActiveFileId = $activeFileId, Version = Version + 1 WHERE MediaId = $mediaId;",
                ("$activeFileId", plan.FileId),
                ("$mediaId", plan.MediaId));
            return (bindingCreated, true);
        }

        Execute(connection, transaction, "UPDATE MediaState SET Version=Version+1 WHERE MediaId=$media", ("$media", plan.MediaId));
        return (bindingCreated, false);
    }

    private static bool InsertBinding(SqliteConnection connection, SqliteTransaction transaction, string mediaId, string fileId, long now) =>
        Execute(
            connection,
            transaction,
            "INSERT OR IGNORE INTO MediaBindings (MediaId, FileId, BoundAtUtcMs) VALUES ($mediaId, $fileId, $now);",
            ("$mediaId", mediaId),
            ("$fileId", fileId),
            ("$now", now)) > 0;

    private static MediaStateRecord? LoadMediaState(SqliteConnection connection, SqliteTransaction transaction, string mediaId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT ActiveFileId, IsDeactivated FROM MediaState WHERE MediaId = $mediaId;";
        command.Parameters.AddWithValue("$mediaId", mediaId);
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new MediaStateRecord(GetNullableString(reader, 0), reader.GetInt32(1))
            : null;
    }

    private static void InsertMediaState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string mediaId,
        string? activeFileId,
        int isDeactivated,
        int version)
    {
        Execute(
            connection,
            transaction,
            "INSERT INTO MediaState (MediaId, ActiveFileId, IsDeactivated, Version) VALUES ($mediaId, $activeFileId, $isDeactivated, $version);",
            ("$mediaId", mediaId),
            ("$activeFileId", activeFileId),
            ("$isDeactivated", isDeactivated),
            ("$version", version));
    }

    private void CompleteTask(SqliteConnection connection, SqliteTransaction transaction, PublishPlan plan, int importedComments, string resultCode)
    {
        Execute(
            connection,
            transaction,
            """
            UPDATE ImportTasks
            SET Status = 'Completed',
                Stage = 'Completed',
                ResultCode = $resultCode,
                StagePercent = 100,
                FileId = $fileId,
                ImportedComments = $importedComments,
                ErrorCode = NULL,
                FinishedAtUtcMs = $now,
                TargetFileId = NULL,
                TargetStoredFileName = NULL,
                StagedOriginalPath = NULL,
                StagedAssetPath = NULL
            WHERE TaskId = $taskId;
            """,
            ("$fileId", plan.FileId),
            ("$importedComments", importedComments),
            ("$resultCode", resultCode),
            ("$now", _clock.GetUtcNow().ToUnixTimeMilliseconds()),
            ("$taskId", plan.TaskId));
    }

    private void CompleteSlotAndBatch(SqliteConnection connection, SqliteTransaction transaction, PublishPlan plan)
    {
        var now = _clock.GetUtcNow().ToUnixTimeMilliseconds();
        Execute(
            connection,
            transaction,
            "UPDATE ImportSlots SET Status = 'Completed', FinishedAtUtcMs = $now WHERE BatchId = $batchId AND Slot = $slot AND Status = 'Accepted';",
            ("$now", now),
            ("$batchId", plan.BatchId),
            ("$slot", plan.Slot));

        Execute(
            connection,
            transaction,
            """
            UPDATE ImportBatches
            SET Status = 'Finished', FinishedAtUtcMs = $now
            WHERE BatchId = $batchId
              AND Status = 'Open'
              AND NOT EXISTS (
                  SELECT 1 FROM ImportSlots s
                  WHERE s.BatchId = $batchId
                    AND s.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Expired', 'Interrupted'));
            """,
            ("$now", now),
            ("$batchId", plan.BatchId));
    }

    private static int Execute(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return command.ExecuteNonQuery();
    }

    private static string? GetNullableString(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static long? GetNullableInt64(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal);

    private static async IAsyncEnumerable<CommentRecord> EmptyComments()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }

    private sealed record TaskRecord(
        string TaskId,
        string BatchId,
        int Slot,
        string Status,
        string? TargetFileId,
        string? TargetOriginalFileName,
        string? TargetStoredFileName,
        string? TargetDisplayName,
        string? TargetFormat,
        string? TargetContentHash,
        long? TargetLastCommentTimeMs,
        string? TargetParseDataVersion,
        string? StagedOriginalPath,
        string? StagedAssetPath,
        long? IntentCreatedAtUtcMs,
        string? FileId,
        string? MediaId,
        string Operation,
        string? ReplaceFileId);

    private sealed record PublishPlan(
        string TaskId,
        string BatchId,
        int Slot,
        string Status,
        string? MediaId,
        string Operation,
        string? ReplaceFileId,
        string FileId,
        string OriginalFileName,
        string StoredFileName,
        string DisplayName,
        string Format,
        string ContentHash,
        long? LastCommentTimeMs,
        string ParseDataVersion,
        string? StagedOriginalPath,
        string? StagedAssetPath);

    private sealed record MediaStateRecord(string? ActiveFileId, int IsDeactivated);
}
