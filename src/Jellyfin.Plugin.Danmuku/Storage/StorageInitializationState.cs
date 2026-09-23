namespace Jellyfin.Plugin.Danmuku.Storage;

public sealed class StorageInitializationState
{
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> Ready => _ready.Task;
    internal void Complete(bool success) => _ready.TrySetResult(success);
}
