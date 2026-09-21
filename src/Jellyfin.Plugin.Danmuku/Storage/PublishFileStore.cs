namespace Jellyfin.Plugin.Danmuku.Storage;

/// <inheritdoc cref="IPublishFileStore" />
public sealed class PublishFileStore : IPublishFileStore
{
    private readonly DanmukuDataPaths _paths;

    public PublishFileStore(DanmukuDataPaths paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public string GetStagingPath(string stagedRelativePath)
    {
        // Staging paths are recorded relative to the data root and must resolve inside
        // the staging directory.
        var fullPath = ResolveWithin(_paths.RootPath, stagedRelativePath, nameof(stagedRelativePath));
        var stagingRoot = _paths.StagingPath.EndsWith(Path.DirectorySeparatorChar)
            ? _paths.StagingPath
            : _paths.StagingPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(stagingRoot, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The path '{stagedRelativePath}' is not inside the Danmuku staging directory.");
        }

        return fullPath;
    }

    public string GetOriginalPath(string storedFileName) =>
        ResolveWithin(_paths.OriginalFilesPath, storedFileName, nameof(storedFileName));

    public bool MoveStagedOriginalToOriginals(string stagedRelativePath, string storedFileName)
    {
        var source = GetStagingPath(stagedRelativePath);
        var target = GetOriginalPath(storedFileName);

        if (File.Exists(target))
        {
            // A previous attempt already moved the file; never overwrite it.
            if (File.Exists(source))
            {
                throw new IOException($"Both the staged file and the published original exist for '{storedFileName}'.");
            }

            return false;
        }

        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"The staged original file '{stagedRelativePath}' does not exist.", source);
        }

        Directory.CreateDirectory(_paths.OriginalFilesPath);
        File.Move(source, target);
        return true;
    }

    public bool TryDeleteStagingFile(string stagedRelativePath) => TryDelete(GetStagingPath(stagedRelativePath));

    public bool TryDeleteStagedAsset(string stagedAssetPath) => TryDelete(GetStagingPath(stagedAssetPath));

    public bool TryDeleteOriginal(string storedFileName) => TryDelete(GetOriginalPath(storedFileName));

    private static bool TryDelete(string path)
    {
        if (File.Exists(path))
        {
            try
            {
                File.Delete(path);
                return true;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        // A missing file is already deleted; a directory at the expected file path is a
        // deletion failure that must stay retryable instead of being reported as success.
        return !Directory.Exists(path);
    }

    private static string ResolveWithin(string root, string relativePath, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("A path is required.", parameterName);
        }

        var fullRoot = Path.GetFullPath(root);
        var fullPath = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var prefix = fullRoot.EndsWith(Path.DirectorySeparatorChar)
            ? fullRoot
            : fullRoot + Path.DirectorySeparatorChar;

        if (!fullPath.StartsWith(prefix, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"The path '{relativePath}' escapes the Danmuku data directory.");
        }

        return fullPath;
    }
}
