using System.Text.Json;
using Jellyfin.Plugin.Danmuku.Playback;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class CombinePlaybackContractTests
{
    [Fact]
    public async Task InstancesHaveDistinctIdsAndGlobalLimitAndStableOrder()
    {
        await using var x = new ImportLifecycleTestContext();
        var source = await x.PublishAsync("same");
        x.Execute("UPDATE Comments SET TimeMs=720000");
        var plan = new CombinePlan("p", "media", "P", 1, [new(source, 600000, 1200000, 1200000), new(source, 720000, null, 1320000)]);
        using var c = x.Storage.Factory.CreateOpenConnection(); using var t = c.BeginTransaction(deferred: true);
        var all = CombinePlaybackSelector.Select(c, t, plan, null, 10);
        Assert.Equal(2, all.Items.Count); Assert.Equal(2, all.CandidateCount); Assert.Equal(4, all.ScannedCandidates);
        Assert.All(all.Items, item => { Assert.Equal("same", item.Text); Assert.Equal(1320000, item.TimeMs); });
        Assert.StartsWith("0:", all.Items[0].Id); Assert.StartsWith("1:", all.Items[1].Id);
        Assert.Single(CombinePlaybackSelector.Select(c, t, plan, null, 1).Items);
        Assert.Equal(all.Items, CombinePlaybackSelector.Select(c, t, plan, 1320000, 10).Items);
        Assert.Empty(CombinePlaybackSelector.Select(c, t, plan, 1319999, 10).Items);
    }

    [Fact]
    public async Task CacheSurvivesPlanEditDeleteAndSourceDeleteAndRestartExpiresIt()
    {
        await using var x = new ImportLifecycleTestContext(); var source = await x.PublishAsync("source", "library");
        var plans = new CombinePlanService(x.Storage.Factory, x.Writes, x.Bindings, x.Clock);
        var p = await plans.SaveAsync("media", Guid.NewGuid().ToString("N"), "P", [new(source), new(source, 0, null, 10)], null, true, 0);
        var id = Guid.NewGuid().ToString("N");
        using (var service = Service(x))
        {
            var body = await Read(service, id);
            using var payload = JsonDocument.Parse(body);
            Assert.Equal("plan", payload.RootElement.GetProperty("sourceKind").GetString());
            Assert.Equal(1, payload.RootElement.GetProperty("planVersion").GetInt64());
            Assert.Equal(2, payload.RootElement.GetProperty("selectedCount").GetInt32());
            p = await plans.SaveAsync("media", p.PlanId, "edited", [new(source, 0, null, 100)], 1);
            Assert.Equal(body, await Read(service, id));
            var next = await Read(service, Guid.NewGuid().ToString("N"));
            Assert.NotEqual(body, next);
            await plans.DeleteAsync("media", p.PlanId, p.Version, 2, new("disabled"));
            var state = await x.Bindings.ReadAsync("library");
            await x.Bindings.UpdateAsync("library", state.Version, [], null);
            var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
            await deletion.MarkForDeletionAsync(source); await deletion.CleanupAsync();
            Assert.Equal(body, await Read(service, id));
        }
        using var restarted = Service(x);
        Assert.Equal(410, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(restarted, id))).StatusCode);
    }

    [Fact]
    public async Task MissingSourceFailsWholePlanAndUnsuccessfulIdCanRetry()
    {
        await using var x = new ImportLifecycleTestContext(); var source = await x.PublishAsync("source");
        var plans = new CombinePlanService(x.Storage.Factory, x.Writes, x.Bindings, x.Clock);
        await plans.SaveAsync("media", Guid.NewGuid().ToString("N"), "P", [new(source)], null, true, 1);
        x.Execute($"UPDATE Files SET Status='DeleteFailed' WHERE FileId='{source}'");
        using var service = Service(x); var id = Guid.NewGuid().ToString("N");
        Assert.Equal("SourceUnavailable", (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, id))).Code);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests"));
        x.Execute($"UPDATE Files SET Status='Published' WHERE FileId='{source}'");
        Assert.Contains("source", await Read(service, id));
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests"));
    }

    private static PlaybackService Service(ImportLifecycleTestContext x) => new(x.Storage.Factory, x.Writes, x.Storage.Paths, x.Clock, new Sessions(), new());
    private static async Task<string> Read(PlaybackService service, string id)
    {
        using var stream = await service.GetAsync(id, new("user", "session", "media", null), new() { WebEnabled = true });
        using var reader = new StreamReader(stream); return await reader.ReadToEndAsync();
    }
    private sealed class Sessions : IPlaybackSessionLookup { public Task<bool> IsActiveAsync(string userId, string sessionHash) => Task.FromResult(true); }
}
