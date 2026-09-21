using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Entry point for streaming import parsing. Reads the source once, writes the staged copy and
/// the SHA-256 hash while feeding the format parser, and emits normal comments and abnormal
/// entry details in bounded batches.
///
/// The emitted batches are provisional: when the outcome is rejected (structure failure, file or
/// entry limit, zero valid entries) the caller must discard everything it received.
/// </summary>
public sealed class DanmukuFileParser
{
    private readonly DanmukuParserLimits _limits;

    /// <summary>Initializes a parser with the production limits.</summary>
    public DanmukuFileParser()
        : this(DanmukuParserLimits.Default)
    {
    }

    /// <summary>Initializes a parser with explicit limits (used by boundary tests).</summary>
    public DanmukuFileParser(DanmukuParserLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        limits.Validate();
        _limits = limits;
    }

    /// <summary>Parses a source stream without an abnormal-entry detail callback.</summary>
    public Task<ParseOutcome> ParseAsync(
        Stream source,
        string stagedFilePath,
        Func<IReadOnlyList<CommentRecord>, CancellationToken, Task> onComments,
        CancellationToken cancellationToken = default) =>
        ParseAsync(source, stagedFilePath, onComments, onErrors: null, cancellationToken);

    /// <summary>
    /// Parses a source stream, writes the staged copy and reports the outcome.
    /// </summary>
    /// <param name="source">The input stream; ownership stays with the caller and it is not disposed.</param>
    /// <param name="stagedFilePath">Destination of the staged copy, which is created or truncated.</param>
    /// <param name="onComments">Receives normal comments in batches of at most the configured batch size.</param>
    /// <param name="onErrors">Receives abnormal entry details in bounded batches; may be null.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<ParseOutcome> ParseAsync(
        Stream source,
        string stagedFilePath,
        Func<IReadOnlyList<CommentRecord>, CancellationToken, Task> onComments,
        Func<IReadOnlyList<ImportErrorRecord>, CancellationToken, Task>? onErrors,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedFilePath);
        ArgumentNullException.ThrowIfNull(onComments);

        var fullStagedPath = Path.GetFullPath(stagedFilePath);
        var stagedDirectory = Path.GetDirectoryName(fullStagedPath);
        if (!string.IsNullOrEmpty(stagedDirectory))
        {
            Directory.CreateDirectory(stagedDirectory);
        }

        var accumulator = new ParseAccumulator(_limits, onComments, onErrors);
        DanmukuFormat? format = null;
        var staging = new FileStream(fullStagedPath, FileMode.Create, FileAccess.Write, FileShare.None);
        using var tee = new StagingTeeStream(source, staging, _limits.MaxFileBytes, _limits.InitialBufferSize);

        try
        {
            tee.PrimeReadAhead();
            format = DetectFormat(tee.ReadAhead);
            if (format is null)
            {
                return ParseOutcome.Reject(
                    ParseRejectReason.StructureInvalid,
                    tee.SourceCompleted
                        ? "The content is neither supported XML nor supported JSON."
                        : "The content does not start with '<' or '[' and cannot be recognized.",
                    null,
                    accumulator.ToStatistics(fullStagedPath, tee.GetHashHex()));
            }

            try
            {
                if (format == DanmukuFormat.Xml)
                {
                    await XmlDanmukuFormatParser.ParseAsync(tee, accumulator, _limits, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await JsonDanmukuFormatParser.ParseAsync(tee, accumulator, _limits, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (DanmukuFormatRejectedException rejection)
            {
                // The staged copy stays in place for the caller to clean up; the input was not
                // truncated by the parser, it simply stopped reading.
                return ParseOutcome.Reject(
                    rejection.Reason,
                    rejection.Message,
                    format,
                    accumulator.ToStatistics(fullStagedPath, tee.GetHashHex()));
            }

            tee.DrainToEnd();

            if (accumulator.NormalEntries == 0)
            {
                return ParseOutcome.Reject(
                    ParseRejectReason.ZeroValidEntries,
                    $"The file contains {accumulator.TotalEntries} source entries but no normal entries.",
                    format,
                    accumulator.ToStatistics(fullStagedPath, tee.GetHashHex()));
            }

            await accumulator.CompleteAsync(cancellationToken).ConfigureAwait(false);
            return ParseOutcome.Complete(format.Value, accumulator.ToStatistics(fullStagedPath, tee.GetHashHex()));
        }
        catch (StagingByteLimitExceededException)
        {
            return ParseOutcome.Reject(
                ParseRejectReason.FileTooLarge,
                $"The file is larger than the {_limits.MaxFileBytes}-byte limit.",
                format,
                accumulator.ToStatistics(fullStagedPath, null));
        }
    }

    private static DanmukuFormat? DetectFormat(ReadOnlySpan<byte> prefix)
    {
        var index = 0;
        if (prefix.Length >= 3 && prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
        {
            index = 3;
        }
        else if (prefix.Length >= 2
            && ((prefix[0] == 0xFF && prefix[1] == 0xFE) || (prefix[0] == 0xFE && prefix[1] == 0xFF)))
        {
            // A UTF-16 byte order mark can only be the XML variant; JSON parsing is UTF-8 only.
            return DanmukuFormat.Xml;
        }

        for (; index < prefix.Length; index++)
        {
            var value = prefix[index];
            if (value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
            {
                continue;
            }

            return value switch
            {
                (byte)'<' => DanmukuFormat.Xml,
                (byte)'[' => DanmukuFormat.Json,
                _ => null
            };
        }

        return null;
    }
}
