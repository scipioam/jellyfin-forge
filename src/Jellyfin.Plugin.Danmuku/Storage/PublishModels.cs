namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Persisted publish intent: which file identity is published to which media for a task.
/// </summary>
public sealed record PublishIntentRequest(
    string TaskId,
    string MediaId,
    string FileId,
    string OriginalFileName,
    string StoredFileName,
    string DisplayName,
    string Format,
    string ContentHash,
    long? LastCommentTimeMs,
    string ParseDataVersion,
    string? StagedOriginalPath = null,
    string? StagedAssetPath = null);

/// <summary>
/// Snapshot of a persisted publish intent.
/// </summary>
public sealed record PublishIntentSnapshot(
    string TaskId,
    string MediaId,
    string Operation,
    string FileId,
    string StoredFileName,
    string? ReplaceFileId,
    string? StagedOriginalPath,
    string? StagedAssetPath,
    long? IntentCreatedAtUtcMs);

/// <summary>
/// A normalized danmuku comment ready to be persisted inside the publish transaction.
/// </summary>
public sealed record CommentRecord(
    long SourceOrdinal,
    string? SourceId,
    long TimeMs,
    string Text,
    long Color,
    int Mode,
    int FontSize,
    long? SourceTimeMs = null,
    string? SenderHash = null,
    string? SourceNumericId = null,
    long? Weight = null,
    long? Attr = null,
    long? Pool = null);

public enum PublishOutcomeKind
{
    Published,
    AlreadyPublished
}

/// <summary>
/// Result of a publish attempt.
/// </summary>
public sealed record PublishOutcome(
    PublishOutcomeKind Kind,
    string TaskId,
    string MediaId,
    string FileId,
    bool BindingCreated,
    bool ActiveFileChanged,
    int ImportedComments);
