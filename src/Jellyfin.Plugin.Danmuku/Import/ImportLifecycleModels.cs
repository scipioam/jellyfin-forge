namespace Jellyfin.Plugin.Danmuku.Import;

public sealed record ImportBatchRequest(string BatchId, string MediaId, long ExpectedVersion,
    IReadOnlyList<string> FileNames, string Operation = "append", string? ReplaceFileId = null);

public sealed record ImportSlotSnapshot(int Slot, string Status, string? TaskId, string OriginalFileName,
    long ReceivedBytes, long? TotalBytes, long UploadDeadlineAtUtcMs, long? FinishedAtUtcMs, string? ErrorCode,
    string? TaskStatus, string? Stage, double? StagePercent, long NormalComments, long AbnormalComments,
    long ImportedComments, long SkippedComments, long? DeadlineAtUtcMs, string? ResultCode = null, string? FileId = null);

public sealed record ImportBatchSnapshot(string BatchId, string MediaId, string Operation, string? ReplaceFileId,
    long ExpectedVersion, string Status, long? FinishedAtUtcMs, IReadOnlyList<ImportSlotSnapshot> Slots);

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
