using MediaBrowser.Common.Configuration;

namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Resolves the Danmuku data directory and its well-known child locations.
/// The root lives under the Jellyfin application data path and is created on demand.
/// </summary>
public sealed class DanmukuDataPaths
{
    public const string RootFolderName = "Danmuku";

    public DanmukuDataPaths(IApplicationPaths applicationPaths)
        : this(Path.Combine(
            (applicationPaths ?? throw new ArgumentNullException(nameof(applicationPaths))).DataPath,
            RootFolderName))
    {
    }

    public DanmukuDataPaths(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = Path.GetFullPath(rootPath);
        EnsureCreated();
    }

    public string RootPath { get; }

    public string DatabasePath => Path.Combine(RootPath, "danmuku.db");

    /// <summary>Gets the directory that keeps pre-migration consistency backups.</summary>
    public string MigrationsPath => Path.Combine(RootPath, "migrations");

    /// <summary>Gets the directory that keeps published original upload files.</summary>
    public string OriginalFilesPath => Path.Combine(RootPath, "originals");

    /// <summary>Gets the directory used while receiving and parsing imports.</summary>
    public string StagingPath => Path.Combine(RootPath, "staging");

    /// <summary>Gets the disposable playback retry cache directory.</summary>
    public string PlaybackCachePath => Path.Combine(RootPath, "playback-cache");

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(MigrationsPath);
        Directory.CreateDirectory(OriginalFilesPath);
        Directory.CreateDirectory(StagingPath);
        Directory.CreateDirectory(PlaybackCachePath);
    }
}
