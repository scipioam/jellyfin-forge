using System.Diagnostics;
using System.Text.Json;
using Jellyfin.Plugin.Danmuku.Api;
using Jellyfin.Plugin.Danmuku.Playback;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class CombineAcceptanceContractTests
{
    private static CombinePlanService Plans(ImportLifecycleTestContext x) => new(x.Storage.Factory, x.Writes, x.Bindings, x.Clock);
    private static string Id() => Guid.NewGuid().ToString("N");

    [Fact]
    public async Task ConcurrentNamesAndActivationsHaveOneWinnerAndNoPartialLoser()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("source"); var plans = Plans(x);
        var state = await x.Bindings.ReadAsync("media");
        var ids = new[] { Id(), Id() };
        var results = await Task.WhenAll(ids.Select(async id =>
        {
            try { await plans.SaveAsync("media", id, "same", [new(file)], null, true, state.Version); return true; }
            catch (ImportOperationException) { return false; }
        }));
        Assert.Single(results, r => r);
        var winner = Assert.Single(plans.List("media"));
        Assert.Equal(winner.PlanId, (await x.Bindings.ReadAsync("media")).ActivePlanId);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM CombineSegments"));
        Assert.Equal(state.Version + 1, (await x.Bindings.ReadAsync("media")).Version);
        var edits = await Task.WhenAll(new[] { "first", "second" }.Select(async name =>
        {
            try { await plans.SaveAsync("media", winner.PlanId, name, [new(file, 0, null, name == "first" ? 10 : 20)], 1); return true; }
            catch (ImportOperationException e) { Assert.Equal("PlanVersionConflict", e.Code); return false; }
        }));
        Assert.Single(edits, r => r);
        Assert.Equal(2, plans.Get("media", winner.PlanId).Version);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM CombineSegments"));
    }

    [Fact]
    public async Task CancelledQueuedSaveDoesNotCreateReferencesOrChangeSelection()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("source"); var plans = Plans(x);
        var before = await x.Bindings.ReadAsync("media");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocking = x.Writes.EnqueueAsync(async (_, _) => { entered.SetResult(); await release.Task; });
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var pending = plans.SaveAsync("media", Id(), "cancel", [new(file)], null, true, before.Version, cancel.Token);
        cancel.Cancel(); release.SetResult(); await blocking;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Empty(plans.List("media"));
        Assert.Equal(before.Version, (await x.Bindings.ReadAsync("media")).Version);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM CombineSegments"));
    }

    [Fact]
    public async Task DeleteSourceAndSaveRaceCannotLeaveDanglingReferences()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("source"); var plans = Plans(x);
        var state = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", state.Version, [], null);
        var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
        var mark = deletion.MarkForDeletionAsync(file);
        var save = Record.ExceptionAsync(() => plans.SaveAsync("media", Id(), "race", [new(file)], null));
        var marked = await mark; var error = await save;
        if (marked.Status == FileDeletionMarkStatus.Marked)
        {
            Assert.IsType<ImportOperationException>(error);
            Assert.Empty(plans.List("media"));
        }
        else
        {
            Assert.Equal(FileDeletionMarkStatus.Referenced, marked.Status);
            Assert.Null(error); Assert.Single(plans.List("media"));
        }
        using var c = x.Storage.Factory.CreateOpenConnection();
        Assert.Empty(StorageTestSql.QueryNames(c, "PRAGMA foreign_key_check"));
    }

    [Fact]
    public async Task RangeExpansionReadsOverlapsOnceButKeepsEachSegmentInstance()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("duplicate");
        x.Execute($"""
            DELETE FROM Comments;
            INSERT INTO Comments(FileId,SourceOrdinal,TimeMs,Text,Color,Mode,FontSize) VALUES
              ('{file}',0,0,'same',1,1,25),('{file}',1,1,'same',1,1,25),
              ('{file}',2,59999,'same',1,1,25),('{file}',3,60000,'same',1,1,25),
              ('{file}',4,120000,'same',1,1,25);
            """);
        var plan = new CombinePlan("p", "media", "P", 1,
            [new(file, 0, 60000, 1000), new(file, 1, 120001, 2000), new(file, 60000, null, 0), new(file, 200000, null, 0)]);
        using var c = x.Storage.Factory.CreateOpenConnection(); using var t = c.BeginTransaction(deferred: true);
        var selected = CombinePlaybackSelector.Select(c, t, plan, null, 20);
        Assert.Equal(9, selected.CandidateCount); Assert.Equal(18, selected.ScannedCandidates);
        Assert.Equal(new long[] { 0, 1000, 1001, 2000, 60000, 60999, 61998, 61999, 121999 }, selected.Items.Select(i => i.TimeMs));
        Assert.Equal(9, selected.Items.Select(i => i.Id).Distinct().Count());
        Assert.All(selected.Items, i => Assert.Equal("same", i.Text));
        var capped = CombinePlaybackSelector.Select(c, t, plan, null, 3);
        Assert.Equal(capped.Items, CombinePlaybackSelector.Select(c, t, plan, null, 3).Items);
        Assert.Equal(3, capped.Items.Count);
        Assert.Empty(CombinePlaybackSelector.Select(c, t, plan with { Segments = [new(file, 0, null, 1000)] }, 999, 20).Items);
    }

    [Fact]
    public async Task MillionSparseMinutesAreBoundedAndCancellationReturnsPromptly()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("sparse");
        x.Execute($"""
            DELETE FROM Comments;
            WITH RECURSIVE n(x) AS (SELECT 0 UNION ALL SELECT x+1 FROM n WHERE x<19999)
            INSERT INTO Comments(FileId,SourceOrdinal,TimeMs,Text,Color,Mode,FontSize)
              SELECT '{file}',x,x*60000,'sparse',16777215,1,25 FROM n;
            """);
        var plan = new CombinePlan("sparse", "media", "Sparse", 1,
            Enumerable.Range(0, 50).Select(i => new CombineSegment(file, 0, null, i * 1_200_000_000L)).ToArray());
        using var c = x.Storage.Factory.CreateOpenConnection(); using var t = c.BeginTransaction(deferred: true);
        var startMemory = GC.GetTotalMemory(true); long peakMemory = startMemory;
        using var stop = new CancellationTokenSource();
        var monitor = new Thread(() => { while (!stop.IsCancellationRequested) { peakMemory = Math.Max(peakMemory, GC.GetTotalMemory(false)); Thread.Sleep(5); } });
        monitor.Start(); var clock = Stopwatch.StartNew();
        CombineSelection result;
        try { result = CombinePlaybackSelector.Select(c, t, plan, null, 20000); }
        finally { stop.Cancel(); monitor.Join(); }
        var elapsed = clock.Elapsed.TotalMilliseconds;
        Assert.Equal(1_000_000, result.CandidateCount); Assert.Equal(2_000_000, result.ScannedCandidates);
        Assert.Equal(20000, result.Items.Count);
        Assert.True(peakMemory - startMemory < 512L * 1024 * 1024, "sparse numeric state exceeded 512 MiB");
        using var cancel = new CancellationTokenSource();
        var cancelThread = new Thread(() => { Thread.Sleep(25); cancel.Cancel(); });
        cancelThread.Start(); clock.Restart();
        try { Assert.ThrowsAny<OperationCanceledException>(() => CombinePlaybackSelector.Select(c, t, plan, null, 20000, cancel.Token)); }
        finally { cancelThread.Join(); }
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "cancellation exceeded two seconds");
        var output = Environment.GetEnvironmentVariable("FORGE_SPARSE_RESULT");
        if (!string.IsNullOrEmpty(output)) File.WriteAllText(output, JsonSerializer.Serialize(new
        { candidateCount = result.CandidateCount, scannedCandidates = result.ScannedCandidates, selected = result.Items.Count,
          elapsedMs = elapsed, startManagedBytes = startMemory, peakManagedBytes = peakMemory, cancellationMs = clock.Elapsed.TotalMilliseconds }));
    }
}
