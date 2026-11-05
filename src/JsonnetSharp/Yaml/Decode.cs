// Port of sigs.k8s.io/yaml v1.4.0 goyaml.v2/decode.go and the Unmarshal/failf parts of goyaml.v2/yaml.go,
// specialised to decoding into an interface{} (the only target sigs.k8s.io/yaml uses on this path).
//
// Decoded values mirror the Go dynamic types: null, bool, long (Go int), ulong (Go uint64), double, string,
// List<object?> ([]interface{}) and YamlMap (map[interface{}]interface{}).

using System.Globalization;
using System.Text;

namespace JsonnetSharp.Yaml;

// A failure raised by goyaml (fail/failf); Message is the Go error text.
internal sealed class YamlException : Exception
{
    public YamlException(string message)
        : base(message)
    {
    }
}

internal enum YamlNodeKind
{
    Document,
    Mapping,
    Sequence,
    Scalar,
    Alias,
}

internal sealed class YamlNode
{
    public YamlNodeKind Kind;
    public int Line;
    public int Column;
    public string Tag = "";

    // For an alias node, Alias holds the resolved alias.
    public YamlNode? Alias;
    public string Value = "";
    public bool Implicit;
    public readonly List<YamlNode> Children = new();
    public Dictionary<string, YamlNode>? Anchors;
}

// map[interface{}]interface{}: insertion ordered, keys compared like Go interface values.
internal sealed class YamlMap
{
    // Stands in for the nil interface key, which a .NET dictionary cannot hold.
    internal static readonly object NullKey = new();

    public readonly Dictionary<object, object?> Entries = new(GoKeyComparer.Instance);

    // Go's mapassign also stores the new key for float keys (needkeyupdate), so -0.0 replaced by 0.0 prints as 0.
    public void Set(object key, object? value)
    {
        if (key is double)
        {
            Entries.Remove(key);
        }
        Entries[key] = value;
    }

    private sealed class GoKeyComparer : IEqualityComparer<object>
    {
        public static readonly GoKeyComparer Instance = new();

        // Boxed values of different types never match, like Go interface comparison; NaN never equals itself.
        public new bool Equals(object? x, object? y) =>
            x is double dx && y is double dy ? dx == dy : object.Equals(x, y);

        public int GetHashCode(object obj) => obj is double d && d == 0 ? 0 : obj.GetHashCode();
    }
}

// The goyaml parser wrapper that turns the libyaml event stream into a node tree.
internal sealed class YamlNodeParser
{
    private readonly YamlParser parser;
    private YamlEvent ev = YamlEvent.Empty;
    private YamlNode? doc;
    private bool doneInit;

    public YamlNodeParser(byte[] b)
    {
        if (b.Length == 0)
        {
            b = new[] { (byte)'\n' };
        }
        parser = new YamlParser(b);
    }

    private void Init()
    {
        if (doneInit)
        {
            return;
        }
        Expect(YamlEventType.StreamStart);
        doneInit = true;
    }

    // Consumes an event from the event stream and checks that it's of the expected type.
    private void Expect(YamlEventType e)
    {
        if (ev.Type == YamlEventType.NoEvent)
        {
            if (!parser.Parse(out ev))
            {
                Fail();
            }
        }
        if (ev.Type == YamlEventType.StreamEnd)
        {
            throw YamlDecoder.Failf("attempted to go past the end of stream; corrupted value?");
        }
        if (ev.Type != e)
        {
            parser.Problem = "expected " + YamlEvent.TypeString(e) + " event but got " + YamlEvent.TypeString(ev.Type);
            Fail();
        }
        ev = YamlEvent.Empty;
    }

    // Peeks at the next event in the event stream and returns the event type.
    private YamlEventType Peek()
    {
        if (ev.Type != YamlEventType.NoEvent)
        {
            return ev.Type;
        }
        if (!parser.Parse(out ev))
        {
            Fail();
        }
        return ev.Type;
    }

    private void Fail()
    {
        int line = 0;
        if (parser.ProblemMark.Line != 0)
        {
            line = parser.ProblemMark.Line;
            // Scanner errors don't iterate line before returning error
            if (parser.Error == YamlErrorType.Scanner)
            {
                line++;
            }
        }
        else if (parser.ContextMark.Line != 0)
        {
            line = parser.ContextMark.Line;
        }
        string where = line != 0 ? "line " + line.ToString(CultureInfo.InvariantCulture) + ": " : "";
        string msg = parser.Problem.Length > 0 ? parser.Problem : "unknown problem parsing YAML content";
        throw YamlDecoder.Failf(where + msg);
    }

    private void Anchor(YamlNode n, byte[]? anchor)
    {
        if (anchor != null)
        {
            doc!.Anchors![GoString.FromBytes(anchor)] = n;
        }
    }

    public YamlNode? Parse()
    {
        Init();
        switch (Peek())
        {
            case YamlEventType.Scalar:
                return Scalar();
            case YamlEventType.Alias:
                return Alias();
            case YamlEventType.MappingStart:
                return Mapping();
            case YamlEventType.SequenceStart:
                return Sequence();
            case YamlEventType.DocumentStart:
                return Document();
            case YamlEventType.StreamEnd:
                // Happens when attempting to decode an empty buffer.
                return null;
            default:
                throw new InvalidOperationException("attempted to parse unknown event: " + YamlEvent.TypeString(ev.Type));
        }
    }

    private YamlNode Node(YamlNodeKind kind) => new()
    {
        Kind = kind,
        Line = ev.StartMark.Line,
        Column = ev.StartMark.Column,
    };

    private YamlNode Document()
    {
        YamlNode n = Node(YamlNodeKind.Document);
        n.Anchors = new Dictionary<string, YamlNode>();
        doc = n;
        Expect(YamlEventType.DocumentStart);
        n.Children.Add(Parse()!);
        Expect(YamlEventType.DocumentEnd);
        return n;
    }

    private YamlNode Alias()
    {
        YamlNode n = Node(YamlNodeKind.Alias);
        n.Value = GoString.FromBytes(ev.Anchor);
        doc!.Anchors!.TryGetValue(n.Value, out n.Alias);
        if (n.Alias == null)
        {
            throw YamlDecoder.Failf("unknown anchor '" + n.Value + "' referenced");
        }
        Expect(YamlEventType.Alias);
        return n;
    }

    private YamlNode Scalar()
    {
        YamlNode n = Node(YamlNodeKind.Scalar);
        n.Value = GoString.FromBytes(ev.Value);
        n.Tag = GoString.FromBytes(ev.Tag);
        n.Implicit = ev.Implicit;
        Anchor(n, ev.Anchor);
        Expect(YamlEventType.Scalar);
        return n;
    }

    private YamlNode Sequence()
    {
        YamlNode n = Node(YamlNodeKind.Sequence);
        Anchor(n, ev.Anchor);
        Expect(YamlEventType.SequenceStart);
        while (Peek() != YamlEventType.SequenceEnd)
        {
            YamlDecoder.EnsureStack();
            n.Children.Add(Parse()!);
        }
        Expect(YamlEventType.SequenceEnd);
        return n;
    }

    private YamlNode Mapping()
    {
        YamlNode n = Node(YamlNodeKind.Mapping);
        Anchor(n, ev.Anchor);
        Expect(YamlEventType.MappingStart);
        while (Peek() != YamlEventType.MappingEnd)
        {
            YamlDecoder.EnsureStack();
            n.Children.Add(Parse()!);
            n.Children.Add(Parse()!);
        }
        Expect(YamlEventType.MappingEnd);
        return n;
    }
}

// The goyaml decoder, unmarshalling a node tree into Go-like dynamic values.
internal sealed class YamlDecoder
{
    // 400,000 decode operations is ~500kb of dense object declarations, or
    // ~5kb of dense object declarations with 10000% alias expansion
    private const int AliasRatioRangeLow = 400000;

    // 4,000,000 decode operations is ~5MB of dense object declarations, or
    // ~4.5MB of dense object declarations with 10% alias expansion
    private const int AliasRatioRangeHigh = 4000000;

    // alias_ratio_range is the range over which we scale allowed alias ratios
    private const double AliasRatioRange = AliasRatioRangeHigh - AliasRatioRangeLow;

    private readonly HashSet<YamlNode> aliases = new();
    private int decodeCount;
    private int aliasCount;
    private int aliasDepth;

    internal static YamlException Failf(string message) => new("yaml: " + message);

    // Go grows its stacks; .NET threads have a fixed one, so very deep documents fail cleanly instead of crashing.
    internal static void EnsureStack()
    {
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.EnsureSufficientExecutionStack();
        }
        catch (InsufficientExecutionStackException)
        {
            throw Failf("exceeded max depth");
        }
    }

    // yaml.Unmarshal(in, &v) with v an interface{}: decodes the first document only.
    public static object? Unmarshal(byte[] input)
    {
        var p = new YamlNodeParser(input);
        YamlNode? node = p.Parse();
        if (node == null)
        {
            return null;
        }
        return new YamlDecoder().Unmarshal(node);
    }

    private static double AllowedAliasRatio(int decodeCount)
    {
        if (decodeCount <= AliasRatioRangeLow)
        {
            // allow 99% to come from alias expansion for small-to-medium documents
            return 0.99;
        }
        if (decodeCount >= AliasRatioRangeHigh)
        {
            // allow 10% to come from alias expansion for very large documents
            return 0.10;
        }
        // scale smoothly from 99% down to 10% over the range.
        // this maps to 396,000 - 400,000 allowed alias-driven decodes over the range.
        // 400,000 decode operations is ~100MB of allocations in worst-case scenarios (single-item maps).
        return 0.99 - 0.89 * ((decodeCount - AliasRatioRangeLow) / AliasRatioRange);
    }

    private void Count()
    {
        decodeCount++;
        if (aliasDepth > 0)
        {
            aliasCount++;
        }
        if (aliasCount > 100 && decodeCount > 1000 && (double)aliasCount / decodeCount > AllowedAliasRatio(decodeCount))
        {
            throw Failf("document contains excessive aliasing");
        }
    }

    // d.unmarshal(n, out) with out a fresh interface{}.
    private object? Unmarshal(YamlNode n)
    {
        EnsureStack();
        Count();
        switch (n.Kind)
        {
            case YamlNodeKind.Document:
                return n.Children.Count == 1 ? Unmarshal(n.Children[0]) : null;
            case YamlNodeKind.Alias:
            {
                object? result = null;
                Alias(n, () => result = Unmarshal(n.Alias!));
                return result;
            }
            case YamlNodeKind.Scalar:
                return Scalar(n);
            case YamlNodeKind.Mapping:
            {
                var m = new YamlMap();
                Mapping(n, m);
                return m;
            }
            default:
                return Sequence(n);
        }
    }

    // d.unmarshal(n, out) with out an existing map, which only merge keys reach (and only with mapping nodes).
    private void UnmarshalInto(YamlNode n, YamlMap m)
    {
        EnsureStack();
        Count();
        if (n.Kind == YamlNodeKind.Alias)
        {
            Alias(n, () => UnmarshalInto(n.Alias!, m));
            return;
        }
        Mapping(n, m);
    }

    private void Alias(YamlNode n, Action unmarshal)
    {
        if (aliases.Contains(n))
        {
            // TODO this could actually be allowed in some circumstances.
            throw Failf("anchor '" + n.Value + "' value contains itself");
        }
        aliases.Add(n);
        aliasDepth++;
        unmarshal();
        aliasDepth--;
        aliases.Remove(n);
    }

    private static object? Scalar(YamlNode n)
    {
        string tag;
        object? resolved;
        if (n.Tag.Length == 0 && !n.Implicit)
        {
            tag = YamlTags.Str;
            resolved = n.Value;
        }
        else
        {
            (tag, resolved) = YamlResolve.Resolve(n.Tag, n.Value);
            if (tag == YamlTags.Binary)
            {
                byte[]? data = DecodeBase64((string)resolved!);
                if (data == null)
                {
                    throw Failf("!!binary value contains invalid base64 data");
                }
                // Go keeps the raw bytes until json.Marshal; invalid ones are escaped until then (see GoString).
                resolved = GoString.FromBytes(data, escapeInvalid: true);
            }
        }
        if (resolved == null)
        {
            return null;
        }
        if (tag == YamlTags.Timestamp)
        {
            // It looks like a timestamp but for backward compatibility
            // reasons we set it as a string, so that code that unmarshals
            // timestamp-like values into interface{} will continue to
            // see a string and not a time.Time.
            return n.Value;
        }
        return resolved;
    }

    // base64.StdEncoding.DecodeString: padded, and ignoring only '\r' and '\n'.
    private static byte[]? DecodeBase64(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
        {
            if (c == '\r' || c == '\n')
            {
                continue;
            }
            bool ok = c >= 'A' && c <= 'Z' || c >= 'a' && c <= 'z' || c >= '0' && c <= '9' || c == '+' || c == '/' || c == '=';
            if (!ok)
            {
                return null;
            }
            sb.Append(c);
        }
        try
        {
            return Convert.FromBase64String(sb.ToString());
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private List<object?> Sequence(YamlNode n)
    {
        var list = new List<object?>(n.Children.Count);
        foreach (YamlNode child in n.Children)
        {
            list.Add(Unmarshal(child));
        }
        return list;
    }

    private void Mapping(YamlNode n, YamlMap m)
    {
        int l = n.Children.Count;
        for (int i = 0; i < l; i += 2)
        {
            if (IsMerge(n.Children[i]))
            {
                Merge(n.Children[i + 1], m);
                continue;
            }
            object? k = Unmarshal(n.Children[i]);
            if (k is YamlMap || k is List<object?>)
            {
                throw Failf("invalid map key: " + GoFormat.GoSyntax(k));
            }
            object? e = Unmarshal(n.Children[i + 1]);
            m.Set(k ?? YamlMap.NullKey, e);
        }
    }

    private static YamlException FailWantMap() => Failf("map merge requires map or sequence of maps as the value");

    private void Merge(YamlNode n, YamlMap m)
    {
        switch (n.Kind)
        {
            case YamlNodeKind.Mapping:
                UnmarshalInto(n, m);
                break;
            case YamlNodeKind.Alias:
                if (n.Alias != null && n.Alias.Kind != YamlNodeKind.Mapping)
                {
                    throw FailWantMap();
                }
                UnmarshalInto(n, m);
                break;
            case YamlNodeKind.Sequence:
                // Step backwards as earlier nodes take precedence.
                for (int i = n.Children.Count - 1; i >= 0; i--)
                {
                    YamlNode ni = n.Children[i];
                    if (ni.Kind == YamlNodeKind.Alias)
                    {
                        if (ni.Alias != null && ni.Alias.Kind != YamlNodeKind.Mapping)
                        {
                            throw FailWantMap();
                        }
                    }
                    else if (ni.Kind != YamlNodeKind.Mapping)
                    {
                        throw FailWantMap();
                    }
                    UnmarshalInto(ni, m);
                }
                break;
            default:
                throw FailWantMap();
        }
    }

    private static bool IsMerge(YamlNode n) =>
        n.Kind == YamlNodeKind.Scalar && n.Value == "<<" && (n.Implicit || n.Tag == YamlTags.Merge);
}

// Go string <-> .NET string conversions.
//
// A Go string may hold invalid UTF-8 (a !!binary scalar); go-yaml keeps those bytes and only json.Marshal
// replaces them. Until then such a byte b is kept as the lone surrogate U+DC00 + b (like Python's
// surrogateescape), which no decoded YAML text otherwise contains.
internal static class GoString
{
    private static bool IsEscape(string s, int i) =>
        s[i] >= 0xDC80 && s[i] <= 0xDCFF && (i == 0 || !char.IsHighSurrogate(s[i - 1]));

    private static bool HasEscape(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (IsEscape(s, i))
            {
                return true;
            }
        }
        return false;
    }

    // The escaped byte at s[i], or -1.
    public static int EscapedByte(string s, int i) => IsEscape(s, i) ? s[i] - 0xDC00 : -1;

    // What json.Marshal writes for the string: each invalid byte becomes U+FFFD.
    public static string Lossy(string s)
    {
        if (!HasEscape(s))
        {
            return s;
        }
        char[] c = s.ToCharArray();
        for (int i = 0; i < c.Length; i++)
        {
            if (IsEscape(s, i))
            {
                c[i] = (char)0xFFFD;
            }
        }
        return new string(c);
    }

    // Go string comparison: by bytes.
    public static int Compare(string a, string b)
    {
        if (!HasEscape(a) && !HasEscape(b))
        {
            return Util.CompareGoStrings(a, b);
        }
        byte[] x = ToBytes(a);
        byte[] y = ToBytes(b);
        int n = Math.Min(x.Length, y.Length);
        for (int i = 0; i < n; i++)
        {
            if (x[i] != y[i])
            {
                return x[i].CompareTo(y[i]);
            }
        }
        return x.Length.CompareTo(y.Length);
    }

    private static byte[] ToBytes(string s)
    {
        var bytes = new List<byte>(s.Length);
        int start = 0;
        for (int i = 0; i <= s.Length; i++)
        {
            if (i == s.Length || IsEscape(s, i))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(s.Substring(start, i - start)));
                if (i < s.Length)
                {
                    bytes.Add((byte)(s[i] - 0xDC00));
                }
                start = i + 1;
            }
        }
        return bytes.ToArray();
    }

    // string(b) followed by a range over it: each invalid UTF-8 byte becomes one U+FFFD, as encoding/json does,
    // or with escapeInvalid its escape (see above).
    public static string FromBytes(byte[]? b, bool escapeInvalid = false)
    {
        if (b == null || b.Length == 0)
        {
            return "";
        }
        var sb = new StringBuilder(b.Length);
        int i = 0;
        while (i < b.Length)
        {
            int c = b[i];
            if (c < 0x80)
            {
                sb.Append((char)c);
                i++;
                continue;
            }
            int n;
            int r;
            int min;
            if (c >= 0xC2 && c <= 0xDF)
            {
                n = 2;
                r = c & 0x1F;
                min = 0x80;
            }
            else if (c >= 0xE0 && c <= 0xEF)
            {
                n = 3;
                r = c & 0x0F;
                min = 0x800;
            }
            else if (c >= 0xF0 && c <= 0xF4)
            {
                n = 4;
                r = c & 0x07;
                min = 0x10000;
            }
            else
            {
                sb.Append(escapeInvalid ? (char)(0xDC00 | c) : (char)0xFFFD);
                i++;
                continue;
            }
            bool ok = i + n <= b.Length;
            for (int k = 1; ok && k < n; k++)
            {
                int cc = b[i + k];
                if ((cc & 0xC0) != 0x80)
                {
                    ok = false;
                    break;
                }
                r = (r << 6) | (cc & 0x3F);
            }
            if (!ok || r < min || r > 0x10FFFF || (r >= 0xD800 && r <= 0xDFFF))
            {
                sb.Append(escapeInvalid ? (char)(0xDC00 | c) : (char)0xFFFD);
                i++;
                continue;
            }
            if (r >= 0x10000)
            {
                sb.Append(char.ConvertFromUtf32(r));
            }
            else
            {
                sb.Append((char)r);
            }
            i += n;
        }
        return sb.ToString();
    }
}
