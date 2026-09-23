namespace Jellyfin.Plugin.Danmuku.Storage;

public sealed record CombineSegment(string FileId, long SourceStartMs = 0, long? SourceEndMs = null, long TargetStartMs = 0);
public sealed record CombinePlan(string PlanId, string MediaId, string Name, long Version,
    IReadOnlyList<CombineSegment> Segments);
public sealed record CombineSegmentStatistics(long Count, long? FirstTargetMs, long? LastTargetMs,
    long? TargetEndMs, long BeyondDurationCount);
public sealed record CombinePreview(IReadOnlyList<CombineSegmentStatistics> Segments, long TotalCount,
    long BeyondDurationCount, bool DurationUnknown);
public sealed record MediaSelection(string Kind, string? Id = null, long? ExpectedPlanVersion = null);
