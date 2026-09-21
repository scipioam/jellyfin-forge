namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Frame of one top-level JSON array element. A non-oversized frame refers to the framer's
/// current element buffer; an oversized frame has no retained bytes.
/// </summary>
internal readonly record struct JsonElementFrame(bool Oversized, int Length)
{
    public static JsonElementFrame OversizedFrame { get; } = new(true, 0);
}

/// <summary>
/// Bounded framer for a top-level JSON array. It locates each element with a bracket/string
/// state machine so element bytes can be validated by the framework reader. Elements larger
/// than the buffer cap are streamed away instead of misreported as broken structure.
/// </summary>
internal sealed class JsonStreamFramer
{
    private readonly StagingTeeStream _stream;
    private readonly int _maxElementBytes;
    private readonly int _maxDepth;
    private byte[] _buffer;
    private int _length;
    private int _pos;
    private int _elementLength;
    private bool _eof;
    private bool _firstElement = true;
    private bool _arrayFinished;

    public JsonStreamFramer(StagingTeeStream stream, DanmukuParserLimits limits)
    {
        _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        _maxElementBytes = limits.MaxJsonElementBytes;
        _maxDepth = DanmukuImportLimits.MaxJsonNestingDepth;
        _buffer = new byte[Math.Max(1, Math.Min(limits.InitialBufferSize, _maxElementBytes))];
    }

    /// <summary>Gets the current non-oversized element bytes.</summary>
    public ReadOnlySpan<byte> CurrentElement => _buffer.AsSpan(0, _elementLength);

    /// <summary>Reads the document start: optional UTF-8 BOM, whitespace, then the opening bracket.</summary>
    public void ReadDocumentStart()
    {
        // Tolerate streams that return short reads: try to fill the BOM window before sniffing.
        while (_length < 3 && FillGrowing())
        {
        }

        if (_length == 0)
        {
            throw Structure("The JSON document is empty.");
        }

        if (_length >= 3 && _buffer[0] == 0xEF && _buffer[1] == 0xBB && _buffer[2] == 0xBF)
        {
            _pos = 3;
        }

        if (!SkipWhitespace())
        {
            throw Structure("The JSON document is empty.");
        }

        if (_buffer[_pos] != (byte)'[')
        {
            throw Structure("The top-level JSON value must be an array.");
        }

        _pos++;
        Compact();
    }

    /// <summary>
    /// Frames the next element. Returns false when the array closes; the caller then reads the
    /// document end. Every element must be a JSON object.
    /// </summary>
    public bool TryReadElement(out JsonElementFrame frame)
    {
        frame = default;
        if (_arrayFinished)
        {
            throw new InvalidOperationException("The JSON array was already fully read.");
        }

        if (!SkipWhitespace())
        {
            throw Structure("The JSON input ended before the array was closed.");
        }

        if (_buffer[_pos] == (byte)']')
        {
            _pos++;
            _arrayFinished = true;
            return false;
        }

        if (_firstElement)
        {
            _firstElement = false;
        }
        else
        {
            if (_buffer[_pos] != (byte)',')
            {
                throw Structure("Expected ',' or ']' between JSON array elements.");
            }

            _pos++;
            if (!SkipWhitespace())
            {
                throw Structure("The JSON input ended after a trailing comma.");
            }

            if (_buffer[_pos] == (byte)']')
            {
                throw Structure("A trailing comma is not a valid JSON array separator.");
            }
        }

        if (_buffer[_pos] != (byte)'{')
        {
            throw Structure("Every top-level JSON array element must be an object.");
        }

        // The element is framed from buffer offset zero so the span stays valid while parsing.
        Compact();
        frame = FrameElement();
        return true;
    }

    /// <summary>Drops a consumed element and keeps any bytes belonging to the following document.</summary>
    public void AdvanceAfterElement(JsonElementFrame frame)
    {
        if (frame.Oversized)
        {
            // The oversized element's prefix was already discarded; the buffer starts after it.
            return;
        }

        Compact();
    }

    /// <summary>Requires that only whitespace follows the closed array.</summary>
    public void ReadDocumentEnd()
    {
        if (SkipWhitespace())
        {
            throw Structure("Unexpected content after the top-level JSON array.");
        }
    }

    private JsonElementFrame FrameElement()
    {
        var state = new ElementScanState(_maxDepth);
        while (true)
        {
            while (_pos < _length)
            {
                var result = state.Consume(_buffer[_pos]);
                if (result == ElementScanResult.Error)
                {
                    throw Structure("The JSON structure is not valid inside a top-level array element.");
                }

                _pos++;
                if (result == ElementScanResult.Done)
                {
                    _elementLength = _pos;
                    return new JsonElementFrame(false, _pos);
                }
            }

            if (!FillGrowing())
            {
                if (_eof)
                {
                    throw Structure("The JSON input ended inside a top-level array element.");
                }

                return FinishOversizedElement(state);
            }
        }
    }

    private JsonElementFrame FinishOversizedElement(ElementScanState state)
    {
        // The element cannot fit into the bounded buffer: drop its prefix and keep scanning for
        // its end while retaining only the bytes that follow it.
        var scratch = new byte[Math.Max(1, Math.Min(_buffer.Length, 8192))];
        _length = 0;
        _pos = 0;
        _elementLength = 0;

        while (true)
        {
            var read = _stream.Read(scratch, 0, scratch.Length);
            if (read <= 0)
            {
                _eof = true;
                throw Structure("The JSON input ended inside an oversized array element.");
            }

            for (var index = 0; index < read; index++)
            {
                var result = state.Consume(scratch[index]);
                if (result == ElementScanResult.Error)
                {
                    throw Structure("The JSON structure is not valid inside an oversized array element.");
                }

                if (result == ElementScanResult.Done)
                {
                    var leftover = read - index - 1;
                    if (leftover > _buffer.Length)
                    {
                        Array.Resize(ref _buffer, leftover);
                    }

                    Buffer.BlockCopy(scratch, index + 1, _buffer, 0, leftover);
                    _length = leftover;
                    _pos = 0;
                    return JsonElementFrame.OversizedFrame;
                }
            }
        }
    }

    private bool SkipWhitespace()
    {
        while (true)
        {
            while (_pos < _length)
            {
                if (IsWhitespace(_buffer[_pos]))
                {
                    _pos++;
                    continue;
                }

                return true;
            }

            if (!FillCompacting())
            {
                return false;
            }
        }
    }

    private static bool IsWhitespace(byte value) =>
        value is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r';

    private void Compact()
    {
        if (_pos == 0)
        {
            return;
        }

        Buffer.BlockCopy(_buffer, _pos, _buffer, 0, _length - _pos);
        _length -= _pos;
        _pos = 0;
    }

    private bool FillCompacting()
    {
        Compact();
        return FillGrowing();
    }

    private bool FillGrowing()
    {
        if (_eof)
        {
            return false;
        }

        if (_length == _buffer.Length)
        {
            if (_buffer.Length >= _maxElementBytes)
            {
                return false;
            }

            Array.Resize(ref _buffer, (int)Math.Min((long)_buffer.Length * 2, _maxElementBytes));
        }

        var read = _stream.Read(_buffer, _length, _buffer.Length - _length);
        if (read <= 0)
        {
            _eof = true;
            return false;
        }

        _length += read;
        return true;
    }

    private static DanmukuFormatRejectedException Structure(string message) =>
        new(Model.ParseRejectReason.StructureInvalid, message);

    private enum ElementScanResult
    {
        Continue,
        Done,
        Error
    }

    /// <summary>
    /// Bracket and string state machine used to find the end of one top-level value.
    /// </summary>
    private sealed class ElementScanState
    {
        private readonly byte[] _closers;
        private readonly JsonSyntaxScanner _syntax;
        private int _depth;
        private bool _inString;
        private bool _escaped;

        public ElementScanState(int maxDepth)
        {
            _closers = new byte[maxDepth];
            _syntax = new JsonSyntaxScanner(maxDepth);
        }

        public ElementScanResult Consume(byte value)
        {
            if (!_syntax.Consume(value)) return ElementScanResult.Error;

            if (_inString)
            {
                if (_escaped)
                {
                    _escaped = false;
                    return ElementScanResult.Continue;
                }

                if (value == (byte)'\\')
                {
                    _escaped = true;
                    return ElementScanResult.Continue;
                }

                if (value == (byte)'"')
                {
                    _inString = false;
                    return ElementScanResult.Continue;
                }

                return value < 0x20 ? ElementScanResult.Error : ElementScanResult.Continue;
            }

            switch (value)
            {
                case (byte)'"':
                    _inString = true;
                    return ElementScanResult.Continue;

                case (byte)'{':
                    return Push((byte)'}');

                case (byte)'[':
                    return Push((byte)']');

                case (byte)'}':
                case (byte)']':
                    if (_depth == 0 || _closers[_depth - 1] != value)
                    {
                        return ElementScanResult.Error;
                    }

                    _depth--;
                    return _depth == 0 ? ElementScanResult.Done : ElementScanResult.Continue;

                default:
                    return ElementScanResult.Continue;
            }
        }

        private ElementScanResult Push(byte closer)
        {
            if (_depth >= _closers.Length)
            {
                return ElementScanResult.Error;
            }

            _closers[_depth++] = closer;
            return ElementScanResult.Continue;
        }
    }
}
