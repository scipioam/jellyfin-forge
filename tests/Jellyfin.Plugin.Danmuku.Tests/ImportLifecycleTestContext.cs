using System.Text;
using Jellyfin.Plugin.Danmuku.Import;
using Jellyfin.Plugin.Danmuku.Storage;

namespace Jellyfin.Plugin.Danmuku.Tests;

internal sealed class ImportLifecycleTestContext : IAsyncDisposable
{
    public StorageTestContext Storage { get; } = new();
    public TestClock Clock { get; } = new();
    public TestMediaLookup Lookup { get; } = new();
    public SqliteWriteCoordinator Writes { get; }
    public IPublishFileStore Files { get; }
    public MediaBindingService Bindings { get; }
    public ImportService Imports { get; }
    public IPublishService Publisher { get; }

    public ImportLifecycleTestContext(Func<IPublishService, IPublishService>? wrap = null)
    {
        Storage.CreateMigrator().Migrate();
        Writes = new(Storage.Factory);
        Files = new PublishFileStore(Storage.Paths);
        Bindings = new(Storage.Factory, Writes, Lookup, Clock);
        Publisher = new PublishService(Storage.Factory, Files, Writes, Clock);
        Imports = new(Storage.Factory, Writes, Files, wrap?.Invoke(Publisher) ?? Publisher,
            new ImportTaskStore(Storage.Factory, Writes), Bindings, Clock);
    }

    public async Task<ImportBatchSnapshot> BatchAsync(string[]? names = null, string media = "media", string operation = "append", string? replace = null)
    {
        var state = await Bindings.ReadAsync(media);
        return await Imports.CreateBatchAsync(new(Guid.NewGuid().ToString("N"), media, state.Version, names ?? ["sample.json"], operation, replace));
    }

    public Task<ImportSlotSnapshot> UploadAsync(string batch, int slot = 0, string? content = null) =>
        UploadCoreAsync(batch, slot, content ?? Json("synthetic"));

    private async Task<ImportSlotSnapshot> UploadCoreAsync(string batch, int slot, string content)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(content));
        return await Imports.ReceiveAsync(batch, slot, source, source.Length);
    }

    public async Task<string> PublishAsync(string text, string media = "media", string name = "sample.json")
    {
        var batch = await BatchAsync([name], media);
        await UploadAsync(batch.BatchId, content: Json(text));
        await Imports.ProcessPendingAsync();
        return (await Bindings.ReadAsync(media)).FileIds.Single(id => ScalarText("SELECT Text FROM Comments WHERE FileId='" + id + "'") == text);
    }

    public long Scalar(string sql)
    {
        using var c = Storage.Factory.CreateOpenConnection();
        return StorageTestSql.ScalarLong(c, sql);
    }
    public string? ScalarText(string sql)
    {
        using var c = Storage.Factory.CreateOpenConnection();
        return StorageTestSql.ScalarString(c, sql);
    }
    public void Execute(string sql)
    {
        using var c = Storage.Factory.CreateOpenConnection();
        StorageTestSql.Execute(c, sql);
    }
    public static string Json(string text) => "[{\"progress\":1,\"content\":\"" + text + "\"}]";
    public async ValueTask DisposeAsync() { await Writes.DisposeAsync(); Storage.Dispose(); }
}

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 9, 22, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan duration) => _now += duration;
}

internal sealed class TestMediaLookup : IMediaPresenceLookup
{
    public Dictionary<string, MediaPresence> Results { get; } = new();
    public Task<MediaPresence> CheckAsync(string mediaId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Results.GetValueOrDefault(mediaId, MediaPresence.Exists));
}

internal sealed class PausedUploadStream : MemoryStream
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public PausedUploadStream() : base(Encoding.UTF8.GetBytes(ImportLifecycleTestContext.Json("paused"))) { }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Entered.TrySetResult();
        await Release.Task.WaitAsync(cancellationToken);
        return await base.ReadAsync(buffer, cancellationToken);
    }
}
