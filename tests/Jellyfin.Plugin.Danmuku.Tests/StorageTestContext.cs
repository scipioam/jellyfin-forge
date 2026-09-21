using System.Globalization;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Tests;

/// <summary>
/// Temporary Danmuku data root with reopened-context support for restart tests.
/// </summary>
internal sealed class StorageTestContext : IDisposable
{
    private readonly bool _ownsRoot;

    public StorageTestContext()
        : this(Path.Combine(Path.GetTempPath(), "danmuku-tests", Guid.NewGuid().ToString("N")), ownsRoot: true)
    {
    }

    private StorageTestContext(string rootPath, bool ownsRoot)
    {
        RootPath = rootPath;
        _ownsRoot = ownsRoot;
        Paths = new DanmukuDataPaths(RootPath);
        Factory = new SqliteConnectionFactory(Paths.DatabasePath);
    }

    public string RootPath { get; }

    public DanmukuDataPaths Paths { get; }

    public SqliteConnectionFactory Factory { get; }

    /// <summary>Opens the same data root again, as if the server had restarted.</summary>
    public StorageTestContext Reopen() => new(RootPath, ownsRoot: false);

    public SqliteSchemaMigrator CreateMigrator() => new(Factory, Paths);

    public SqliteSchemaMigrator CreateMigrator(IReadOnlyList<SchemaMigration> migrations) => new(Factory, Paths, migrations);

    public StorageTestServices CreateServices() => new(this);

    public void Dispose()
    {
        if (!_ownsRoot)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(RootPath, recursive: true);
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

/// <summary>
/// Storage services wired to the temporary context, sharing one write coordinator.
/// </summary>
internal sealed class StorageTestServices : IAsyncDisposable
{
    public StorageTestServices(StorageTestContext context)
    {
        Coordinator = new SqliteWriteCoordinator(context.Factory);
        FileStore = new PublishFileStore(context.Paths);
        Publish = new PublishService(context.Factory, FileStore, Coordinator);
        Deletion = new FileDeletionCoordinator(context.Factory, FileStore, Coordinator);
        Recovery = new StorageRecoveryService(context.Factory, FileStore, Coordinator);
    }

    public SqliteWriteCoordinator Coordinator { get; }

    public IPublishFileStore FileStore { get; }

    public IPublishService Publish { get; }

    public IFileDeletionCoordinator Deletion { get; }

    public IStorageRecoveryService Recovery { get; }

    public ValueTask DisposeAsync() => Coordinator.DisposeAsync();
}

/// <summary>
/// Raw SQL helpers shared by the storage contract tests.
/// </summary>
internal static class StorageTestSql
{
    public const long NowMs = 1_700_000_000_000;

    public static void Execute(SqliteConnection connection, string sql, params (string Name, object? Value)[] parameters) =>
        Execute(connection, null, sql, parameters);

    public static void Execute(
        SqliteConnection connection,
        SqliteTransaction? transaction,
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

        command.ExecuteNonQuery();
    }

    public static object? Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    public static long ScalarLong(SqliteConnection connection, string sql) =>
        Convert.ToInt64(Scalar(connection, sql), CultureInfo.InvariantCulture);

    public static string? ScalarString(SqliteConnection connection, string sql) => Scalar(connection, sql) as string;

    public static bool ScalarIsNull(SqliteConnection connection, string sql) => Scalar(connection, sql) is null or DBNull;

    public static string[] QueryNames(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var names = new List<string>();
        while (reader.Read())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    public static SqliteConnection OpenReadOnly(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    public static void InsertFile(SqliteConnection connection, string fileId, string? storedFileName = null, string contentHash = "hash-x", string status = "Published")
    {
        Execute(
            connection,
            """
            INSERT INTO Files (FileId, OriginalFileName, StoredFileName, DisplayName, Format, ContentHash, ImportedAtUtcMs, CommentCount, LastCommentTimeMs, ParseDataVersion, Status, DeleteRequestedAtUtcMs)
            VALUES ($fileId, 'sample.xml', $storedFileName, 'Sample', 'xml', $contentHash, $now, 0, NULL, 'm1-v1', $status, NULL);
            """,
            ("$fileId", fileId),
            ("$storedFileName", storedFileName ?? fileId + ".xml"),
            ("$contentHash", contentHash),
            ("$status", status),
            ("$now", NowMs));
    }

    public static void InsertBinding(SqliteConnection connection, string mediaId, string fileId, SqliteTransaction? transaction = null)
    {
        Execute(
            connection,
            transaction,
            "INSERT INTO MediaBindings (MediaId, FileId, BoundAtUtcMs) VALUES ($mediaId, $fileId, $now);",
            ("$mediaId", mediaId),
            ("$fileId", fileId),
            ("$now", NowMs));
    }

    public static void InsertBatchAndTask(
        SqliteConnection connection,
        string batchId,
        string mediaId,
        string taskId,
        int slot = 0,
        string operation = "append",
        string? replaceFileId = null,
        string taskStatus = "Processing",
        string slotStatus = "Accepted")
    {
        Execute(
            connection,
            """
            INSERT INTO ImportBatches (BatchId, MediaId, Operation, ReplaceFileId, ExpectedMediaVersion, Status, CreatedAtUtcMs, FinishedAtUtcMs)
            VALUES ($batchId, $mediaId, $operation, $replaceFileId, 0, 'Open', $now, NULL);
            """,
            ("$batchId", batchId),
            ("$mediaId", mediaId),
            ("$operation", operation),
            ("$replaceFileId", replaceFileId),
            ("$now", NowMs));
        Execute(
            connection,
            """
            INSERT INTO ImportSlots (BatchId, Slot, Status, TotalBytes, ReceivedBytes, CreatedAtUtcMs, UploadDeadlineAtUtcMs, FinishedAtUtcMs)
            VALUES ($batchId, $slot, $slotStatus, 1024, $receivedBytes, $now, $deadline, NULL);
            """,
            ("$batchId", batchId),
            ("$slot", slot),
            ("$slotStatus", slotStatus),
            ("$receivedBytes", slotStatus is "PendingUpload" ? 0 : 512),
            ("$now", NowMs),
            ("$deadline", NowMs + 3_600_000));
        Execute(
            connection,
            """
            INSERT INTO ImportTasks (TaskId, BatchId, Slot, Status, CreatedAtUtcMs)
            VALUES ($taskId, $batchId, $slot, $taskStatus, $now);
            """,
            ("$taskId", taskId),
            ("$batchId", batchId),
            ("$slot", slot),
            ("$taskStatus", taskStatus),
            ("$now", NowMs));
    }
}
