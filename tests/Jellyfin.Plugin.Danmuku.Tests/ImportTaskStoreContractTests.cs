using System.Globalization;
using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;
using Microsoft.Data.Sqlite;
using Xunit;
using static Jellyfin.Plugin.Danmuku.Tests.StorageTestSql;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class ImportTaskStoreContractTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    [Fact]
    public async Task AppendedErrorsAreReadInStableOrderWithPaging()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
            InsertBatchAndTask(connection, "batch-2", "media-1", "task-2", slot: 1);
            Execute(
                connection,
                "INSERT INTO ImportErrors (TaskId, SourceOrdinal, ReasonCodes, TextSummary, CreatedAtUtcMs) VALUES ('task-2', 1, 'BlankText', NULL, 1);");
        }

        var appended = await services.ImportTasks.AppendErrorsAsync(
            "task-1",
            new[]
            {
                new ImportErrorRecord(5, "BlankText", "测试弹幕0005"),
                new ImportErrorRecord(2, "MissingTime", "测试弹幕0002"),
                new ImportErrorRecord(9, "InvalidTime", null),
                new ImportErrorRecord(1, "TextTooLong", "测试弹幕0001"),
                new ImportErrorRecord(7, "InvalidMode", "测试弹幕0007"),
                new ImportErrorRecord(3, "InvalidTime,BlankText", "测试弹幕0003"),
                new ImportErrorRecord(3, "InvalidTime,BlankText", "测试弹幕0003")
            });

        Assert.Equal(7L, appended);

        var firstPage = await services.ImportTasks.ReadErrorsAsync("task-1", 0, 2);
        Assert.Equal(new long[] { 1, 2 }, firstPage.Select(error => error.SourceOrdinal));
        Assert.Equal("TextTooLong", firstPage[0].ReasonCodes);
        Assert.Equal("测试弹幕0001", firstPage[0].TextSummary);
        Assert.Equal("task-1", firstPage[0].TaskId);
        Assert.True(firstPage[0].CreatedAtUtcMs > 0);

        var secondPage = await services.ImportTasks.ReadErrorsAsync("task-1", 2, 2);
        Assert.Equal(new long[] { 3, 3 }, secondPage.Select(error => error.SourceOrdinal));
        Assert.True(secondPage[0].ErrorId < secondPage[1].ErrorId);
        Assert.Equal("InvalidTime,BlankText", secondPage[0].ReasonCodes);

        var thirdPage = await services.ImportTasks.ReadErrorsAsync("task-1", 4, 10);
        Assert.Equal(new long[] { 5, 7, 9 }, thirdPage.Select(error => error.SourceOrdinal));
        Assert.Null(thirdPage[2].TextSummary);

        var beyond = await services.ImportTasks.ReadErrorsAsync("task-1", 100, 5);
        Assert.Empty(beyond);

        // Paging never leaks details belonging to another task.
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM ImportErrors WHERE TaskId = 'task-2';"));
        }
    }

    [Fact]
    public async Task ErrorAppendValidationRejectsBadInputAndUnknownTasks()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        Assert.Equal(0L, await services.ImportTasks.AppendErrorsAsync("task-1", Array.Empty<ImportErrorRecord>()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            services.ImportTasks.AppendErrorsAsync("task-1", new[] { new ImportErrorRecord(1, "  ", null) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.ImportTasks.AppendErrorsAsync("task-1", new[] { new ImportErrorRecord(-1, "BlankText", null) }));
        await Assert.ThrowsAsync<SqliteException>(() =>
            services.ImportTasks.AppendErrorsAsync("task-missing", new[] { new ImportErrorRecord(1, "BlankText", null) }));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.ImportTasks.ReadErrorsAsync("task-1", -1, 10));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.ImportTasks.ReadErrorsAsync("task-1", 0, 0));

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM ImportErrors;"));
        }
    }

    [Fact]
    public async Task CounterUpdatesWriteOnlyProvidedColumns()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        var updated = await services.ImportTasks.UpdateCountersAsync(
            "task-1",
            new ImportTaskCounterUpdate(
                TotalComments: 12,
                ProcessedComments: 10,
                NormalComments: 8,
                AbnormalComments: 2,
                SkippedComments: 0,
                Stage: "Parsing",
                StagePercent: 42.5));
        Assert.True(updated);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(12L, ScalarLong(connection, "SELECT TotalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(10L, ScalarLong(connection, "SELECT ProcessedComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(8L, ScalarLong(connection, "SELECT NormalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(2L, ScalarLong(connection, "SELECT AbnormalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT SkippedComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("Parsing", ScalarString(connection, "SELECT Stage FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(
                42.5,
                Convert.ToDouble(Scalar(connection, "SELECT StagePercent FROM ImportTasks WHERE TaskId = 'task-1';"), CultureInfo.InvariantCulture),
                3);
        }

        var partial = await services.ImportTasks.UpdateCountersAsync(
            "task-1",
            new ImportTaskCounterUpdate(NormalComments: 9));
        Assert.True(partial);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(9L, ScalarLong(connection, "SELECT NormalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(12L, ScalarLong(connection, "SELECT TotalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("Parsing", ScalarString(connection, "SELECT Stage FROM ImportTasks WHERE TaskId = 'task-1';"));
        }

        Assert.False(await services.ImportTasks.UpdateCountersAsync("task-missing", new ImportTaskCounterUpdate(TotalComments: 1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.ImportTasks.UpdateCountersAsync("task-1", new ImportTaskCounterUpdate(TotalComments: -1)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            services.ImportTasks.UpdateCountersAsync("task-1", new ImportTaskCounterUpdate(StagePercent: 101)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            services.ImportTasks.UpdateCountersAsync("task-1", new ImportTaskCounterUpdate()));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            services.ImportTasks.UpdateCountersAsync("task-1", new ImportTaskCounterUpdate(Stage: " ")));
    }

    [Fact]
    public async Task ParserErrorsAndStatisticsFlowIntoTheTaskStore()
    {
        var content = "[" +
            "{\"progress\":100,\"content\":\"测试弹幕0001\"}," +
            "{\"content\":\"测试弹幕0002\"}," +
            "{\"progress\":2.5,\"content\":\"测试弹幕0003\",\"mode\":6}," +
            "{\"progress\":400,\"content\":\"测试弹幕0004\"}]";

        var parser = new DanmukuFileParser();
        var comments = new List<CommentRecord>();
        var errors = new List<ImportErrorRecord>();

        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        using var source = new MemoryStream(Utf8NoBom.GetBytes(content), writable: false);
        var outcome = await parser.ParseAsync(
            source,
            Path.Combine(storage.Paths.StagingPath, "parser-store.dat"),
            (batch, _) =>
            {
                comments.AddRange(batch);
                return Task.CompletedTask;
            },
            (batch, _) =>
            {
                errors.AddRange(batch);
                return Task.CompletedTask;
            });

        Assert.True(outcome.Accepted);
        Assert.True(outcome.RequiresConfirmation);
        Assert.Equal(2, comments.Count);
        Assert.Equal(2, errors.Count);

        var appended = await services.ImportTasks.AppendErrorsAsync("task-1", errors);
        Assert.Equal(2L, appended);

        var countersUpdated = await services.ImportTasks.UpdateCountersAsync(
            "task-1",
            new ImportTaskCounterUpdate(
                TotalComments: outcome.Statistics.TotalSourceEntries,
                ProcessedComments: outcome.Statistics.TotalSourceEntries,
                NormalComments: outcome.Statistics.NormalEntries,
                AbnormalComments: outcome.Statistics.AbnormalEntries,
                SkippedComments: 0,
                Stage: "AwaitingConfirmation",
                StagePercent: 100));
        Assert.True(countersUpdated);

        var persisted = await services.ImportTasks.ReadErrorsAsync("task-1", 0, 10);
        Assert.Equal(new long[] { 2, 3 }, persisted.Select(error => error.SourceOrdinal));
        Assert.Equal("MissingTime", persisted[0].ReasonCodes);
        Assert.Equal("测试弹幕0002", persisted[0].TextSummary);
        Assert.Contains("UnsupportedMode", persisted[1].ReasonCodes, StringComparison.Ordinal);
        Assert.Equal("测试弹幕0003", persisted[1].TextSummary);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(4L, ScalarLong(connection, "SELECT TotalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(2L, ScalarLong(connection, "SELECT NormalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal(2L, ScalarLong(connection, "SELECT AbnormalComments FROM ImportTasks WHERE TaskId = 'task-1';"));
            Assert.Equal("AwaitingConfirmation", ScalarString(connection, "SELECT Stage FROM ImportTasks WHERE TaskId = 'task-1';"));

            // Normal entries never produce abnormal details.
            Assert.Equal(2L, ScalarLong(connection, "SELECT COUNT(*) FROM ImportErrors WHERE TaskId = 'task-1';"));
            Assert.Equal(0L, ScalarLong(connection, "SELECT COUNT(*) FROM ImportErrors WHERE TaskId = 'task-1' AND SourceOrdinal IN (1, 4);"));
        }
    }

    [Fact]
    public async Task RejectedParseEmitsNoBatchesSoExistingTaskDetailsStayUntouched()
    {
        using var storage = new StorageTestContext();
        storage.CreateMigrator().Migrate();
        await using var services = storage.CreateServices();
        using (var connection = storage.Factory.CreateOpenConnection())
        {
            InsertBatchAndTask(connection, "batch-1", "media-1", "task-1");
        }

        await services.ImportTasks.AppendErrorsAsync("task-1", new[] { new ImportErrorRecord(1, "BlankText", "prior") });

        var parser = new DanmukuFileParser();
        var comments = new List<CommentRecord>();
        var errors = new List<ImportErrorRecord>();
        using var source = new MemoryStream(Utf8NoBom.GetBytes("<noti><d p=\"1,1,25,16777215\">测试弹幕0001</d></noti>"), writable: false);

        var outcome = await parser.ParseAsync(
            source,
            Path.Combine(storage.Paths.StagingPath, "rejected.dat"),
            (batch, _) =>
            {
                comments.AddRange(batch);
                return Task.CompletedTask;
            },
            (batch, _) =>
            {
                errors.AddRange(batch);
                return Task.CompletedTask;
            });

        Assert.False(outcome.Accepted);
        Assert.Equal(ParseRejectReason.StructureInvalid, outcome.RejectReason);
        Assert.Empty(comments);
        Assert.Empty(errors);

        using (var connection = storage.Factory.CreateOpenConnection())
        {
            Assert.Equal(1L, ScalarLong(connection, "SELECT COUNT(*) FROM ImportErrors WHERE TaskId = 'task-1';"));
            Assert.Equal("prior", ScalarString(connection, "SELECT TextSummary FROM ImportErrors WHERE TaskId = 'task-1';"));
        }
    }
}
