using Jellyfin.Plugin.Danmuku.Api;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class CombinePlanContractTests
{
    private static CombinePlanService Service(ImportLifecycleTestContext x) => new(x.Storage.Factory, x.Writes, x.Bindings, x.Clock);
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task SaveActivatePreserveBindingsAndDeleteAreAtomic()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source");
        var plans = Service(x);
        var before = await x.Bindings.ReadAsync("media");
        var plan = await plans.SaveAsync("media", Id(), " combine-1 ", [new(source)], null);
        Assert.Equal("combine-1", plan.Name);
        var saved = await x.Bindings.ReadAsync("media");
        Assert.Equal(before.Version, saved.Version); Assert.Equal(before.Selection, saved.Selection);
        Assert.Equal(before.FileIds, saved.FileIds);
        var active = await x.Bindings.UpdateSelectionAsync("media", before.Version, new("plan", plan.PlanId, plan.Version));
        Assert.Null(active.ActiveFileId); Assert.Equal(plan.PlanId, active.ActivePlanId);
        Assert.Equal("SelectionFormatConflict", (await Assert.ThrowsAsync<ImportOperationException>(() =>
            x.Bindings.UpdateAsync("media", active.Version, [], null))).Code);
        var cleared = await x.Bindings.UpdateBindingsAsync("media", active.Version, [], "preserve", null);
        Assert.Equal(plan.PlanId, cleared.ActivePlanId);
        Assert.Equal(1, plans.Get("media", plan.PlanId).Version);
        var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
        Assert.Equal(FileDeletionMarkStatus.Referenced, (await deletion.MarkForDeletionAsync(source)).Status);
        Assert.Equal(0, new ManagementQueries(x.Storage.Factory, x.Files).Files(null, true, 0, 50).TotalCount);
        await plans.DeleteAsync("media", plan.PlanId, 1, cleared.Version, new("disabled"));
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.True((await x.Bindings.ReadAsync("media")).IsDeactivated);
        Assert.Equal(FileDeletionMarkStatus.Marked, (await deletion.MarkForDeletionAsync(source)).Status);
    }

    [Fact]
    public async Task StaleMediaVersionCannotCommitPlanContentOrDeleteNewSelection()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source");
        var plans = Service(x);
        var state = await x.Bindings.ReadAsync("media");
        var p = await plans.SaveAsync("media", Id(), "P", [new(source)], null);
        var q = await plans.SaveAsync("media", Id(), "Q", [new(source)], null, true, state.Version);
        Assert.Equal("MediaVersionConflict", (await Assert.ThrowsAsync<ImportOperationException>(() =>
            plans.SaveAsync("media", p.PlanId, "changed", [new(source, 0, null, 10)], p.Version, true, state.Version))).Code);
        Assert.Equal("P", plans.Get("media", p.PlanId).Name);
        Assert.Equal(0, plans.Get("media", p.PlanId).Segments[0].TargetStartMs);
        p = await plans.SaveAsync("media", p.PlanId, "saved", [new(source)], p.Version);
        var active = await x.Bindings.ReadAsync("media");
        Assert.Equal(q.PlanId, active.ActivePlanId); Assert.Equal(state.Version + 1, active.Version);
        await x.Bindings.UpdateSelectionAsync("media", active.Version, new("file", source));
        Assert.Equal("MediaVersionConflict", (await Assert.ThrowsAsync<ImportOperationException>(() =>
            plans.DeleteAsync("media", q.PlanId, q.Version, active.Version, new("disabled")))).Code);
        Assert.Equal(source, (await x.Bindings.ReadAsync("media")).ActiveFileId);
        Assert.Equal(2, plans.List("media").Count);
        Assert.Equal("PlanVersionConflict", (await Assert.ThrowsAsync<ImportOperationException>(() =>
            plans.SaveAsync("media", p.PlanId, "stale", [new(source)], 1))).Code);
    }

    [Fact]
    public async Task PlanOnlyMissingMediaIsDiscoverableAndExplicitlyCleanable()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source", "other");
        var plans = Service(x);
        var p = await plans.SaveAsync("orphan", Id(), "P", [new(source)], null, true, 0);
        x.Lookup.Results["orphan"] = MediaPresence.Missing;
        var job = await x.Bindings.StartCheckAllAsync(); await x.Bindings.ProcessCheckJobAsync();
        Assert.Equal(1, x.Bindings.GetCheckJob(job).Missing);
        var abnormal = new ManagementQueries(x.Storage.Factory, x.Files).AbnormalMedia(0, 50);
        Assert.Equal("orphan", Assert.Single(abnormal.Items)["MediaId"]);
        Assert.Equal("MediaMissing", (await Assert.ThrowsAsync<ImportOperationException>(() =>
            plans.SaveAsync("orphan", p.PlanId, "updated", [new(source)], 1))).Code);
        await Assert.ThrowsAsync<ImportOperationException>(() => plans.DeleteAsync("orphan", p.PlanId, 1, 1, new("file", source)));
        await plans.DeleteAsync("orphan", p.PlanId, 1, 1, new("disabled"));
        Assert.Empty(plans.List("orphan"));
        Assert.Equal(source, (await x.Bindings.ReadAsync("other")).ActiveFileId);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
    }

    [Fact]
    public async Task RangeMappingCountsBoundariesDuplicatesAndOutOfDuration()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source");
        x.Execute("UPDATE Comments SET TimeMs=720000");
        var plans = Service(x);
        var preview = await plans.PreviewAsync("media", [new(source, 600000, 1200000, 1200000), new(source, 720000, null, 0)], 1000000);
        Assert.Equal(2, preview.TotalCount); Assert.Equal(1, preview.BeyondDurationCount);
        Assert.Equal(1320000, preview.Segments[0].FirstTargetMs);
        Assert.Equal(1800000, preview.Segments[0].TargetEndMs);
        Assert.Null(preview.Segments[1].TargetEndMs);
        Assert.Equal(0, (await plans.PreviewAsync("media", [new(source, 0, 720000)], null)).TotalCount);
        Assert.Equal("DuplicateSegment", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.PreviewAsync("media", [new(source), new(source)], null))).Code);
        Assert.Equal("InvalidTime", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync("media", Id(), "overflow", [new(source, 0, null, CombinePlanService.MaximumTimeMs)], null))).Code);
        Assert.Equal("EmptyPlan", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync("media", Id(), "empty", [new(source, 800000)], null))).Code);
    }

    [Fact]
    public async Task SegmentAndCandidateCapsIncludeRepeatedInstances()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source"); var plans = Service(x);
        x.Execute($"""
            DELETE FROM Comments;
            WITH RECURSIVE n(x) AS (SELECT 0 UNION ALL SELECT x+1 FROM n WHERE x<19999)
            INSERT INTO Comments(FileId,SourceOrdinal,TimeMs,Text,Color,Mode,FontSize)
                SELECT '{source}',x,x,'bounded',16777215,1,25 FROM n;
            """);
        var rows = Enumerable.Range(0, 50).Select(i => new CombineSegment(source, 0, null, i)).ToArray();
        Assert.Equal(1000000, (await plans.PreviewAsync("media", rows, null)).TotalCount);
        await plans.SaveAsync("media", Id(), "million", rows, null);
        Assert.Equal("SegmentLimit", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.PreviewAsync("media", [.. rows, new(source, 0, null, 51)], null))).Code);
        x.Execute($"INSERT INTO Comments(FileId,SourceOrdinal,TimeMs,Text,Color,Mode,FontSize) VALUES('{source}',20000,20000,'extra',1,1,25)");
        var exactOverflow = rows.Select((r, i) => r with { SourceEndMs = i == 49 ? 20001 : 20000 }).ToArray();
        Assert.Equal("CommentLimit", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync("media", Id(), "too many", exactOverflow, null))).Code);
        Assert.Single(plans.List("media"));
    }

    [Fact]
    public async Task FirstAppendAndReplacementPreservePlanAndRejectStalePublish()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source", "library"); var plans = Service(x);
        var p = await plans.SaveAsync("media", Id(), "P", [new(source)], null, true, 0);
        var first = await x.PublishAsync("first");
        var state = await x.Bindings.ReadAsync("media");
        Assert.Equal(p.PlanId, state.ActivePlanId); Assert.Null(state.ActiveFileId);
        var batch = await x.BatchAsync(operation: "replace", replace: first);
        await x.UploadAsync(batch.BatchId, content: ImportLifecycleTestContext.Json("replacement"));
        p = await plans.SaveAsync("media", p.PlanId, "edited", p.Segments, p.Version);
        await x.Imports.ProcessPendingAsync();
        var pending = (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0];
        Assert.Equal("AwaitingConflictResolution", pending.TaskStatus);
        state = await x.Bindings.ReadAsync("media");
        await x.Imports.ResumeAsync(pending.TaskId!, state.Version);
        await x.Imports.ProcessPendingAsync();
        Assert.Equal("Completed", (await x.Imports.GetBatchAsync(batch.BatchId)).Slots[0].TaskStatus);
        Assert.Equal(p.PlanId, (await x.Bindings.ReadAsync("media")).ActivePlanId);
        Assert.Equal(source, plans.Get("media", p.PlanId).Segments[0].FileId);
    }

    [Fact]
    public async Task BindingSetFailureRollsBackNewBindingAndMissingSourceCannotBeSaved()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source"); var other = await x.PublishAsync("other", "other");
        var plans = Service(x); var p = await plans.SaveAsync("media", Id(), "P", [new(source)], null);
        var state = await x.Bindings.ReadAsync("media");
        await Assert.ThrowsAsync<ImportOperationException>(() => x.Bindings.UpdateBindingsAsync("media", state.Version,
            [source, other], "set", new("plan", p.PlanId, 0)));
        Assert.Equal(new[] { source }, (await x.Bindings.ReadAsync("media")).FileIds);
        x.Execute($"UPDATE Files SET Status='Deleting' WHERE FileId='{other}'");
        Assert.Equal("SourceUnavailable", (await Assert.ThrowsAsync<ImportOperationException>(() =>
            plans.SaveAsync("media", p.PlanId, "invalid", [new(other)], p.Version))).Code);
        Assert.Equal(source, plans.Get("media", p.PlanId).Segments[0].FileId);
    }

    [Fact]
    public async Task NameAndPlanLimitsAndActiveEditVersionAreEnforced()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("source"); var plans = Service(x);
        var state = await x.Bindings.ReadAsync("media");
        var p = await plans.SaveAsync("media", Id(), "combine-1", [new(source)], null, true, state.Version);
        await plans.SaveAsync("media", p.PlanId, "combine-1", [new(source)], p.Version);
        Assert.Equal(state.Version + 2, (await x.Bindings.ReadAsync("media")).Version);
        Assert.Equal("combine-2", plans.NextName("media"));
        Assert.Equal("PlanNameConflict", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync("media", Id(), "combine-1", [new(source)], null))).Code);
        await plans.SaveAsync("media", Id(), string.Concat(Enumerable.Repeat("😀", 100)), [new(source)], null);
        Assert.Equal("InvalidPlanName", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync("media", Id(), string.Concat(Enumerable.Repeat("😀", 101)), [new(source)], null))).Code);
        for (var i = 2; i < 20; i++) await plans.SaveAsync("media", Id(), "p" + i, [new(source)], null);
        Assert.Equal("PlanLimit", (await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync("media", Id(), "extra", [new(source)], null))).Code);
    }
}
