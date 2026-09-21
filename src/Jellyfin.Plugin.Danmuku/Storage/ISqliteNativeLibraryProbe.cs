namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Verifies that the plugin-bundled SQLite native library is available before any
/// storage operation starts.
/// </summary>
public interface ISqliteNativeLibraryProbe
{
    /// <summary>
    /// Returns the absolute path of the bundled asset, or throws
    /// <see cref="SqliteNativeLibraryUnavailableException"/> when it is missing or cannot
    /// be loaded. Callers must not continue with the Server-provided SQLite library.
    /// </summary>
    string EnsureAvailable();
}

/// <summary>
/// Default probe backed by <see cref="SqliteNativeLibraryResolver"/>.
/// </summary>
public sealed class SqliteNativeLibraryProbe : ISqliteNativeLibraryProbe
{
    public string EnsureAvailable() => SqliteNativeLibraryResolver.EnsureBundledAssetLoaded();
}
