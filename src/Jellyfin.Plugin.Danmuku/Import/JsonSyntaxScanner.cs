namespace Jellyfin.Plugin.Danmuku.Import;

/// <summary>
/// Incremental JSON grammar validation, including discarded oversized elements. Retains only
/// the nesting stack and lexical state, so long strings and numbers need no token buffer.
/// </summary>
internal sealed class JsonSyntaxScanner(int maxDepth)
{
    private enum Context { ObjectKeyOrEnd, ObjectKey, Colon, Value, ObjectCommaOrEnd, ArrayValueOrEnd, ArrayValue, ArrayCommaOrEnd }
    private enum Token { None, String, Escape, Unicode, Number, Literal }
    private readonly Context[] _stack = new Context[maxDepth];
    private int _depth;
    private Token _token;
    private bool _key;
    private bool _started;
    private int _digits;
    private int _numberState;
    private string _literal = "";
    private int _literalPosition;

    public bool Consume(byte value)
    {
        if (_token == Token.String)
        {
            if (value < 0x20) return false;
            if (value == '\\') _token = Token.Escape;
            else if (value == '"')
            {
                _token = Token.None;
                if (_key) _stack[_depth - 1] = Context.Colon;
            }

            return true;
        }

        if (_token == Token.Escape)
        {
            if (value == 'u') { _digits = 0; _token = Token.Unicode; return true; }
            if (value is not ((byte)'"' or (byte)'\\' or (byte)'/' or (byte)'b' or (byte)'f' or (byte)'n' or (byte)'r' or (byte)'t')) return false;
            _token = Token.String;
            return true;
        }

        if (_token == Token.Unicode)
        {
            if (!(value is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F')) return false;
            if (++_digits == 4) _token = Token.String;
            return true;
        }

        if (_token == Token.Literal)
        {
            if (value != _literal[_literalPosition++]) return false;
            if (_literalPosition == _literal.Length) _token = Token.None;
            return true;
        }

        if (_token == Token.Number)
        {
            if (NumberByte(value)) return true;
            if (_numberState is not (2 or 3 or 5 or 8)) return false;
            _token = Token.None;
            // The delimiter belongs to the surrounding container.
        }

        if (value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') return true;
        if (_depth == 0)
        {
            if (_started || value != '{') return false;
            _started = true;
            return Push(Context.ObjectKeyOrEnd);
        }

        switch (_stack[_depth - 1])
        {
            case Context.ObjectKeyOrEnd:
                if (value == '}') { _depth--; return true; }
                goto case Context.ObjectKey;
            case Context.ObjectKey:
                if (value != '"') return false;
                _key = true;
                _token = Token.String;
                return true;
            case Context.Colon:
                if (value != ':') return false;
                _stack[_depth - 1] = Context.Value;
                return true;
            case Context.ObjectCommaOrEnd:
                if (value == '}') { _depth--; return true; }
                if (value != ',') return false;
                _stack[_depth - 1] = Context.ObjectKey;
                return true;
            case Context.ArrayCommaOrEnd:
                if (value == ']') { _depth--; return true; }
                if (value != ',') return false;
                _stack[_depth - 1] = Context.ArrayValue;
                return true;
            case Context.ArrayValueOrEnd:
                if (value == ']') { _depth--; return true; }
                goto case Context.ArrayValue;
            case Context.ArrayValue:
            case Context.Value:
                _stack[_depth - 1] = _stack[_depth - 1] == Context.Value ? Context.ObjectCommaOrEnd : Context.ArrayCommaOrEnd;
                if (value == '{') return Push(Context.ObjectKeyOrEnd);
                if (value == '[') return Push(Context.ArrayValueOrEnd);
                if (value == '"') { _key = false; _token = Token.String; return true; }
                if (value is (byte)'t' or (byte)'f' or (byte)'n')
                {
                    _literal = value == 't' ? "true" : value == 'f' ? "false" : "null";
                    _literalPosition = 1;
                    _token = Token.Literal;
                    return true;
                }

                _token = Token.Number;
                _numberState = 0;
                return NumberByte(value);
            default:
                return false;
        }
    }

    private bool Push(Context context)
    {
        if (_depth == _stack.Length) return false;
        _stack[_depth++] = context;
        return true;
    }

    private bool NumberByte(byte value)
    {
        var digit = value is >= (byte)'0' and <= (byte)'9';
        var nonzero = value is >= (byte)'1' and <= (byte)'9';
        var next = _numberState switch
        {
            0 when value == '-' => 1,
            0 or 1 when value == '0' => 2,
            0 or 1 when nonzero => 3,
            3 when digit => 3,
            2 or 3 when value == '.' => 4,
            4 or 5 when digit => 5,
            2 or 3 or 5 when value is (byte)'e' or (byte)'E' => 6,
            6 when value is (byte)'+' or (byte)'-' => 7,
            6 or 7 or 8 when digit => 8,
            _ => -1
        };
        if (next < 0) return false;
        _numberState = next;
        return true;
    }
}
