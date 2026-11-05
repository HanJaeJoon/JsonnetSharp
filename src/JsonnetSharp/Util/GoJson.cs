// The parts of Go's encoding/json that go-jsonnet's builtins use: json.Unmarshal into an interface{} plus
// jsonToValue (scanner.go, decode.go; adapted from the decoder of the sister project JqSharp) and the string
// escaping of a json.Encoder with SetEscapeHTML(false) (encode.go appendString).

using System.Globalization;
using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

internal sealed class GoJsonException : Exception
{
    public GoJsonException(string message)
        : base(message)
    {
    }
}

internal static class GoJson
{
    private const int MaxNestingDepth = 10000;

    private sealed class Frame
    {
        public List<CachedThunk>? Array;
        public Dictionary<string, Value>? Object;
        public string Key = "";
    }

    // json.Unmarshal(data, &v) with v an interface{}, followed by jsonToValue. Syntax errors win over
    // number range errors, as Go validates first. Builds the values directly and iteratively, so that
    // deeply nested input (Go allows 10000 levels) cannot exhaust the native stack.
    public static Value Unmarshal(string s)
    {
        int pos = 0;
        string? rangeError = null;
        List<Frame> stack = new();
        while (true)
        {
            // Beginning of a value.
            SkipSpace(s, ref pos);
            if (pos >= s.Length)
            {
                throw UnexpectedEnd();
            }
            char c = s[pos];
            Value v;
            if (c == '[' || c == '{')
            {
                if (stack.Count >= MaxNestingDepth)
                {
                    throw InvalidChar(s, pos, "exceeded max depth");
                }
                pos++;
                SkipSpace(s, ref pos);
                if (pos >= s.Length)
                {
                    throw UnexpectedEnd();
                }
                if (c == '[')
                {
                    if (s[pos] != ']')
                    {
                        stack.Add(new Frame { Array = new List<CachedThunk>() });
                        continue;
                    }
                    pos++;
                    v = ValueArray.Empty;
                }
                else
                {
                    if (s[pos] != '}')
                    {
                        Frame f = new() { Object = new Dictionary<string, Value>() };
                        f.Key = ParseKey(s, ref pos);
                        stack.Add(f);
                        continue;
                    }
                    pos++;
                    v = Interpreter.BuildObject(ObjectFieldHide.ObjectFieldInherit, new Dictionary<string, Value>());
                }
            }
            else
            {
                v = ParseScalar(s, ref pos, ref rangeError);
            }

            // A value is complete: store it in its container, closing containers as they end.
            while (true)
            {
                if (stack.Count == 0)
                {
                    SkipSpace(s, ref pos);
                    if (pos < s.Length)
                    {
                        throw InvalidChar(s, pos, "after top-level value");
                    }
                    if (rangeError != null)
                    {
                        throw new GoJsonException(rangeError);
                    }
                    return v;
                }
                Frame top = stack[stack.Count - 1];
                if (top.Array != null)
                {
                    top.Array.Add(CachedThunk.ReadyThunk(v));
                }
                else
                {
                    top.Object![top.Key] = v;
                }
                SkipSpace(s, ref pos);
                if (pos >= s.Length)
                {
                    throw UnexpectedEnd();
                }
                char d = s[pos];
                if (d == ',')
                {
                    pos++;
                    if (top.Object != null)
                    {
                        top.Key = ParseKey(s, ref pos);
                    }
                    break;
                }
                if (top.Array != null ? d == ']' : d == '}')
                {
                    pos++;
                    stack.RemoveAt(stack.Count - 1);
                    v = top.Array != null
                        ? new ValueArray(top.Array.ToArray())
                        : Interpreter.BuildObject(ObjectFieldHide.ObjectFieldInherit, top.Object!);
                    continue;
                }
                throw InvalidChar(s, pos, top.Array != null ? "after array element" : "after object key:value pair");
            }
        }
    }

    // An object key and the ':' after it.
    private static string ParseKey(string s, ref int pos)
    {
        SkipSpace(s, ref pos);
        if (pos >= s.Length)
        {
            throw UnexpectedEnd();
        }
        if (s[pos] != '"')
        {
            throw InvalidChar(s, pos, "looking for beginning of object key string");
        }
        string key = ParseString(s, ref pos);
        SkipSpace(s, ref pos);
        if (pos >= s.Length)
        {
            throw UnexpectedEnd();
        }
        if (s[pos] != ':')
        {
            throw InvalidChar(s, pos, "after object key");
        }
        pos++;
        return key;
    }

    private static Value ParseScalar(string s, ref int pos, ref string? rangeError)
    {
        char c = s[pos];
        switch (c)
        {
            case '"':
                return new ValueFlatString(ParseString(s, ref pos));
            case 't':
                ParseLiteral(s, ref pos, "true");
                return ValueBoolean.True;
            case 'f':
                ParseLiteral(s, ref pos, "false");
                return ValueBoolean.False;
            case 'n':
                ParseLiteral(s, ref pos, "null");
                return ValueNull.Instance;
            default:
                if (c == '-' || (c >= '0' && c <= '9'))
                {
                    string literal = ScanNumber(s, ref pos);
                    // strconv.ParseFloat; only overflow is an error.
                    if (!GoStrconv.TryParseFloat(literal, out double d) || double.IsInfinity(d))
                    {
                        rangeError ??= "json: cannot unmarshal number " + literal + " into Go value of type float64";
                        return ValueNull.Instance;
                    }
                    // .NET Framework parses "-0" as +0.
                    return new ValueNumber(d == 0 && literal[0] == '-' ? -0.0 : d);
                }
                throw InvalidChar(s, pos, "looking for beginning of value");
        }
    }

    private static void SkipSpace(string s, ref int pos)
    {
        while (pos < s.Length && (s[pos] == ' ' || s[pos] == '\t' || s[pos] == '\r' || s[pos] == '\n'))
        {
            pos++;
        }
    }

    private static void ParseLiteral(string s, ref int pos, string literal)
    {
        for (int k = 1; k < literal.Length; k++)
        {
            if (pos + k >= s.Length)
            {
                throw EndInToken("in literal " + literal + " (expecting '" + literal[k] + "')");
            }
            if (s[pos + k] != literal[k])
            {
                throw InvalidChar(s, pos + k, "in literal " + literal + " (expecting '" + literal[k] + "')");
            }
        }
        pos += literal.Length;
    }

    private static string ScanNumber(string s, ref int pos)
    {
        int start = pos;
        if (s[pos] == '-')
        {
            pos++;
            if (pos >= s.Length)
            {
                throw EndInToken("in numeric literal");
            }
            if (!IsDigit(s[pos]))
            {
                throw InvalidChar(s, pos, "in numeric literal");
            }
        }
        if (s[pos] == '0')
        {
            pos++;
        }
        else
        {
            while (pos < s.Length && IsDigit(s[pos]))
            {
                pos++;
            }
        }
        if (pos < s.Length && s[pos] == '.')
        {
            pos++;
            if (pos >= s.Length)
            {
                throw EndInToken("after decimal point in numeric literal");
            }
            if (!IsDigit(s[pos]))
            {
                throw InvalidChar(s, pos, "after decimal point in numeric literal");
            }
            while (pos < s.Length && IsDigit(s[pos]))
            {
                pos++;
            }
        }
        if (pos < s.Length && (s[pos] == 'e' || s[pos] == 'E'))
        {
            pos++;
            if (pos < s.Length && (s[pos] == '+' || s[pos] == '-'))
            {
                pos++;
            }
            if (pos >= s.Length)
            {
                throw EndInToken("in exponent of numeric literal");
            }
            if (!IsDigit(s[pos]))
            {
                throw InvalidChar(s, pos, "in exponent of numeric literal");
            }
            while (pos < s.Length && IsDigit(s[pos]))
            {
                pos++;
            }
        }
        return s.Substring(start, pos - start);
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static string ParseString(string s, ref int pos)
    {
        pos++;
        StringBuilder sb = new();
        int start = pos;
        while (true)
        {
            if (pos >= s.Length)
            {
                throw UnexpectedEnd();
            }
            char c = s[pos];
            if (c == '"')
            {
                sb.Append(s, start, pos - start);
                pos++;
                return sb.ToString();
            }
            if (c < 0x20)
            {
                throw InvalidChar(s, pos, "in string literal");
            }
            if (char.IsSurrogate(c))
            {
                if (char.IsHighSurrogate(c) && pos + 1 < s.Length && char.IsLowSurrogate(s[pos + 1]))
                {
                    pos += 2;
                    continue;
                }
                // A lone surrogate is U+FFFD in the Go string.
                sb.Append(s, start, pos - start).Append('\uFFFD');
                pos++;
                start = pos;
                continue;
            }
            if (c != '\\')
            {
                pos++;
                continue;
            }
            sb.Append(s, start, pos - start);
            pos++;
            if (pos >= s.Length)
            {
                throw EndInToken("in string escape code");
            }
            c = s[pos];
            switch (c)
            {
                case '"':
                case '\\':
                case '/':
                    sb.Append(c);
                    pos++;
                    break;
                case 'b':
                    sb.Append('\b');
                    pos++;
                    break;
                case 'f':
                    sb.Append('\f');
                    pos++;
                    break;
                case 'n':
                    sb.Append('\n');
                    pos++;
                    break;
                case 'r':
                    sb.Append('\r');
                    pos++;
                    break;
                case 't':
                    sb.Append('\t');
                    pos++;
                    break;
                case 'u':
                {
                    int r = ReadHex4(s, pos + 1);
                    pos += 5;
                    if (r >= 0xD800 && r < 0xE000)
                    {
                        // A valid surrogate pair is consumed whole; anything else is U+FFFD.
                        if (r < 0xDC00 && pos + 5 < s.Length && s[pos] == '\\' && s[pos + 1] == 'u' &&
                            TryHex4(s, pos + 2, out int r2) && r2 >= 0xDC00 && r2 < 0xE000)
                        {
                            sb.Append((char)r).Append((char)r2);
                            pos += 6;
                        }
                        else
                        {
                            sb.Append('\uFFFD');
                        }
                    }
                    else
                    {
                        sb.Append((char)r);
                    }
                    break;
                }
                default:
                    throw InvalidChar(s, pos, "in string escape code");
            }
            start = pos;
        }
    }

    private static int ReadHex4(string s, int pos)
    {
        int r = 0;
        for (int k = 0; k < 4; k++)
        {
            if (pos + k >= s.Length)
            {
                throw EndInToken("in \\u hexadecimal character escape");
            }
            int d = HexVal(s[pos + k]);
            if (d < 0)
            {
                throw InvalidChar(s, pos + k, "in \\u hexadecimal character escape");
            }
            r = (r << 4) | d;
        }
        return r;
    }

    private static bool TryHex4(string s, int pos, out int r)
    {
        r = 0;
        for (int k = 0; k < 4; k++)
        {
            int d = pos + k < s.Length ? HexVal(s[pos + k]) : -1;
            if (d < 0)
            {
                return false;
            }
            r = (r << 4) | d;
        }
        return true;
    }

    private static int HexVal(char c) =>
        c >= '0' && c <= '9' ? c - '0'
        : c >= 'a' && c <= 'f' ? c - 'a' + 10
        : c >= 'A' && c <= 'F' ? c - 'A' + 10
        : -1;

    private static GoJsonException UnexpectedEnd() => new("unexpected end of JSON input");

    // At the end of input Go's scanner steps with a space, so an unfinished token reports ' '.
    private static GoJsonException EndInToken(string context) => new("invalid character ' ' " + context);

    private static GoJsonException InvalidChar(string s, int pos, string context) =>
        new("invalid character " + QuoteChar(s, pos) + " " + context);

    // Go reports the first byte of the UTF-8 encoding, quoted like strconv.Quote of that byte as a rune.
    private static string QuoteChar(string s, int pos)
    {
        char c = s[pos];
        if (c == '\'')
        {
            return "'\\''";
        }
        if (c == '"')
        {
            return "'\"'";
        }
        int b = c < 0x80 ? c : Encoding.UTF8.GetBytes(s.Substring(pos, char.IsHighSurrogate(c) && pos + 1 < s.Length ? 2 : 1))[0];
        string q = b switch
        {
            7 => "\\a",
            8 => "\\b",
            9 => "\\t",
            10 => "\\n",
            11 => "\\v",
            12 => "\\f",
            13 => "\\r",
            '\\' => "\\\\",
            < 0x20 or 0x7f => "\\x" + b.ToString("x2", CultureInfo.InvariantCulture),
            (>= 0x80 and <= 0xa0) or 0xad => "\\u00" + b.ToString("x2", CultureInfo.InvariantCulture),
            _ => ((char)b).ToString(),
        };
        return "'" + q + "'";
    }

    // The string encoding of a json.Encoder with SetEscapeHTML(false), as jsonEncode uses it.
    public static string EncodeString(string s)
    {
        const string Hex = "0123456789abcdef";
        StringBuilder sb = new(s.Length + 2);
        sb.Append('"');
        for (int k = 0; k < s.Length; k++)
        {
            if ((k & 0xFFFF) == 0)
            {
                Util.CheckLength(sb);
            }
            char c = s[k];
            switch (c)
            {
                case '\\':
                case '"':
                    sb.Append('\\').Append(c);
                    break;
                case '\b':
                    sb.Append("\\b");
                    break;
                case '\f':
                    sb.Append("\\f");
                    break;
                case '\n':
                    sb.Append("\\n");
                    break;
                case '\r':
                    sb.Append("\\r");
                    break;
                case '\t':
                    sb.Append("\\t");
                    break;
                case '\u2028':
                case '\u2029':
                    sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    break;
                default:
                    if (c < 0x20)
                    {
                        sb.Append("\\u00").Append(Hex[c >> 4]).Append(Hex[c & 0xF]);
                    }
                    else if (char.IsHighSurrogate(c) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]))
                    {
                        sb.Append(c).Append(s[k + 1]);
                        k++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // The Go string holds U+FFFD (valid UTF-8) for a lone surrogate.
                        sb.Append('\uFFFD');
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    break;
            }
        }
        sb.Append('"');
        return Util.ToStringChecked(sb);
    }
}
