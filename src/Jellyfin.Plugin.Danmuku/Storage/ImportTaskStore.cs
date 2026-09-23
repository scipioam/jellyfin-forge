using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="IImportTaskStore" />
public sealed class ImportTaskStore : IImportTaskStore
{
    private readonly ISqliteConnectionFactory _connectionFactory;
    private readonly ISqliteWriteCoordinator _writeCoordinator;

    public ImportTaskStore(
        ISqliteConnectionFactory connectionFactory,
        ISqliteWriteCoordinator writeCoordinator)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _writeCoordinator = writeCoordinator ?? throw new ArgumentNullException(nameof(writeCoordinator));
    }

    /// <inheritdoc />
    public Task<long> AppendErrorsAsync(
        string taskId,
        IReadOnlyList<ImportErrorRecord> errors,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(errors);

        if (errors.Count == 0)
        {
            return Task.FromResult(0L);
        }

        foreach (var error in errors)
        {
            if (error.SourceOrdinal < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(errors), error.SourceOrdinal, "A source ordinal must not be negative.");
            }

            if (string.IsNullOrWhiteSpace(error.ReasonCodes))
            {
                throw new ArgumentException("Every import error requires at least one reason code.", nameof(errors));
            }
        }

        return _writeCoordinator.EnqueueAsync(
            (connection, token) =>
            {
                using var transaction = connection.BeginTransaction();
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO ImportErrors (TaskId, SourceOrdinal, ReasonCodes, TextSummary, CreatedAtUtcMs)
                    VALUES ($taskId, $sourceOrdinal, $reasonCodes, $textSummary, $createdAtUtcMs);
                    """;
                var taskIdParameter = command.Parameters.Add("$taskId", SqliteType.Text);
                var sourceOrdinalParameter = command.Parameters.Add("$sourceOrdinal", SqliteType.Integer);
                var reasonCodesParameter = command.Parameters.Add("$reasonCodes", SqliteType.Text);
                var textSummaryParameter = command.Parameters.Add("$textSummary", SqliteType.Text);
                var createdAtParameter = command.Parameters.Add("$createdAtUtcMs", SqliteType.Integer);

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long appended = 0;
                foreach (var error in errors)
                {
                    token.ThrowIfCancellationRequested();
                    taskIdParameter.Value = taskId;
                    sourceOrdinalParameter.Value = error.SourceOrdinal;
                    reasonCodesParameter.Value = error.ReasonCodes;
                    textSummaryParameter.Value = (object?)error.TextSummary ?? DBNull.Value;
                    createdAtParameter.Value = now;
                    appended += command.ExecuteNonQuery();
                }

                transaction.Commit();
                return Task.FromResult(appended);
            },
            cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<PersistedImportError>> ReadErrorsAsync(
        string taskId,
        long offset = 0,
        int limit = 50,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(limit);

        using var connection = _connectionFactory.CreateOpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT ErrorId, TaskId, SourceOrdinal, ReasonCodes, TextSummary, CreatedAtUtcMs
            FROM ImportErrors
            WHERE TaskId = $taskId
            ORDER BY SourceOrdinal, ErrorId
            LIMIT $limit OFFSET $offset;
            """;
        command.Parameters.AddWithValue("$taskId", taskId);
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$offset", offset);

        var results = new List<PersistedImportError>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new PersistedImportError(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetInt64(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5)));
        }

        return Task.FromResult<IReadOnlyList<PersistedImportError>>(results);
    }

    /// <inheritdoc />
    public Task<bool> UpdateCountersAsync(
        string taskId,
        ImportTaskCounterUpdate update,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentNullException.ThrowIfNull(update);

        var assignments = new List<string>(7);
        var parameters = new List<(string Name, object Value)>(7);

        AddCount(ref assignments, ref parameters, "TotalComments", "$totalComments", update.TotalComments);
        AddCount(ref assignments, ref parameters, "ProcessedComments", "$processedComments", update.ProcessedComments);
        AddCount(ref assignments, ref parameters, "NormalComments", "$normalComments", update.NormalComments);
        AddCount(ref assignments, ref parameters, "AbnormalComments", "$abnormalComments", update.AbnormalComments);
        AddCount(ref assignments, ref parameters, "SkippedComments", "$skippedComments", update.SkippedComments);

        if (update.Stage is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(update.Stage, nameof(update.Stage));
            assignments.Add("Stage = $stage");
            parameters.Add(("$stage", update.Stage));
        }

        if (update.StagePercent is { } stagePercent)
        {
            if (double.IsNaN(stagePercent) || stagePercent < 0 || stagePercent > 100)
            {
                throw new ArgumentOutOfRangeException(nameof(update), stagePercent, "Stage percent must be between 0 and 100.");
            }

            assignments.Add("StagePercent = $stagePercent");
            parameters.Add(("$stagePercent", stagePercent));
        }

        if (assignments.Count == 0)
        {
            throw new ArgumentException("At least one counter must be provided.", nameof(update));
        }

        var sql = $"UPDATE ImportTasks SET {string.Join(", ", assignments)} WHERE TaskId = $taskId;";
        return _writeCoordinator.EnqueueAsync(
            (connection, _) =>
            {
                using var command = connection.CreateCommand();
                command.CommandText = sql;
                foreach (var (name, value) in parameters)
                {
                    command.Parameters.AddWithValue(name, value);
                }

                command.Parameters.AddWithValue("$taskId", taskId);
                return Task.FromResult(command.ExecuteNonQuery() > 0);
            },
            cancellationToken);
    }

    private static void AddCount(
        ref List<string> assignments,
        ref List<(string Name, object Value)> parameters,
        string column,
        string parameterName,
        long? value)
    {
        if (value is not { } count)
        {
            return;
        }

        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), count, $"The {column} counter must not be negative.");
        }

        assignments.Add($"{column} = {parameterName}");
        parameters.Add((parameterName, count));
    }
}
