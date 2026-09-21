using System.Text.Json.Serialization;

namespace Jellyfin.Plugin.Danmuku.Import;

public sealed record ImportBatchRequest(string BatchId, string MediaId, long ExpectedVersion,
    IReadOnlyList<string> FileNames, string Operation = "append", [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReplaceFileId = null);

public sealed record ImportSlotSnapshot(int Slot, string Status, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TaskId, string OriginalFileName,
    long ReceivedBytes, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? TotalBytes, long UploadDeadlineAtUtcMs, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? FinishedAtUtcMs, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ErrorCode,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? TaskStatus, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Stage, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] double? StagePercent, long NormalComments, long AbnormalComments,
    long ImportedComments, long SkippedComments, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? DeadlineAtUtcMs, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ResultCode = null, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? FileId = null);

public sealed record ImportBatchSnapshot(string BatchId, string MediaId, string Operation, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? ReplaceFileId,
    long ExpectedVersion, string Status, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? FinishedAtUtcMs, IReadOnlyList<ImportSlotSnapshot> Slots);

public static class ImportLifecycleLimits
{
    public const int BatchFiles = 10;
    public const int OpenSlots = 100;
    public const long TemporaryBytes = 4L * 1024 * 1024 * 1024;
    public static readonly TimeSpan UploadWait = TimeSpan.FromHours(1);
    public static readonly TimeSpan Receive = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan ReceiveIdle = TimeSpan.FromSeconds(120);
    public static readonly TimeSpan Confirmation = TimeSpan.FromHours(24);
    public static readonly TimeSpan Retention = TimeSpan.FromDays(7);
}
