using Jellyfin.Plugin.Danmuku.Model;
using Jellyfin.Plugin.Danmuku.Storage;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Collects parse statistics and emits normal comments and abnormal entry details in bounded
/// batches. At no point does it hold all entries of a file in one collection.
/// </summary>
internal sealed class ParseAccumulator
{
    private readonly DanmukuParserLimits _limits;
    private readonly Func<IReadOnlyList<CommentRecord>, CancellationToken, Task> _onComments;
    private readonly Func<IReadOnlyList<ImportErrorRecord>, CancellationToken, Task>? _onErrors;
    private readonly List<CommentAbnormalReason> _reasons = new(4);
    private List<CommentRecord> _commentBatch = new();
    private List<ImportErrorRecord> _errorBatch = new();

    public ParseAccumulator(
        DanmukuParserLimits limits,
        Func<IReadOnlyList<CommentRecord>, CancellationToken, Task> onComments,
        Func<IReadOnlyList<ImportErrorRecord>, CancellationToken, Task>? onErrors)
    {
        _limits = limits;
        _onComments = onComments;
        _onErrors = onErrors;
    }

    public long TotalEntries { get; private set; }

    public long NormalEntries { get; private set; }

    public long AbnormalEntries { get; private set; }

    public long DefaultModeCount { get; private set; }

    public long DefaultFontSizeCount { get; private set; }

    public long DefaultColorCount { get; private set; }

    public long IdFallbackCount { get; private set; }

    public Dictionary<CommentAbnormalReason, long> ReasonCounts { get; } = new();

    public Dictionary<long, long> UnsupportedModeDistribution { get; } = new();

    public Dictionary<string, long> UnparsedOptionalFieldCounts { get; } = new();

    /// <summary>Gets a value indicating whether the current entry has at least one abnormal reason.</summary>
    public bool HasReasons => _reasons.Count > 0;

    /// <summary>
    /// Starts the next source entry and enforces the entry count limit, including entries that
    /// will later be skipped as abnormal or unsupported.
    /// </summary>
    public void BeginEntry()
    {
        TotalEntries++;
        if (TotalEntries > _limits.MaxSourceEntries)
        {
            throw new DanmukuFormatRejectedException(
                ParseRejectReason.EntryCountExceeded,
                $"The file contains more than {_limits.MaxSourceEntries} source entries.");
        }

        _reasons.Clear();
    }

    public void AddReason(CommentAbnormalReason reason)
    {
        if (!_reasons.Contains(reason))
        {
            _reasons.Add(reason);
        }
    }

    public void RecordDefaultMode() => DefaultModeCount++;

    public void RecordDefaultFontSize() => DefaultFontSizeCount++;

    public void RecordDefaultColor() => DefaultColorCount++;

    public void RecordIdFallback() => IdFallbackCount++;

    public void RecordUnsupportedMode(long mode) =>
        UnsupportedModeDistribution[mode] = UnsupportedModeDistribution.GetValueOrDefault(mode) + 1;

    public void RecordUnparsedOptionalField(string field) =>
        UnparsedOptionalFieldCounts[field] = UnparsedOptionalFieldCounts.GetValueOrDefault(field) + 1;

    /// <summary>Emits a normal entry, flushing the batch when it is full.</summary>
    public async Task EndNormalAsync(CommentRecord record, CancellationToken cancellationToken)
    {
        NormalEntries++;
        _commentBatch.Add(record);
        if (_commentBatch.Count >= _limits.CommentBatchSize)
        {
            await FlushCommentsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Records an abnormal entry and queues its bounded detail for the error store.</summary>
    public async Task EndAbnormalAsync(string? textSummary, CancellationToken cancellationToken)
    {
        if (_reasons.Count == 0)
        {
            throw new InvalidOperationException("An abnormal entry must carry at least one reason.");
        }

        AbnormalEntries++;
        foreach (var reason in _reasons)
        {
            ReasonCounts[reason] = ReasonCounts.GetValueOrDefault(reason) + 1;
        }

        var reasonCodes = string.Join(',', _reasons.OrderBy(static reason => reason).Select(static reason => reason.ToString()));
        _errorBatch.Add(new ImportErrorRecord(TotalEntries, reasonCodes, textSummary));
        if (_errorBatch.Count >= _limits.ErrorBatchSize)
        {
            await FlushErrorsAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Flushes the remaining partial batches after the whole file parsed successfully.</summary>
    public async Task CompleteAsync(CancellationToken cancellationToken)
    {
        await FlushCommentsAsync(cancellationToken).ConfigureAwait(false);
        await FlushErrorsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Creates an immutable copy of the current counters.</summary>
    public ParseStatistics ToStatistics(string? stagedPath, string? contentSha256) =>
        new(
            TotalEntries,
            NormalEntries,
            AbnormalEntries,
            new Dictionary<CommentAbnormalReason, long>(ReasonCounts),
            DefaultModeCount,
            DefaultFontSizeCount,
            DefaultColorCount,
            IdFallbackCount,
            new Dictionary<long, long>(UnsupportedModeDistribution),
            new Dictionary<string, long>(UnparsedOptionalFieldCounts),
            stagedPath,
            contentSha256);

    private Task FlushCommentsAsync(CancellationToken cancellationToken)
    {
        if (_commentBatch.Count == 0)
        {
            return Task.CompletedTask;
        }

        var batch = _commentBatch;
        _commentBatch = new List<CommentRecord>(_limits.CommentBatchSize);
        return _onComments(batch, cancellationToken);
    }

    private Task FlushErrorsAsync(CancellationToken cancellationToken)
    {
        if (_errorBatch.Count == 0)
        {
            return Task.CompletedTask;
        }

        var batch = _errorBatch;
        _errorBatch = new List<ImportErrorRecord>(_limits.ErrorBatchSize);

        // Without an error callback the details are dropped so the batch stays bounded; the
        // per-reason counters were already recorded.
        return _onErrors is null ? Task.CompletedTask : _onErrors(batch, cancellationToken);
    }
}
