namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Hard import limits and bounded-buffer defaults. Kept in one place so boundary tests can
/// reference the exact production values.
/// </summary>
public static class DanmukuImportLimits
{
    /// <summary>Maximum accepted file size in bytes (50 MiB).</summary>
    public const long MaxFileBytes = 52_428_800;

    /// <summary>Maximum number of source entries per file, including entries that will be skipped.</summary>
    public const int MaxSourceEntries = 300_000;

    /// <summary>Maximum number of Unicode code points per comment text.</summary>
    public const int MaxTextCodepoints = 100;

    /// <summary>Default display mode used when the mode field is missing.</summary>
    public const int DefaultMode = 1;

    /// <summary>Default font size used when the font size field is missing.</summary>
    public const int DefaultFontSize = 25;

    /// <summary>Maximum color value accepted by the schema.</summary>
    public const long MaxColor = 16_777_215;

    /// <summary>Default color (white) used when the color field is missing.</summary>
    public const long DefaultColor = MaxColor;

    /// <summary>Number of normal entries emitted per callback batch.</summary>
    public const int CommentBatchSize = 1_000;

    /// <summary>Number of abnormal entry details emitted per callback batch.</summary>
    public const int ErrorBatchSize = 1_000;

    /// <summary>Starting input buffer size in bytes (64 KiB).</summary>
    public const int InitialBufferSize = 65_536;

    /// <summary>Maximum number of code points kept in an error detail text summary.</summary>
    public const int TextSummaryMaxCodepoints = 200;

    /// <summary>Maximum bytes buffered for one JSON array element before it is streamed away.</summary>
    public const int MaxJsonElementBytes = 4 * 1024 * 1024;

    /// <summary>Maximum JSON nesting depth, matching the framework JSON reader default.</summary>
    public const int MaxJsonNestingDepth = 64;
}

/// <summary>
/// Effective parser limits. Production uses <see cref="Default"/>; boundary tests inject
/// smaller values, for example a 5-entry or 100-byte limit.
/// </summary>
public sealed record DanmukuParserLimits
{
    /// <summary>Gets the production limits.</summary>
    public static DanmukuParserLimits Default { get; } = new();

    public long MaxFileBytes { get; init; } = DanmukuImportLimits.MaxFileBytes;

    public int MaxSourceEntries { get; init; } = DanmukuImportLimits.MaxSourceEntries;

    public int MaxTextCodepoints { get; init; } = DanmukuImportLimits.MaxTextCodepoints;

    public int CommentBatchSize { get; init; } = DanmukuImportLimits.CommentBatchSize;

    public int ErrorBatchSize { get; init; } = DanmukuImportLimits.ErrorBatchSize;

    public int InitialBufferSize { get; init; } = DanmukuImportLimits.InitialBufferSize;

    public int TextSummaryMaxCodepoints { get; init; } = DanmukuImportLimits.TextSummaryMaxCodepoints;

    public int MaxJsonElementBytes { get; init; } = DanmukuImportLimits.MaxJsonElementBytes;

    /// <summary>Validates that every limit is usable by the parser.</summary>
    public void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxFileBytes, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(MaxFileBytes, long.MaxValue - 2);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSourceEntries, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxTextCodepoints, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(CommentBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(ErrorBatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(InitialBufferSize, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(TextSummaryMaxCodepoints, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxJsonElementBytes, 1);
    }
}
