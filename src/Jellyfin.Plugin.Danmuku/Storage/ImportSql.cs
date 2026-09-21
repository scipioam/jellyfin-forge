using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Jellyfin.Plugin.Danmuku.Storage;

internal static class ImportSql
{
    internal const string Terminal = "'Completed','Failed','Cancelled','Expired','Interrupted'";

    internal static SqliteCommand Command(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] args)
    {
        var command = c.CreateCommand();
        command.Transaction = t;
        command.CommandText = sql;
        foreach (var (key, value) in args) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    internal static int Execute(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] args)
    {
        using var command = Command(c, t, sql, args);
        return command.ExecuteNonQuery();
    }

    internal static long Long(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] args)
    {
        using var command = Command(c, t, sql, args);
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    internal static string? Text(SqliteConnection c, SqliteTransaction? t, string sql, params (string, object?)[] args)
    {
        using var command = Command(c, t, sql, args);
        return command.ExecuteScalar() as string;
    }

    internal static void FinishBatches(SqliteConnection c, SqliteTransaction? t, long now) => Execute(c, t, $"""
        UPDATE ImportBatches SET Status='Finished', FinishedAtUtcMs=COALESCE(FinishedAtUtcMs,$now)
        WHERE Status='Open' AND NOT EXISTS
        (SELECT 1 FROM ImportSlots s WHERE s.BatchId=ImportBatches.BatchId AND s.Status NOT IN ({Terminal}));
        """, ("$now", now));
}

public sealed class ImportOperationException(string code, int statusCode, string message) : Exception(message)
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}
