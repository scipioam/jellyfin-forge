using System.Security.Cryptography;

namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Single-pass read-through stream. Every byte returned to the format parser is also written
/// to the staging file and folded into the SHA-256 hash. The byte limit is enforced while
/// reading, so an oversized input is aborted instead of growing further.
/// </summary>
internal sealed class StagingTeeStream : Stream
{
    private readonly Stream _source;
    private readonly FileStream _staging;
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly long _maxBytes;
    private readonly byte[] _readAhead;
    private int _readAheadLength;
    private int _readAheadOffset;
    private long _totalRead;
    private bool _sourceCompleted;
    private string? _hashHex;

    public StagingTeeStream(Stream source, FileStream staging, long maxBytes, int readAheadSize)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _staging = staging ?? throw new ArgumentNullException(nameof(staging));
        _maxBytes = maxBytes;
        _readAhead = new byte[Math.Max(1, readAheadSize)];
    }

    /// <summary>Gets the number of source bytes processed so far.</summary>
    public long TotalBytesRead => _totalRead;

    /// <summary>Gets a value indicating whether the source stream reached its end.</summary>
    public bool SourceCompleted => _sourceCompleted;

    /// <summary>Gets the already processed prefix that is replayed to the parser during sniffing.</summary>
    public ReadOnlySpan<byte> ReadAhead => _readAhead.AsSpan(0, _readAheadLength);

    /// <summary>Reads the sniffing prefix without consuming it logically.</summary>
    public void PrimeReadAhead()
    {
        while (_readAheadLength < _readAhead.Length && !_sourceCompleted)
        {
            var read = ReadFromSource(_readAhead.AsSpan(_readAheadLength, _readAhead.Length - _readAheadLength));
            if (read == 0)
            {
                _sourceCompleted = true;
                break;
            }

            _readAheadLength += read;
        }
    }

    /// <summary>Reads the remaining source bytes to detect trailing content and complete the hash.</summary>
    public void DrainToEnd()
    {
        var scratch = new byte[16 * 1024];
        while (Read(scratch, 0, scratch.Length) > 0)
        {
        }
    }

    /// <summary>Gets the lowercase SHA-256 of the whole input, or null when the input was not read to the end.</summary>
    public string? GetHashHex()
    {
        if (!_sourceCompleted)
        {
            return null;
        }

        if (_hashHex is null)
        {
            _hashHex = Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
        }

        return _hashHex;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (destination.Length == 0)
        {
            return 0;
        }

        if (_readAheadOffset < _readAheadLength)
        {
            var copied = Math.Min(destination.Length, _readAheadLength - _readAheadOffset);
            _readAhead.AsSpan(_readAheadOffset, copied).CopyTo(destination);
            _readAheadOffset += copied;
            return copied;
        }

        if (_sourceCompleted)
        {
            return 0;
        }

        var read = ReadFromSource(destination);
        if (read == 0)
        {
            _sourceCompleted = true;
        }

        return read;
    }

    public override void Flush() => _staging.Flush();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _hash.Dispose();
            _staging.Dispose();
        }

        base.Dispose(disposing);
    }

    private int ReadFromSource(Span<byte> destination)
    {
        var remainingAllowed = _maxBytes - _totalRead;

        // Read one byte beyond the limit to distinguish "exactly at the limit" from "too large".
        var request = (int)Math.Min(destination.Length, remainingAllowed + 1);
        if (request <= 0)
        {
            throw new StagingByteLimitExceededException(_maxBytes);
        }

        var read = _source.Read(destination[..request]);
        if (read <= 0)
        {
            return 0;
        }

        var writable = (int)Math.Min(read, remainingAllowed);
        if (writable > 0)
        {
            _staging.Write(destination[..writable]);
            _hash.AppendData(destination[..writable]);
            _totalRead += writable;
        }

        if (read > writable)
        {
            throw new StagingByteLimitExceededException(_maxBytes);
        }

        return writable;
    }
}
