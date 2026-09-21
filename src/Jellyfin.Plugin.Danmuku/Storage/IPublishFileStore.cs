namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Moves import files between the staging area and the published originals area.
/// These operations are deliberately outside any SQLite transaction; the database
/// transaction is the publish point.
/// </summary>
public interface IPublishFileStore
{
    /// <summary>Gets the absolute path of a staging file identified by its path relative to the data root.</summary>
    string GetStagingPath(string stagedRelativePath);

    /// <summary>Gets the absolute path of a published original file.</summary>
    string GetOriginalPath(string storedFileName);

    /// <summary>
    /// Moves a staged original upload into the originals directory. Re-running after a
    /// crash is a no-op when the target already exists and the source is gone.
    /// </summary>
    bool MoveStagedOriginalToOriginals(string stagedRelativePath, string storedFileName);

    /// <summary>Deletes a staged file. Missing files count as success.</summary>
    bool TryDeleteStagingFile(string stagedRelativePath);

    /// <summary>Deletes a staged parser asset. Missing files count as success.</summary>
    bool TryDeleteStagedAsset(string stagedAssetPath);

    /// <summary>Deletes a published original file. Missing files count as success.</summary>
    bool TryDeleteOriginal(string storedFileName);
}
