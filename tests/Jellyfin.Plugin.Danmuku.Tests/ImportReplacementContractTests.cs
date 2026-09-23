using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

[Collection("Import boundaries")]
public sealed class ImportReplacementContractTests
{
    [Theory]
    [InlineData("A", false)]
    [InlineData("A", true)]
    [InlineData("C", false)]
    [InlineData("C", true)]
    [InlineData("off", false)]
    [InlineData("off", true)]
    [InlineData("none", false)]
    [InlineData("none", true)]
    public async Task ReplacementUsesCurrentActiveSelectionAndKeepsOtherMedia(string active, bool existingB)
    {
        await using var x = new ImportLifecycleTestContext();
        var a = await x.PublishAsync("A");
        var c = await x.PublishAsync("C");
        string? b = null;
        if (existingB) b = await x.PublishAsync("B");
        var other = await x.Bindings.ReadAsync("other");
        await x.Bindings.UpdateAsync("other", other.Version, [a], a);
        var state = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", state.Version, state.FileIds, active == "A" ? a : active == "C" ? c : null);
        if (active == "none") x.Execute("UPDATE MediaState SET IsDeactivated=0 WHERE MediaId='media'");
        var before = await x.Bindings.ReadAsync("media");
        var batch = await x.BatchAsync(operation: "replace", replace: a);
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("B"));
        await x.Imports.ProcessPendingAsync();
        Assert.Equal("Completed", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].Status);
        var after = await x.Bindings.ReadAsync("media");
        Assert.DoesNotContain(a, after.FileIds);
        Assert.Contains(c, after.FileIds);
        b ??= after.FileIds.Single(id => id != c);
        Assert.Contains(b, after.FileIds);
        Assert.Equal(active == "A" ? b : active == "C" ? c : null, after.ActiveFileId);
        Assert.Equal(before.IsDeactivated, after.IsDeactivated);
        Assert.Equal(before.Version + 1, after.Version);
        Assert.Equal(a, (await x.Bindings.ReadAsync("other")).ActiveFileId);
        Assert.Equal(3, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(2, after.FileIds.Count);
    }

    [Fact]
    public async Task ReplacingAWithIdenticalAIsANoOpAfterVersionAndTargetValidation()
    {
        await using var x = new ImportLifecycleTestContext();
        var a = await x.PublishAsync("A");
        var before = await x.Bindings.ReadAsync("media");
        var batch = await x.BatchAsync(operation: "replace", replace: a);
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("A"));
        await x.Imports.ProcessPendingAsync();
        var after = await x.Bindings.ReadAsync("media");
        Assert.Equal("Unchanged", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].ResultCode);
        Assert.Equal(before.Version, after.Version);
        Assert.Equal(before.ActiveFileId, after.ActiveFileId);
        Assert.Equal(before.FileIds, after.FileIds);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal("Completed", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].Status);
    }

    [Fact]
    public async Task ConflictKeepsStagingAndResumePreservesNewSelectionAndOriginalDeadline()
    {
        await using var x = new ImportLifecycleTestContext();
        var a = await x.PublishAsync("A"); var c = await x.PublishAsync("C");
        var batch = await x.BatchAsync(operation: "replace", replace: a);
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("B"));
        var state = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", state.Version, state.FileIds, c);
        await x.Imports.ProcessPendingAsync();
        var conflict = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0];
        Assert.Equal("AwaitingConflictResolution", conflict.TaskStatus);
        Assert.Equal(2, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.NotEmpty(Directory.GetFiles(x.Storage.Paths.StagingPath));
        var deadline = conflict.DeadlineAtUtcMs;
        var stale = await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.ResumeAsync(conflict.TaskId!, state.Version));
        Assert.Equal("MediaVersionConflict", stale.Code);
        x.Clock.Advance(TimeSpan.FromHours(1));
        await x.Imports.ResumeAsync(conflict.TaskId!, state.Version + 1);
        Assert.Equal(deadline, (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].DeadlineAtUtcMs);
        await x.Imports.ProcessPendingAsync();
        var final = await x.Bindings.ReadAsync("media");
        Assert.Equal(c, final.ActiveFileId);
        Assert.DoesNotContain(a, final.FileIds);
        Assert.Equal("replace", (await x.Imports.GetBatchAsync(batch.BatchId)).Operation);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task UnboundReplacementTargetCannotResumeOrTurnIntoAppendAndHistoryDoesNotBlockDeletion()
    {
        await using var x = new ImportLifecycleTestContext();
        var a = await x.PublishAsync("A");
        var batch = await x.BatchAsync(operation: "replace", replace: a);
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("B"));
        var state = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", state.Version, [], null);
        await x.Imports.ProcessPendingAsync();
        var task = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].TaskId!;
        var denied = await Assert.ThrowsAsync<ImportOperationException>(() => x.Imports.ResumeAsync(task, state.Version + 1));
        Assert.Equal("ReplaceTargetUnbound", denied.Code);
        var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
        Assert.Equal(FileDeletionMarkStatus.Marked, (await deletion.MarkForDeletionAsync(a)).Status);
        Assert.Equal(1, (await deletion.CleanupAsync()).Deleted);
        Assert.Equal(a, (await x.Imports.GetBatchAsync(batch.BatchId)).ReplaceFileId);
        await x.Imports.CancelTaskAsync(task);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Empty((await x.Bindings.ReadAsync("media")).FileIds);
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }

    [Fact]
    public async Task ConsecutiveBatchPublishesAdvanceOwnVersionButConcurrentBatchConflicts()
    {
        await using var x = new ImportLifecycleTestContext();
        var one = await x.BatchAsync(["a", "b"]); var two = await x.BatchAsync();
        await x.UploadAsync(one.BatchId, 0, ImportLifecycleTestContext.Json("A"));
        await x.UploadAsync(one.BatchId, 1, ImportLifecycleTestContext.Json("B"));
        await x.UploadAsync(two.BatchId, content: ImportLifecycleTestContext.Json("C"));
        await x.Imports.ProcessPendingAsync();
        var batches = new[] { await x.Imports.GetBatchAsync(one.BatchId), await x.Imports.GetBatchAsync(two.BatchId) };
        // Creation timestamps can tie; whichever batch wins must not silently overwrite the other.
        Assert.Contains(batches.SelectMany(b => b.Slots), s => s.TaskStatus == "AwaitingConflictResolution");
        foreach (var batch in batches)
            foreach (var slot in batch.Slots.Where(s => s.TaskStatus == "AwaitingConflictResolution"))
            {
                await x.Imports.ResumeAsync(slot.TaskId!, (await x.Bindings.ReadAsync("media")).Version);
                await x.Imports.ProcessPendingAsync();
            }
        // A resumed batch may now expose its next conflict; explicitly refresh again.
        foreach (var batch in batches)
            foreach (var slot in (await x.Imports.GetBatchAsync(batch.BatchId)).Slots.Where(s => s.TaskStatus == "AwaitingConflictResolution"))
            {
                await x.Imports.ResumeAsync(slot.TaskId!, (await x.Bindings.ReadAsync("media")).Version);
                await x.Imports.ProcessPendingAsync();
            }
        Assert.Equal(3, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(3, (await x.Bindings.ReadAsync("media")).Version);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationRacingAcceptanceOrPublicationHasOneTerminalOutcome(bool duringPublish)
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync();
        if (duringPublish)
        {
            await x.UploadAsync(batch.BatchId);
            await Task.WhenAll(x.Imports.ProcessPendingAsync(), x.Imports.CancelBatchAsync(batch.BatchId));
        }
        else
        {
            using var source = new PausedUploadStream();
            var receive = x.Imports.ReceiveAsync(batch.BatchId, 0, source);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            source.Release.TrySetResult();
            await Task.WhenAll(receive, x.Imports.CancelBatchAsync(batch.BatchId));
            await x.Imports.ProcessPendingAsync();
        }
        var final = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0];
        Assert.Contains(final.Status, new[] { "Completed", "Cancelled" });
        var count = final.Status == "Completed" ? 1 : 0;
        Assert.Equal(count, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(count, x.Scalar("SELECT COUNT(*) FROM MediaBindings"));
        Assert.Equal(count, x.Scalar("SELECT COUNT(*) FROM Comments"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.StagingPath));
    }
}
