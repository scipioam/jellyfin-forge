namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// One abnormal source entry to persist in <c>ImportErrors</c>. The text summary is produced by
/// the parser and is bounded; the full source text stays in the original file.
/// </summary>
public sealed record ImportErrorRecord(long SourceOrdinal, string ReasonCodes, string? TextSummary);

/// <summary>
/// A persisted abnormal entry detail.
/// </summary>
public sealed record PersistedImportError(
    long ErrorId,
    string TaskId,
    long SourceOrdinal,
    string ReasonCodes,
    string? TextSummary,
    long CreatedAtUtcMs);

/// <summary>
/// Partial counter update for an import task. Only non-null members are written, so callers can
/// update the counters and the stage independently.
/// </summary>
public sealed record ImportTaskCounterUpdate(
    long? TotalComments = null,
    long? ProcessedComments = null,
    long? NormalComments = null,
    long? AbnormalComments = null,
    long? SkippedComments = null,
    string? Stage = null,
    double? StagePercent = null);
