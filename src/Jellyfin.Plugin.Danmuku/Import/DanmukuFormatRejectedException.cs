namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Raised when a whole import file is rejected while parsing.
/// </summary>
internal sealed class DanmukuFormatRejectedException : Exception
{
    public DanmukuFormatRejectedException(
        Model.ParseRejectReason reason,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Reason = reason;
    }

    public Model.ParseRejectReason Reason { get; }
}

/// <summary>
/// Raised when the input stream provides more bytes than the file size limit allows.
/// </summary>
internal sealed class StagingByteLimitExceededException : Exception
{
    public StagingByteLimitExceededException(long maxBytes)
        : base($"The import file exceeds the {maxBytes}-byte limit.")
    {
        MaxBytes = maxBytes;
    }

    public long MaxBytes { get; }
}
