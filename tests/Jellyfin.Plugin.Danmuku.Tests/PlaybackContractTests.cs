using System.Text.Json;
using Jellyfin.Plugin.Danmuku.Api;
using Jellyfin.Plugin.Danmuku.Configuration;
using Jellyfin.Plugin.Danmuku.Playback;
using Jellyfin.Plugin.Danmuku.Storage;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public sealed class PlaybackContractTests
{
    [Theory]
    [InlineData(null, 5000)] [InlineData(1799999L, 5000)] [InlineData(1800000L, 8000)]
    [InlineData(3600000L, 8000)] [InlineData(3600001L, 10000)]
    public void ServerDurationDeterminesTier(long? duration, int expected) => Assert.Equal(expected, new PluginConfiguration().LoadLimit(duration));

    [Fact]
    public void ConfigurationRejectsOutOfRangeAndDecreasingValues()
    {
        Assert.Throws<ArgumentException>(() => new PluginConfiguration { ShortLoadLimit = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new PluginConfiguration { LongLoadLimit = 20001 }.Validate());
        Assert.Throws<ArgumentException>(() => new PluginConfiguration { ShortLoadLimit = 9000 }.Validate());
        Assert.Throws<ArgumentException>(() => new PluginConfiguration { HighDensity = 101 }.Validate());
        Assert.Throws<ArgumentException>(() => new PluginConfiguration { LowDensity = 31 }.Validate());
    }

    [Fact]
    public async Task RetryFreezesFileAndConfigurationAndConcurrentRequestsShareOneRecord()
    {
        await using var x = new ImportLifecycleTestContext();
        await x.PublishAsync("first");
        using var service = Service(x);
        var id = Guid.NewGuid().ToString("N");
        var identity = Identity();
        var responses = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Read(service, id, identity)));
        Assert.All(responses, r => Assert.Equal(responses[0], r));
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests"));
        var state = await x.Bindings.ReadAsync("media");
        await x.Bindings.UpdateAsync("media", state.Version, [], null);
        Assert.Equal(responses[0], await Read(service, id, identity, new() { WebEnabled = true, ShortLoadLimit = 2 }));
        Assert.Contains("Disabled", await Read(service, id, identity, new()));
        Assert.Equal(409, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, id, identity with { UserId = "other" }))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, id, identity with { SessionHash = "other" }))).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, id, identity with { MediaId = "other" }))).StatusCode);
        x.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(410, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, id, identity))).StatusCode);
    }

    [Fact]
    public async Task RestartNeverRedrawsOldIdentifiersAndDeletedSourceDoesNotBreakCachedPayload()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("first");
        var id = Guid.NewGuid().ToString("N");
        using (var first = Service(x))
        {
            var body = await Read(first, id, Identity());
            var state = await x.Bindings.ReadAsync("media");
            await x.Bindings.UpdateAsync("media", state.Version, [], null);
            var deletion = new FileDeletionCoordinator(x.Storage.Factory, x.Files, x.Writes);
            await deletion.MarkForDeletionAsync(file); await deletion.CleanupAsync();
            Assert.Equal(body, await Read(first, id, Identity()));
        }
        using var restarted = Service(x);
        Assert.Equal(410, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(restarted, id, Identity()))).StatusCode);
    }

    [Theory]
    [InlineData(1, 100000, 268435456L)] [InlineData(128, 1, 268435456L)]
    public async Task CapacityDoesNotEvictLiveCollections(int collections, int requests, long bytes)
    {
        await using var x = new ImportLifecycleTestContext();
        await x.PublishAsync("first");
        using var service = Service(x, new(bytes, collections, requests));
        var id = Guid.NewGuid().ToString("N");
        var first = await Read(service, id, Identity());
        Assert.Equal(503, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, Guid.NewGuid().ToString("N"), Identity()))).StatusCode);
        Assert.Equal(first, await Read(service, id, Identity()));
    }

    [Fact]
    public async Task DefaultCollectionCapRejects129thUntilExpiryWithoutForgettingIdentifiers()
    {
        await using var x = new ImportLifecycleTestContext();
        using var service = Service(x);
        var first = Guid.NewGuid().ToString("N");
        await Read(service, first, Identity());
        for (var i = 1; i < 128; i++) await Read(service, Guid.NewGuid().ToString("N"), Identity());
        Assert.Equal(128, Directory.GetFiles(x.Storage.Paths.PlaybackCachePath).Length);
        Assert.Equal(503, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, Guid.NewGuid().ToString("N"), Identity()))).StatusCode);
        x.Clock.Advance(TimeSpan.FromMinutes(10));
        await Read(service, Guid.NewGuid().ToString("N"), Identity());
        Assert.Single(Directory.GetFiles(x.Storage.Paths.PlaybackCachePath));
        Assert.Equal(128, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests WHERE Status='Expired'"));
        Assert.Equal(410, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, first, Identity()))).StatusCode);
    }

    [Fact]
    public async Task ByteBudgetRejectsAtomicallyAndInactiveSessionsCanBePruned()
    {
        await using var x = new ImportLifecycleTestContext();
        using (var tiny = Service(x, new(1)))
            Assert.Equal(503, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(tiny, Guid.NewGuid().ToString("N"), Identity()))).StatusCode);
        Assert.Equal(0, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests"));
        Assert.Empty(Directory.GetFiles(x.Storage.Paths.PlaybackCachePath));
        var lookup = new Sessions();
        using var service = Service(x, new(Requests: 1), lookup);
        await Read(service, Guid.NewGuid().ToString("N"), Identity());
        lookup.Active = false;
        await Read(service, Guid.NewGuid().ToString("N"), Identity() with { SessionHash = "new" });
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests"));
    }

    [Fact]
    public async Task MetadataPruningAdvancesPastTheFirstPageOfActiveSessions()
    {
        await using var x = new ImportLifecycleTestContext();
        var lookup = new Sessions();
        using var service = Service(x, new(Requests: 101), lookup);
        for (var i = 0; i < 101; i++)
            await Read(service, Guid.NewGuid().ToString("N"), Identity() with { SessionHash = $"session-{i:D3}" });
        x.Clock.Advance(TimeSpan.FromMinutes(10));
        lookup.Inactive.Add("session-100");
        Assert.Equal(503, (await Assert.ThrowsAsync<ImportOperationException>(() => Read(service, Guid.NewGuid().ToString("N"), Identity()))).StatusCode);
        await Read(service, Guid.NewGuid().ToString("N"), Identity());
        Assert.Equal(101, x.Scalar("SELECT COUNT(*) FROM PlaybackRequests"));
    }

    [Fact]
    public async Task BalancedSelectorIsStableAcrossConnectionsAndExcludesBeyondDuration()
    {
        await using var x = new ImportLifecycleTestContext();
        var batch = await x.BatchAsync();
        var input = Enumerable.Range(0, 302).Select(i => new { progress = i < 2 ? i : 60000 + (i - 2) * 1000, content = "c" + i });
        await x.UploadAsync(batch.BatchId, content: JsonSerializer.Serialize(input));
        await x.Imports.ProcessPendingAsync();
        var file = (await x.Bindings.ReadAsync("media")).ActiveFileId!;
        IReadOnlyList<PlaybackComment> Select(int limit)
        {
            using var c = x.Storage.Factory.CreateOpenConnection(); using var t = c.BeginTransaction(deferred: true);
            return PlaybackSelector.Select(c, t, "media", file, 300000, limit);
        }
        var first = Select(20);
        Assert.Equal(first, Select(20));
        Assert.Equal(20, first.Count);
        Assert.Equal(2, first.Count(c => c.TimeMs < 60000));
        Assert.All(first, c => Assert.InRange(c.TimeMs, 0, 300000));
        Assert.Equal(first.OrderBy(c => c.TimeMs), first);
        Assert.Equal(2, Select(2).Count);
        Assert.Equal(243, Select(20000).Count);
        using var unknownConnection = x.Storage.Factory.CreateOpenConnection();
        using var unknownTransaction = unknownConnection.BeginTransaction(deferred: true);
        Assert.Equal(302, PlaybackSelector.Select(unknownConnection, unknownTransaction, "media", file, null, 20000).Count);
    }

    [Fact]
    public async Task ManagementQueriesUseBoundedProjections()
    {
        await using var x = new ImportLifecycleTestContext();
        var file = await x.PublishAsync("secret body");
        var q = new ManagementQueries(x.Storage.Factory, x.Files);
        Assert.Throws<ImportOperationException>(() => q.Files(null, false, 0, 101));
        Assert.Equal(1, q.Files(null, false, 0, 50).TotalCount);
        Assert.Equal(0, q.Files(null, true, 0, 50).TotalCount);
        Assert.True((long)q.File(file)["SizeBytes"]! > 0);
        var task = q.Imports(null, 0, 50).Items.Single();
        Assert.DoesNotContain("secret body", JsonSerializer.Serialize(task));
        Assert.Empty(q.Errors((string)task["TaskId"]!, 0, 50).Items);
        var batch = await x.BatchAsync();
        var received = await x.UploadAsync(batch.BatchId, content: "[{\"progress\":1,\"content\":\"valid\"},{\"content\":\"missing time\"}]");
        await x.Imports.ProcessPendingAsync();
        var error = Assert.Single(q.Errors(received.TaskId!, 0, 50).Items);
        Assert.Equal("Time field is missing", error["Description"]);
        Assert.IsType<string>(error["ErrorId"]);
    }

    [Theory]
    [InlineData(2, "1,13")]
    [InlineData(10, "1,2,3,11,14,15,16,17,19,20")]
    public void GoldenSelectionUsesPortableHashSerialization(int limit, string expected)
    {
        using var x = new StorageTestContext(); x.CreateMigrator().Migrate();
        using (var c = x.Factory.CreateOpenConnection())
        {
            StorageTestSql.InsertFile(c, "golden-file");
            var ordinal = 0;
            foreach (var pair in new[] { (0, 2), (1, 11), (2, 3), (3, 5) })
                for (var offset = 0; offset < pair.Item2; offset++)
                    StorageTestSql.Execute(c, "INSERT INTO Comments(FileId,SourceOrdinal,TimeMs,Text,Color,Mode,FontSize) VALUES('golden-file',$ordinal,$time,'golden',16777215,1,25)",
                        ("$ordinal", ++ordinal), ("$time", pair.Item1 * 60000 + offset * 1000));
        }
        using var read = x.Factory.CreateOpenConnection(); using var t = read.BeginTransaction(deferred: true);
        Assert.Equal(expected, string.Join(',', PlaybackSelector.Select(read, t, "golden-media", "golden-file", 240000, limit).Select(i => i.Id)));
    }

    private static PlaybackIdentity Identity() => new("user", "session-hash", "media", null);
    private static PlaybackService Service(ImportLifecycleTestContext x, PlaybackBudgets? limits = null, Sessions? sessions = null) => new(x.Storage.Factory, x.Writes, x.Storage.Paths, x.Clock, sessions ?? new(), limits ?? new());
    private static async Task<string> Read(PlaybackService service, string id, PlaybackIdentity identity, PluginConfiguration? config = null)
    {
        using var stream = await service.GetAsync(id, identity, config ?? new() { WebEnabled = true });
        using var reader = new StreamReader(stream); return await reader.ReadToEndAsync();
    }
    private sealed class Sessions : IPlaybackSessionLookup
    {
        public bool Active { get; set; } = true;
        public HashSet<string> Inactive { get; } = new(StringComparer.Ordinal);
        public Task<bool> IsActiveAsync(string user, string hash) => Task.FromResult(Active && !Inactive.Contains(hash));
    }
}
