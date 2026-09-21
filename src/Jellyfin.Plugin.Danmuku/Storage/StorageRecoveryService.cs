using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="IStorageRecoveryService" />
/// <remarks>
/// The classification of a publish is decided only by persisted transaction results:
/// a transaction that committed leaves the task at 'Completed' together with its Files
/// row; anything else is an uncommitted intent and is interrupted and cleaned.
/// </remarks>
public sealed class StorageRecoveryService : IStorageRecoveryService
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IPublishFileStore _fileStore;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public StorageRecoveryService(
        ISqliteConnectionFactory connectionFactory,
        IPublishFileStore fileStore,
        ISqliteWriteCoordinator writeCoordinator)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
    }

    public Task<StorageRecoveryReport> RunAsync(CancellationToken cancellationToken = default) =>
        _writeCoordinator.EnqueueAsync(
            (connection, _) =>
            {
                var stagingCleaned = 0;
                var originalsCleaned = 0;
                var retriedCleanups = 0;
                var cleanupFailures = 0;

                // Retry cleanups that failed on an earlier startup.
                var retrySucceeded = new List<string>();
                foreach (var task in QueryTasks(connection, """
                    WHERE t.Status = 'Interrupted'
                      AND t.ErrorCode = 'RecoveryCleanupFailed'
                      AND t.TargetFileId IS NOT NULL
                    """))
                {
                    if (CleanupIntent(connection, task, ref stagingCleaned, ref originalsCleaned))
                    {
                        retrySucceeded.Add(task.TaskId);
                        retriedCleanups++;
                    }
                    else
                    {
                        cleanupFailures++;
                    }
                }

                // Every non-terminal task is interrupted; committed tasks were already
                // written as 'Completed' inside the publish transaction.
                var interrupted = new List<(string TaskId, bool CleanupSucceeded)>();
                var interruptedTasks = 0;
                foreach (var task in QueryTasks(connection, """
                    WHERE t.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Expired', 'Interrupted')
                    """))
                {
                    var cleaned = CleanupIntent(connection, task, ref stagingCleaned, ref originalsCleaned);
                    if (!cleaned)
                    {
                        cleanupFailures++;
                    }

                    interrupted.Add((task.TaskId, cleaned));
                    interruptedTasks++;
                }

                // Defensive check: a completion marker without its file row never deletes data.
                var inconsistentTasks = 0;
                foreach (var task in QueryTasks(connection, "WHERE t.Status = 'Completed' AND t.TargetFileId IS NOT NULL"))
                {
                    if (!FileRecordExists(connection, task.TargetFileId!))
                    {
                        inconsistentTasks++;
                    }
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                using var transaction = connection.BeginTransaction();

                foreach (var taskId in retrySucceeded)
                {
                    Execute(
                        connection,
                        transaction,
                        "UPDATE ImportTasks SET ErrorCode = NULL WHERE TaskId = $taskId;",
                        ("$taskId", taskId));
                }

                foreach (var (taskId, cleanupSucceeded) in interrupted)
                {
                    Execute(
                        connection,
                        transaction,
                        """
                        UPDATE ImportTasks
                        SET Status = 'Interrupted',
                            Stage = NULL,
                            StagePercent = NULL,
                            ErrorCode = $errorCode,
                            FinishedAtUtcMs = $now
                        WHERE TaskId = $taskId;
                        """,
                        ("$errorCode", cleanupSucceeded ? null : StorageStatuses.RecoveryCleanupFailedErrorCode),
                        ("$now", now),
                        ("$taskId", taskId));
                }

                var interruptedSlots = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE ImportSlots
                    SET Status = 'Interrupted', FinishedAtUtcMs = $now
                    WHERE Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Expired', 'Interrupted');
                    """,
                    ("$now", now));

                var finishedBatches = Execute(
                    connection,
                    transaction,
                    """
                    UPDATE ImportBatches
                    SET Status = 'Finished', FinishedAtUtcMs = $now
                    WHERE Status = 'Open'
                      AND NOT EXISTS (
                          SELECT 1 FROM ImportSlots s
                          WHERE s.BatchId = ImportBatches.BatchId
                            AND s.Status NOT IN ('Completed', 'Failed', 'Cancelled', 'Expired', 'Interrupted'));
                    """,
                    ("$now", now));

                transaction.Commit();
                return Task.FromResult(new StorageRecoveryReport(
                    interruptedTasks,
                    interruptedSlots,
                    finishedBatches,
                    stagingCleaned,
                    originalsCleaned,
                    retriedCleanups,
                    cleanupFailures,
                    inconsistentTasks));
            },
            cancellationToken);

    private bool CleanupIntent(
        SqliteConnection connection,
        RecoveryTask task,
        ref int stagingCleaned,
        ref int originalsCleaned)
    {
        var success = true;

        if (task.StagedOriginalPath is not null)
        {
            if (_fileStore.TryDeleteStagingFile(task.StagedOriginalPath))
            {
                stagingCleaned++;
            }
            else
            {
                success = false;
            }
        }

        if (task.StagedAssetPath is not null)
        {
            if (_fileStore.TryDeleteStagedAsset(task.StagedAssetPath))
            {
                stagingCleaned++;
            }
            else
            {
                success = false;
            }
        }

        if (task.TargetFileId is not null && task.TargetStoredFileName is not null)
        {
            // Never remove a file that any published record references (content reuse).
            var referenced = ScalarLong(
                connection,
                "SELECT COUNT(*) FROM Files WHERE FileId = $fileId OR StoredFileName = $storedFileName;",
                ("$fileId", task.TargetFileId),
                ("$storedFileName", task.TargetStoredFileName));
            if (referenced == 0)
            {
                if (_fileStore.TryDeleteOriginal(task.TargetStoredFileName))
                {
                    originalsCleaned++;
                }
                else
                {
                    success = false;
                }
            }
        }

        return success;
    }

    private static List<RecoveryTask> QueryTasks(SqliteConnection connection, string whereClause)
    {
        var tasks = new List<RecoveryTask>();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT t.TaskId, t.Status, t.TargetFileId, t.TargetStoredFileName, t.StagedOriginalPath, t.StagedAssetPath
            FROM ImportTasks t
            {whereClause}
            ORDER BY t.TaskId;
            """;
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            tasks.Add(new RecoveryTask(
                reader.GetString(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetString(5)));
        }

        return tasks;
    }

    private static bool FileRecordExists(SqliteConnection connection, string fileId) =>
        ScalarLong(connection, "SELECT COUNT(*) FROM Files WHERE FileId = $fileId;", ("$fileId", fileId)) > 0;

    private static long ScalarLong(
        SqliteConnection connection,
        string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
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

    private sealed record RecoveryTask(
        string TaskId,
        string Status,
        string? TargetFileId,
        string? TargetStoredFileName,
        string? StagedOriginalPath,
        string? StagedAssetPath);
}
