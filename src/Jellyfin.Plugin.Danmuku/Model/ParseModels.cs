namespace Jellyfin.Plugin.Danmuku.Model;

/// <summary>
/// Structure of a danmuku import file, detected from the content instead of the extension.
/// </summary>
public enum DanmukuFormat
{
    /// <summary>Bilibili XML: an <c>i</c> root with <c>d</c> elements carrying a <c>p</c> attribute and text.</summary>
    Xml,

    /// <summary>A top-level JSON array of comment objects.</summary>
    Json
}

/// <summary>
/// Why an entire import file was rejected while parsing.
/// </summary>
public enum ParseRejectReason
{
    /// <summary>The content is neither supported XML nor supported JSON.</summary>
    StructureInvalid,

    /// <summary>The file is larger than the byte limit.</summary>
    FileTooLarge,

    /// <summary>
    /// The file contains more source entries than the entry limit, including entries that
    /// would be skipped as abnormal or unsupported.
    /// </summary>
    EntryCountExceeded,

    /// <summary>The file parsed but contains no normal entries.</summary>
    ZeroValidEntries
}

/// <summary>
/// Per-entry reason a source entry cannot be imported as a normal comment.
/// Values are persisted as reason-code names; append new values instead of reordering.
/// </summary>
public enum CommentAbnormalReason
{
    /// <summary>The necessary time field is absent.</summary>
    MissingTime,

    /// <summary>The time field is present but is not a finite, non-negative storable millisecond value.</summary>
    InvalidTime,

    /// <summary>The necessary text is absent, null or whitespace.</summary>
    BlankText,

    /// <summary>The text field is present with an unsupported type.</summary>
    InvalidText,

    /// <summary>The text exceeds the Unicode code point limit.</summary>
    TextTooLong,

    /// <summary>The display mode is present but is not a positive integer.</summary>
    InvalidMode,

    /// <summary>The display mode is a valid integer outside the supported set (1, 4, 5).</summary>
    UnsupportedMode,

    /// <summary>The font size is present but is not a positive integer.</summary>
    InvalidFontSize,

    /// <summary>The color is present but is not an integer in the supported range.</summary>
    InvalidColor,

    /// <summary>The source entry is larger than the parser's bounded element buffer.</summary>
    EntryTooLarge
}

/// <summary>
/// Parse-time statistics for one import file. Dictionaries are copies taken when the
/// parse completes and never change afterwards.
/// </summary>
public sealed record ParseStatistics(
    long TotalSourceEntries,
    long NormalEntries,
    long AbnormalEntries,
    IReadOnlyDictionary<CommentAbnormalReason, long> AbnormalByReason,
    long DefaultModeCount,
    long DefaultFontSizeCount,
    long DefaultColorCount,
    long IdFallbackCount,
    IReadOnlyDictionary<long, long> UnsupportedModeDistribution,
    IReadOnlyDictionary<string, long> UnparsedOptionalFieldCounts,
    string? StagedPath,
    string? ContentSha256)
{
    /// <summary>Gets statistics for input that produced no counters yet.</summary>
    public static ParseStatistics Empty { get; } = new(
        0,
        0,
        0,
        new Dictionary<CommentAbnormalReason, long>(),
        0,
        0,
        0,
        0,
        new Dictionary<long, long>(),
        new Dictionary<string, long>(),
        null,
        null);

    /// <summary>Gets the number of entries carrying an unsupported but valid display mode.</summary>
    public long UnsupportedModeCount => UnsupportedModeDistribution.Values.Sum();
}

/// <summary>
/// Result of parsing one import file. A rejected outcome never publishes entries; a
/// completed outcome with abnormal entries requires the administrator's skip confirmation.
/// </summary>
public sealed record ParseOutcome(
    bool Accepted,
    ParseRejectReason? RejectReason,
    string? RejectDetail,
    DanmukuFormat? Format,
    ParseStatistics Statistics)
{
    /// <summary>
    /// Gets a value indicating whether the parsed entries wait for the administrator to skip
    /// abnormal entries. The task state change itself belongs to the import task stage.
    /// </summary>
    public bool RequiresConfirmation => Accepted && Statistics.AbnormalEntries > 0;

    /// <summary>Creates a rejection outcome.</summary>
    public static ParseOutcome Reject(
        ParseRejectReason reason,
        string detail,
        DanmukuFormat? format,
        ParseStatistics statistics) =>
        new(false, reason, detail, format, statistics);

    /// <summary>Creates a completed outcome.</summary>
    public static ParseOutcome Complete(DanmukuFormat format, ParseStatistics statistics) =>
        new(true, null, null, format, statistics);
}
