namespace Jellyfin.Plugin.Danmuku.Storage;

/// <summary>
/// Canonical status values persisted by the Danmuku schema.
/// </summary>
public static class StorageStatuses
{
    public static class Tasks
    {
        public const string Queued = "Queued";
        public const string Processing = "Processing";
        public const string AwaitingConfirmation = "AwaitingConfirmation";
        public const string AwaitingConflictResolution = "AwaitingConflictResolution";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";
        public const string Expired = "Expired";
        public const string Interrupted = "Interrupted";

        public static IReadOnlyList<string> Terminal { get; } =
            [Completed, Failed, Cancelled, Expired, Interrupted];

        public static bool IsTerminal(string status) => Terminal.Contains(status, StringComparer.Ordinal);
    }

    public static class Slots
    {
        public const string PendingUpload = "PendingUpload";
        public const string Receiving = "Receiving";
        public const string Accepted = "Accepted";
        public const string Completed = "Completed";
        public const string Failed = "Failed";
        public const string Cancelled = "Cancelled";
        public const string Expired = "Expired";
        public const string Interrupted = "Interrupted";

        public static IReadOnlyList<string> Terminal { get; } =
            [Completed, Failed, Cancelled, Expired, Interrupted];
    }

    public static class Batches
    {
        public const string Open = "Open";
        public const string Finished = "Finished";
    }

    public static class Files
    {
        public const string Published = "Published";
        public const string Deleting = "Deleting";
        public const string DeleteFailed = "DeleteFailed";
    }

    /// <summary>Error code persisted on tasks whose interrupted cleanup still needs a retry.</summary>
    public const string RecoveryCleanupFailedErrorCode = "RecoveryCleanupFailed";
}
