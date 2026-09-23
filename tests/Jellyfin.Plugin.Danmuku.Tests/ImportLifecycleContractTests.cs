using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

[Collection("Import boundaries")]
public sealed class ImportLifecycleContractTests
{
    [Fact]
    public async Task BatchLimitsReserveEveryPositionAtomicallyAndRequestsAreIdempotent()
    {
        await using var x = new ImportLifecycleTestContext();
        var request = new ImportBatchRequest("stable", "media", 0, Enumerable.Repeat("sample.json", 10).ToArray());
        var batch = await x.Imports.CreateBatchAsync(request);
        Assert.Equal(10, batch.Slots.Count);
        Assert.All(batch.Slots, s => { Assert.Equal("PendingUpload", s.Status); Assert.Null(s.TaskId); });
        Assert.Equal(batch.BatchId, (await x.Imports.CreateBatchAsync(request)).BatchId);
        var eleven = await Assert.ThrowsAsync<ImportOperationException>(() => x.BatchAsync(Enumerable.Repeat("a", 11).ToArray()));
        Assert.Equal(422, eleven.StatusCode);
        await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.CreateBatchAsync(request with { MediaId = "different" }));
        for (var i = 0; i < 9; i++) await x.BatchAsync(Enumerable.Repeat("a", 10).ToArray());
        var full = await Assert.ThrowsAsync<ImportOperationException>(() => x.BatchAsync());
        Assert.Equal(503, full.StatusCode);
        Assert.Equal(100, x.Scalar("SELECT COUNT(*) FROM ImportSlots"));
        Assert.Equal(10, x.Scalar("SELECT COUNT(*) FROM ImportBatches"));
        await x.Imports.CancelBatchAsync(batch.BatchId);
        await x.BatchAsync();
    }

    [Fact]
    public async Task LostUploadResponseReturnsSameTaskAndTerminalPositionNeverRestarts()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync();
        var first = await x.UploadAsync(batch.BatchId);
        Assert.Equal("Accepted", first.Status);
        using var neverRead = new PausedUploadStream();
        var second = await x.Imports.ReceiveAsync(batch.BatchId, 0, neverRead);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.False(neverRead.Entered.Task.IsCompleted);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM ImportTasks"));
        await x.Imports.ProcessPendingAsync();
        Assert.Equal("Completed", (await x.Imports.ReceiveAsync(batch.BatchId, 0, neverRead)).Status);
        Assert.False(neverRead.Entered.Task.IsCompleted);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Comments"));
    }

    [Fact]
    public async Task LaterParsedFileWaitsForAnEmptyPositionAndCancellationReleasesOrder()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync(["first.json", "second.json"]);
        await x.UploadAsync(batch.BatchId, 1);
        await x.Imports.ProcessPendingAsync();
        var waiting = await x.Imports.GetBatchAsync(batch.BatchId);
        Assert.Equal("WaitingForPrevious", waiting.Slots[1].Stage);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM Files"));
        await x.Imports.CancelBatchAsync(batch.BatchId, [0]);
        await x.Imports.ProcessPendingAsync();
        var final = await x.Imports.GetBatchAsync(batch.BatchId);
        Assert.Equal("Finished", final.Status);
        Assert.Equal(new[] { "Cancelled", "Completed" }, final.Slots.Select(s => s.Status));
        Assert.NotNull((await x.Bindings.ReadAsync("media")).ActiveFileId);
    }

    [Fact]
    public async Task BatchSkipConfirmsOnlyAlreadyParsedErrorsAndIsIdempotent()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync(["one.json", "two.json"]);
        const string abnormal = "[{\"progress\":1,\"content\":\"valid\"},{\"content\":\"missing time\"}]";
        await x.UploadAsync(batch.BatchId, 0, abnormal);
        await x.Imports.ProcessPendingAsync();
        Assert.Equal("AwaitingConfirmation", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].TaskStatus);
        await x.Imports.ConfirmBatchSkipAsync(batch.BatchId);
        await x.Imports.ConfirmBatchSkipAsync(batch.BatchId);
        await x.UploadAsync(batch.BatchId, 1, abnormal);
        await x.Imports.ProcessPendingAsync();
        var middle = await x.Imports.GetBatchAsync(batch.BatchId);
        Assert.Equal("Completed", middle.Slots[0].Status);
        Assert.Equal(1, middle.Slots[0].SkippedComments);
        Assert.Equal("AwaitingConfirmation", middle.Slots[1].TaskStatus);
        Assert.Equal(0, middle.Slots[1].SkippedComments);
        await x.Imports.ConfirmSkipAsync(middle.Slots[1].TaskId!);
        await x.Imports.ProcessPendingAsync();
        Assert.Equal("Finished", (await x.Imports.GetBatchAsync(batch.BatchId)).Status);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(2, x.Scalar("SELECT COUNT(*) FROM ImportErrors"));
    }

    [Fact]
    public async Task DuplicateContentReusesFileAndDoesNotReactivateStoppedMedia()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("same");
        var before = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", before.Version, [file], null);
        var batch = await x.BatchAsync();
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("same"));
        await x.Imports.ProcessPendingAsync();
        var after = await x.Bindings.ReadAsync("media");
        Assert.Equal("AlreadyBound", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].ResultCode);
        Assert.True(after.IsDeactivated);
        Assert.Null(after.ActiveFileId);
        var other = await x.BatchAsync(media: "other");
        await x.UploadAsync(other.BatchId, content: ImportLifecycleTestContext.Json("same"));
        await x.Imports.ProcessPendingAsync();
        Assert.Equal(file, (await x.Bindings.ReadAsync("other")).ActiveFileId);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(2, x.Scalar("SELECT COUNT(*) FROM MediaBindings"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task SameNameDifferentContentsGetPhysicalSuffixesAndPathsAreSanitized()
    {
        await using var x = new ImportLifecycleTestContext();
        await x.PublishAsync("first", name: "../../sample.json");
        await x.PublishAsync("second", name: "C:\\folder\\sample.json");
        Assert.Equal(new[] { "sample (1).json", "sample.json" }, Directory.GetFiles(x.Storage.Paths.OriginalFilesPath).Select(Path.GetFileName).Order());
        Assert.Equal(2, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(2, x.Scalar("SELECT COUNT(DISTINCT ContentHash) FROM Files"));
    }

    [Theory]
    [InlineData("[]", "ZeroValidEntries")]
    [InlineData("[{\"content\":\"no time\"}]", "ZeroValidEntries")]
    [InlineData("[{", "StructureInvalid")]
    public async Task RejectedParsingDoesNotPublishAndLaterFileCanProceed(string content, string code)
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync(["bad.json", "good.json"]);
        await x.UploadAsync(batch.BatchId, 0, content);
        await x.UploadAsync(batch.BatchId, 1);
        await x.Imports.ProcessPendingAsync();
        var result = await x.Imports.GetBatchAsync(batch.BatchId);
        Assert.Equal(code, result.Slots[0].ErrorCode);
        Assert.Equal(new[] { "Failed", "Completed" }, result.Slots.Select(s => s.Status));
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task UploadLengthAndByteLimitFailuresTerminateTheirPositions()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync(["a", "b"]);
        using var tiny = new MemoryStream(Encoding.UTF8.GetBytes("[]"));
        Assert.Equal("FileTooLarge", (await x.Imports.ReceiveAsync(batch.BatchId, 0, tiny, 52428801)).ErrorCode);
        Assert.Equal("UploadIncomplete", (await x.Imports.ReceiveAsync(batch.BatchId, 1, tiny, 10)).ErrorCode);
        Assert.Equal("Finished", (await x.Imports.GetBatchAsync(batch.BatchId)).Status);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportTasks"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task ConcurrentReceivingLimitCancellationAndIdleTimeoutAreEnforced()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync(["a", "b", "c"]);
        using var a = new PausedUploadStream(); using var b = new PausedUploadStream();
        var first = x.Imports.ReceiveAsync(batch.BatchId, 0, a);
        await a.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = x.Imports.ReceiveAsync(batch.BatchId, 1, b);
        await b.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var busy = await Assert.ThrowsAsync<ImportOperationException>(() => x.UploadAsync(batch.BatchId, 2));
        Assert.Equal("ReceiveCapacity", busy.Code);
        await x.Imports.CancelBatchAsync(batch.BatchId, [0]);
        Assert.Equal("Cancelled", (await first).Status);
        x.Clock.Advance(TimeSpan.FromSeconds(120));
        await x.Imports.SweepAsync();
        Assert.Equal("ReceiveTimeout", (await second).ErrorCode);
        await x.UploadAsync(batch.BatchId, 2);
        await x.Imports.ProcessPendingAsync();
        Assert.Equal("Finished", (await x.Imports.GetBatchAsync(batch.BatchId)).Status);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task StagingBudgetFailureIsExplicitAndDoesNotTouchPublishedData()
    {
        await using var x = new ImportLifecycleTestContext();
        var published = await x.PublishAsync("existing");
        var batch = await x.BatchAsync();
        x.Execute("UPDATE ImportSlots SET TemporaryBytes=4294967296 WHERE Status='PendingUpload'");
        var failed = await x.UploadAsync(batch.BatchId);
        Assert.Equal("StagingCapacity", failed.ErrorCode);
        Assert.Equal(published, (await x.Bindings.ReadAsync("media")).ActiveFileId);
        Assert.Equal(0, x.Scalar("SELECT SUM(TemporaryBytes) FROM ImportSlots"));
    }

    [Fact]
    public async Task ExpirationAndSevenDayRetentionDoNotDeleteImportedData()
    {
        await using var x = new ImportLifecycleTestContext();
        var published = await x.PublishAsync("keep");
        var missing = await x.BatchAsync();
        x.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("Expired", (await x.Imports.GetBatchAsync(missing.BatchId)).Slots[0].Status);
        var waiting = await x.BatchAsync();
        await x.UploadAsync(waiting.BatchId, content: "[{\"progress\":1,\"content\":\"ok\"},{\"content\":\"bad\"}]");
        await x.Imports.ProcessPendingAsync();
        var pending = (await x.Imports.GetBatchAsync(waiting.BatchId)).Slots[0];
        Assert.NotNull(pending.DeadlineAtUtcMs);
        x.Clock.Advance(TimeSpan.FromHours(24));
        await x.Imports.ConfirmSkipAsync(pending.TaskId!);
        var expired = (await x.Imports.GetBatchAsync(waiting.BatchId)).Slots[0];
        Assert.Equal("Expired", expired.Status);
        var finished = expired.FinishedAtUtcMs;
        await x.Imports.SweepAsync();
        Assert.Equal(finished, (await x.Imports.GetBatchAsync(waiting.BatchId)).Slots[0].FinishedAtUtcMs);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM ImportErrors"));
        x.Clock.Advance(TimeSpan.FromDays(7));
        await x.Imports.SweepAsync();
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportBatches"));
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM ImportErrors"));
        Assert.Equal(published, (await x.Bindings.ReadAsync("media")).ActiveFileId);
        Assert.Single(Directory.GetFiles(x.Storage.Paths.OriginalFilesPath));
    }

    [Fact]
    public async Task RestartInterruptsUnfinishedWorkWithoutResettingFinishedTimes()
    {
        await using var x = new ImportLifecycleTestContext();
        var keep = await x.PublishAsync("keep");
        var batch = await x.BatchAsync(["empty", "queued", "waiting"]);
        await x.UploadAsync(batch.BatchId, 1);
        await x.UploadAsync(batch.BatchId, 2, "[{\"progress\":1,\"content\":\"ok\"},{\"content\":\"bad\"}]");
        await x.Imports.ProcessPendingAsync();
        var recovery = new StorageRecoveryService(x.Storage.Factory, x.Files, x.Writes, x.Clock);
        await recovery.RunAsync();
        await x.Imports.SweepAsync();
        var interrupted = await x.Imports.GetBatchAsync(batch.BatchId);
        Assert.All(interrupted.Slots, s => Assert.Equal("Interrupted", s.Status));
        Assert.Equal("Finished", interrupted.Status);
        x.Clock.Advance(TimeSpan.FromHours(1));
        await recovery.RunAsync();
        Assert.Equal(interrupted.FinishedAtUtcMs, (await x.Imports.GetBatchAsync(batch.BatchId)).FinishedAtUtcMs);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
        Assert.Equal(keep, (await x.Bindings.ReadAsync("media")).ActiveFileId);
    }
}
