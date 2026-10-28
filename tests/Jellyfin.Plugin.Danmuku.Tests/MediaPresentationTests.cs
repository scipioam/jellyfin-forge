using System.Reflection;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Entities.Security;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Danmuku.Configuration;
using Jellyfin.Plugin.Danmuku.Api;
using Jellyfin.Plugin.Danmuku.Media;
using Jellyfin.Plugin.Danmuku.Storage;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Audio;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.LiveTv;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Querying;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Danmuku.Tests;

public class MediaLibraryProxy : DispatchProxy
{
    public Func<MethodInfo, object?[]?, object?> Call { get; set; } = (_, _) => throw new InvalidOperationException("unexpected query");
    protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    public static ILibraryManager Create(Func<MethodInfo, object?[]?, object?> call)
    {
        var library = Create<ILibraryManager, MediaLibraryProxy>();
        ((MediaLibraryProxy)library).Call = call;
        return library;
    }
}

public sealed class MediaPresentationTests
{
    public MediaPresentationTests()
    {
        Video.RecordingsManager = DispatchProxy.Create<IRecordingsManager, MediaLibraryProxy>();
        ((MediaLibraryProxy)Video.RecordingsManager).Call = (_, _) => null;
    }

    [Fact]
    public async Task ExactTypesAndOwnershipUseOnePresencePolicy()
    {
        BaseItem[] allowed = [new Movie(), new Episode(), new Video()];
        BaseItem[] rejected = [new MusicVideo(), new Trailer(), new LiveTvChannel(), new LiveTvProgram(),
            new Series(), new Season(), new Folder(), new Audio(), new Photo(), new CustomVideo(),
            new Video { ExtraType = ExtraType.Clip }, new Video { OwnerId = Guid.NewGuid() },
            new Movie { ExtraType = ExtraType.Trailer }, new Video { ChannelId = Guid.NewGuid() }];
        foreach (var item in allowed.Concat(rejected))
        {
            var expected = allowed.Any(value => ReferenceEquals(value, item));
            Assert.Equal(expected, MediaEligibility.CanBind(item));
            var lookup = new JellyfinMediaPresenceLookup(MediaLibraryProxy.Create((_, _) => item));
            Assert.Equal(expected ? MediaPresence.Exists : MediaPresence.Missing, await lookup.CheckAsync(Guid.NewGuid().ToString()));
        }
        Assert.Equal(MediaPresence.CheckFailed, await new JellyfinMediaPresenceLookup(MediaLibraryProxy.Create((_, _) => throw new IOException())).CheckAsync(Guid.NewGuid().ToString()));
        Assert.True(MediaEligibility.CanNavigate(new Series()));
        Assert.True(MediaEligibility.CanNavigate(new Season()));
        Assert.False(MediaEligibility.CanNavigate(new Video()));
    }

    private sealed class CustomVideo : Video;

    [Fact]
    public async Task PlaybackControllerRejectsExcludedSdkEntitiesBeforeCreatingPlayback()
    {
        var user = new User("viewer", "test", "test");
        user.SetPermission(PermissionKind.IsDisabled, false);
        user.SetPermission(PermissionKind.EnableMediaPlayback, true);
        var auth = DispatchProxy.Create<IAuthorizationContext, MediaLibraryProxy>();
        ((MediaLibraryProxy)auth).Call = (_, _) => Task.FromResult(new AuthorizationInfo { User = user, Token = "test-token" });
        var devices = DispatchProxy.Create<IDeviceManager, MediaLibraryProxy>();
        ((MediaLibraryProxy)devices).Call = (_, _) => new QueryResult<Device> { Items = [new(user.Id, "test", "1", "test", "test")] };
        BaseItem[] excluded = [new LiveTvChannel(), new LiveTvProgram(), new MusicVideo(), new Trailer(),
            new Video { ExtraType = ExtraType.Unknown }, new Video { OwnerId = Guid.NewGuid() },
            new Video { ChannelId = Guid.NewGuid() }, new CustomVideo(), new Audio(), new Photo(), new Folder()];
        foreach (var item in excluded)
        {
            var queried = false;
            var library = MediaLibraryProxy.Create((method, _) =>
            {
                Assert.Equal("GetItemById", method.Name); queried = true; return item;
            });
            // A null playback service ensures these types cannot reach snapshot creation.
            var controller = new PlaybackController(auth, library, devices, null!, new PluginConfiguration())
            {
                ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
            };
            Assert.IsType<ForbidResult>(await controller.Get(Guid.NewGuid(), Guid.NewGuid().ToString("N"), default, "m2-speed-v1"));
            Assert.True(queried); // The authenticated user passed the permission checks.
        }
    }

    [Fact]
    public void TitlesDoNotInventMissingSeriesNumbersOrYear()
    {
        Assert.Equal("剧名 · S00E02 · 特别篇", MediaPresentationService.Title(new Episode { Name = "特别篇", SeriesName = "剧名", ParentIndexNumber = 0, IndexNumber = 2 }));
        Assert.Equal("E123 · 1.mkv", MediaPresentationService.Title(new Episode { Name = " ", IndexNumber = 123, Path = "/media/1.mkv" }));
        Assert.Equal("电影（2026）", MediaPresentationService.Title(new Movie { Name = "电影", ProductionYear = 2026 }));
        Assert.Equal("电影", MediaPresentationService.Title(new Movie { Name = "电影" }));
        Assert.Equal("剧名 · 第 1 季", MediaPresentationService.Title(new Season { Name = "第 1 季", SeriesName = "剧名" }));
        Assert.Equal("<视频 & 名>", MediaPresentationService.Title(new Video { Name = "<视频 & 名>" }));
        var video = new Video { Id = Guid.NewGuid(), Path = "https://example.test/private?token=secret" };
        Assert.Equal(video.Id.ToString("N"), MediaPresentationService.Title(video));
    }

    [Fact]
    public void LocationsUseConfiguredRootOrderAndBoundariesWithStableLibraryOrder()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mb-roots"));
        var video = new Video { Id = Guid.NewGuid(), Path = Path.Combine(root, "two", "dir", "1.mkv") };
        var id = Guid.NewGuid();
        var one = new MediaLibraryLocation(id, "视频", [Path.Combine(root, "one"), Path.Combine(root, "two"), Path.Combine(root, "one") + Path.DirectorySeparatorChar]);
        Assert.Equal("视频 / 目录 2 / dir / 1.mkv", MediaPresentationService.Location(video, [one]));
        Assert.Equal("视频 / 目录 1 / dir / 1.mkv", MediaPresentationService.Location(video, [one with { Roots = [Path.Combine(root, "two"), Path.Combine(root, "one")] }]));
        Assert.Equal("视频 / 目录 2 / 1.mkv", MediaPresentationService.Location(video, [one with { Roots = [root, Path.Combine(root, "two", "dir")] }]));
        var other = one with { Id = Guid.NewGuid() };
        var both = MediaPresentationService.Location(video, [one, other]);
        Assert.Equal(both, MediaPresentationService.Location(video, [other, one]));
        Assert.Contains(id.ToString("N"), both);
        video.Path = Path.Combine(root, "two-other", "1.mkv");
        Assert.Equal("视频 / 1.mkv / " + video.Id.ToString("N"), MediaPresentationService.Location(video, [one]));
        video.Path = Path.Combine(root, "two", "..", "outside", "1.mkv");
        Assert.DoesNotContain("..", MediaPresentationService.Location(video, [one]));
        Assert.DoesNotContain(root, MediaPresentationService.Location(video, [one]));
        video.Path = "https://example.test/video?token=secret";
        Assert.Equal("视频 / " + video.Id.ToString("N"), MediaPresentationService.Location(video, [one]));
    }

    [Fact]
    public void MetadataFailureAndRequestCachePreserveUsableDescriptor()
    {
        var seriesId = Guid.NewGuid(); var calls = 0;
        var service = new MediaPresentationService(MediaLibraryProxy.Create((method, _) =>
        {
            if (method.Name == "GetItemById") { calls++; return new Series { Name = "系列" }; }
            throw new IOException("metadata unavailable");
        }));
        var episode = new Episode { Id = Guid.NewGuid(), SeriesId = seriesId, Name = "一" };
        Assert.Equal("系列 · 一", service.Describe(episode).DisplayTitle);
        Assert.True(service.Describe(episode).CanBind);
        Assert.Equal(1, calls);
        var failing = new MediaPresentationService(MediaLibraryProxy.Create((_, _) => throw new IOException()));
        Assert.Equal(episode.Id.ToString("N"), failing.Describe(episode.Id).DisplayTitle);
    }

    [Fact]
    public void DescriptorUsesConfiguredRootOrderRatherThanSortedVirtualLocations()
    {
        var root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "mb-config"));
        var a = Path.Combine(root, "a"); var b = Path.Combine(root, "b"); var id = Guid.NewGuid();
        var calls = 0;
        var library = MediaLibraryProxy.Create((method, _) => method.Name switch
        {
            "GetVirtualFolders" => Folders(),
            "GetCollectionFolders" => new List<Folder> { new() { Id = id, Name = "库" } },
            _ => throw new InvalidOperationException()
        });
        List<VirtualFolderInfo> Folders()
        {
            calls++;
            return [new() { ItemId = id.ToString("N"), Name = "库", Locations = [a, b],
                LibraryOptions = new LibraryOptions { PathInfos = [new MediaPathInfo { Path = b }, new MediaPathInfo { Path = a }] } }];
        }
        var service = new MediaPresentationService(library);
        Assert.Equal("库 / 目录 2 / 1.mkv", service.Describe(new Video { Id = Guid.NewGuid(), Path = Path.Combine(a, "1.mkv") }).LocationText);
        Assert.Equal("库 / 目录 1 / 1.mkv", service.Describe(new Video { Id = Guid.NewGuid(), Path = Path.Combine(b, "1.mkv") }).LocationText);
        Assert.Equal(1, calls);
    }

    [Fact]
    public void RootLookupFailureKeepsKnownLibraryNameAndIsCachedOnlyForThisRequest()
    {
        var calls = 0; var id = Guid.NewGuid();
        var library = MediaLibraryProxy.Create((method, _) =>
        {
            if (method.Name == "GetVirtualFolders") { calls++; throw new IOException(); }
            if (method.Name == "GetCollectionFolders") return new List<Folder> { new() { Id = id, Name = "已知库" } };
            throw new InvalidOperationException();
        });
        var item = new Video { Id = Guid.NewGuid(), Path = Path.Combine(Path.GetTempPath(), "1.mkv") };
        var service = new MediaPresentationService(library);
        Assert.Equal("已知库 / 1.mkv / " + item.Id.ToString("N"), service.Describe(item).LocationText);
        Assert.True(service.Describe(item).CanBind); Assert.Equal(1, calls);
        new MediaPresentationService(library).Describe(item);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void ListingKeepsUnsupportedRowsAndOriginalPagingContract()
    {
        var items = new BaseItem[] { new Video { Id = Guid.NewGuid(), Name = "独立" }, new Video { Id = Guid.NewGuid(), ExtraType = ExtraType.Clip }, new Series { Id = Guid.NewGuid() } };
        var library = MediaLibraryProxy.Create((method, args) =>
        {
            if (method.Name == "GetItemsResult")
            {
                var query = Assert.IsType<InternalItemsQuery>(args![0]);
                Assert.Equal(50, query.StartIndex); Assert.Equal(3, query.Limit); Assert.Equal("原词", query.SearchTerm);
                Assert.Contains(Jellyfin.Data.Enums.BaseItemKind.Video, query.IncludeItemTypes);
                return new QueryResult<BaseItem> { Items = items, TotalRecordCount = 67 };
            }
            throw new IOException();
        });
        var controller = new ManagementController(null!, null!, null!, null!, null!, library, new(library));
        // Jellyfin retains CLR property casing; the existing lower-case API fields
        // must remain stable without relying on a global camel-case policy.
        var json = JsonSerializer.SerializeToElement(controller.Media("原词", startIndex: 50, limit: 3));
        Assert.Equal(67, json.GetProperty("totalCount").GetInt32());
        var rows = json.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(3, rows.Length); Assert.True(rows[0].GetProperty("canBind").GetBoolean());
        Assert.Equal(items[0].Id.ToString("N"), rows[0].GetProperty("id").GetString());
        Assert.Equal("独立", rows[0].GetProperty("name").GetString());
        Assert.Equal("Video", rows[0].GetProperty("type").GetString());
        Assert.Equal("独立", rows[0].GetProperty("displayTitle").GetString());
        Assert.True(rows[0].TryGetProperty("locationText", out _));
        Assert.False(rows[1].GetProperty("canBind").GetBoolean()); Assert.False(rows[1].GetProperty("canNavigate").GetBoolean());
        Assert.True(rows[2].GetProperty("canNavigate").GetBoolean());
        Assert.Throws<ImportOperationException>(() => controller.Media(limit: 101));
    }

    [Fact]
    public async Task ExistingExcludedVideoCanBeDisabledUnboundAndCleanedWithoutDeletingSources()
    {
        await using var x = new ImportLifecycleTestContext();
        var id = Guid.NewGuid().ToString("N");
        var item = new Video { Id = Guid.Parse(id), Name = "普通视频" };
        var library = MediaLibraryProxy.Create((method, _) => method.Name == "GetItemById" ? item : throw new IOException());
        var bindings = new MediaBindingService(x.Storage.Factory, x.Writes, new JellyfinMediaPresenceLookup(library), x.Clock);
        var plans = new CombinePlanService(x.Storage.Factory, x.Writes, bindings, x.Clock);
        var file = await x.PublishAsync("source", id);
        var state = await bindings.ReadAsync(id);
        var plan = await plans.SaveAsync(id, Guid.NewGuid().ToString("N"), "saved", [new(file)], null, true, state.Version);
        item.ExtraType = ExtraType.BehindTheScenes;
        Assert.Equal(MediaPresence.Missing, await bindings.CheckAsync(id));
        var controller = new CombinePlansController(plans, bindings, library, new(library));
        var json = JsonSerializer.SerializeToElement(await controller.List(id, default), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(id, json.GetProperty("media").GetProperty("id").GetString());
        Assert.False(json.GetProperty("media").GetProperty("canBind").GetBoolean());
        Assert.Single(json.GetProperty("items").EnumerateArray());
        await Assert.ThrowsAsync<ImportOperationException>(() => plans.SaveAsync(id, plan.PlanId, "changed", [new(file)], plan.Version));
        state = await bindings.ReadAsync(id);
        await Assert.ThrowsAsync<ImportOperationException>(() => bindings.UpdateSelectionAsync(id, state.Version, new("file", file)));
        state = await bindings.UpdateSelectionAsync(id, state.Version, new("disabled"));
        await plans.DeleteAsync(id, plan.PlanId, plan.Version, state.Version, null);
        state = await bindings.ReadAsync(id);
        await bindings.UpdateBindingsAsync(id, state.Version, [], "preserve", null);
        Assert.Empty(plans.List(id));
        Assert.Empty((await bindings.ReadAsync(id)).FileIds);
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Files"));
        Assert.Equal(1, x.Scalar("SELECT COUNT(*) FROM Comments"));
    }
}
