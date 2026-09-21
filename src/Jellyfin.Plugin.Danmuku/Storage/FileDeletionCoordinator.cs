using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="IFileDeletionCoordinator" />
public sealed class FileDeletionCoordinator : IFileDeletionCoordinator
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly IPublishFileStore _fileStore;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public FileDeletionCoordinator(
        ISqliteConnectionFactory connectionFactory,
        IPublishFileStore fileStore,
        ISqliteWriteCoordinator writeCoordinator)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _fileStore = fileStore ?? throw new ArgumentNullException(nameof(fileStore));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
    }

    public Task<FileDeletionMarkResult> MarkForDeletionAsync(string fileId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);

        return _writeCoordinator.EnqueueAsync(
            (connection, _) =>
            {
                using var transaction = connection.BeginTransaction();
                var status = ReadFileStatus(connection, transaction, fileId);
                if (status is null)
                {
                    return Task.FromResult(new FileDeletionMarkResult(FileDeletionMarkStatus.NotFound, 0));
                }

                var bindingCount = ScalarLong(
                    connection,
                    transaction,
                    "SELECT COUNT(*) FROM MediaBindings WHERE FileId = $fileId;",
                    ("$fileId", fileId));

                if (string.Equals(status, StorageStatuses.Files.Deleting, StringComparison.Ordinal)
                    || string.Equals(status, StorageStatuses.Files.DeleteFailed, StringComparison.Ordinal))
                {
                    return Task.FromResult(new FileDeletionMarkResult(FileDeletionMarkStatus.AlreadyMarked, bindingCount));
                }

                if (bindingCount > 0)
                {
                    return Task.FromResult(new FileDeletionMarkResult(FileDeletionMarkStatus.Referenced, bindingCount));
                }

                Execute(
                    connection,
                    transaction,
                    "UPDATE Files SET Status = 'Deleting', DeleteRequestedAtUtcMs = $now WHERE FileId = $fileId;",
                    ("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
                    ("$fileId", fileId));
                transaction.Commit();
                return Task.FromResult(new FileDeletionMarkResult(FileDeletionMarkStatus.Marked, 0));
            },
            cancellationToken);
    }

    public Task<FileDeletionCleanupReport> CleanupAsync(CancellationToken cancellationToken = default) =>
        _writeCoordinator.EnqueueAsync(
            (connection, _) =>
            {
                var candidates = ReadDeletionCandidates(connection);
                var deletedFiles = new List<string>();
                var failedFiles = new List<string>();

                foreach (var candidate in candidates)
                {
                    if (_fileStore.TryDeleteOriginal(candidate.StoredFileName))
                    {
                        deletedFiles.Add(candidate.FileId);
                    }
                    else
                    {
                        failedFiles.Add(candidate.FileId);
                    }
                }

                using var transaction = connection.BeginTransaction();
                var deleted = 0;
                foreach (var fileId in deletedFiles)
                {
                    var stillReferenced = ScalarLong(
                        connection,
                        transaction,
                        "SELECT COUNT(*) FROM MediaBindings WHERE FileId = $fileId;",
                        ("$fileId", fileId)) > 0;
                    if (stillReferenced)
                    {
                        // A binding appeared between marking and cleanup: keep the file record.
                        Execute(
                            connection,
                            transaction,
                            "UPDATE Files SET Status = 'DeleteFailed' WHERE FileId = $fileId;",
                            ("$fileId", fileId));
                        continue;
                    }

                    Execute(
                        connection,
                        transaction,
                        "DELETE FROM Files WHERE FileId = $fileId AND Status IN ('Deleting', 'DeleteFailed');",
                        ("$fileId", fileId));
                    deleted++;
                }

                foreach (var fileId in failedFiles)
                {
                    // The mark stays in place so a later cleanup run retries the disk delete.
                    Execute(
                        connection,
                        transaction,
                        "UPDATE Files SET Status = 'DeleteFailed' WHERE FileId = $fileId;",
                        ("$fileId", fileId));
                }

                transaction.Commit();
                return Task.FromResult(new FileDeletionCleanupReport(deleted, failedFiles.Count));
            },
            cancellationToken);

    private static List<DeletionCandidate> ReadDeletionCandidates(SqliteConnection connection)
    {
        var candidates = new List<DeletionCandidate>();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT FileId, StoredFileName FROM Files WHERE Status IN ('Deleting', 'DeleteFailed') ORDER BY FileId;";
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            candidates.Add(new DeletionCandidate(reader.GetString(0), reader.GetString(1)));
        }

        return candidates;
    }

    private static string? ReadFileStatus(SqliteConnection connection, SqliteTransaction transaction, string fileId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Status FROM Files WHERE FileId = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId);
        return command.ExecuteScalar() as string;
    }

    private static long ScalarLong(
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

        return Convert.ToInt64(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void Execute(
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

        command.ExecuteNonQuery();
    }

    private sealed record DeletionCandidate(string FileId, string StoredFileName);
}
