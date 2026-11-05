// Port of sigs.k8s.io/yaml v1.4.0 goyaml.v2/yamlh.go, yamlprivateh.go and the parser half of apic.go.
// Only what the decoding path needs is kept (no emitter, no document/node API, UTF-8 input only).

namespace JsonnetSharp.Yaml;

internal enum YamlEncoding
{
    Any,
    Utf8,
}

internal enum YamlErrorType
{
    None,
    Memory,
    Reader,
    Scanner,
    Parser,
    Composer,
}

internal struct YamlMark
{
    public int Index;
    public int Line;
    public int Column;
}

internal enum YamlScalarStyle
{
    Any,
    Plain,
    SingleQuoted,
    DoubleQuoted,
    Literal,
    Folded,
}

internal enum YamlTokenType
{
    NoToken,
    StreamStart,
    StreamEnd,
    VersionDirective,
    TagDirective,
    DocumentStart,
    DocumentEnd,
    BlockSequenceStart,
    BlockMappingStart,
    BlockEnd,
    FlowSequenceStart,
    FlowSequenceEnd,
    FlowMappingStart,
    FlowMappingEnd,
    BlockEntry,
    FlowEntry,
    Key,
    Value,
    Alias,
    Anchor,
    Tag,
    Scalar,
}

internal sealed class YamlToken
{
    public YamlTokenType Type;
    public YamlMark StartMark;
    public YamlMark EndMark;

    // The alias/anchor/scalar value, the tag handle or the %TAG directive handle.
    public byte[]? Value;

    // The tag suffix.
    public byte[]? Suffix;

    // The %TAG directive prefix.
    public byte[]? Prefix;

    public YamlScalarStyle Style;
    public int Major;
    public int Minor;
}

internal enum YamlEventType
{
    NoEvent,
    StreamStart,
    StreamEnd,
    DocumentStart,
    DocumentEnd,
    Alias,
    Scalar,
    SequenceStart,
    SequenceEnd,
    MappingStart,
    MappingEnd,
}

internal sealed class YamlEvent
{
    public static readonly YamlEvent Empty = new();

    public YamlEventType Type;
    public YamlMark StartMark;
    public YamlMark EndMark;
    public byte[]? Anchor;
    public byte[]? Tag;
    public byte[]? Value;

    // Is the tag optional for the plain style (scalars) / is the start implicit (collections, documents)?
    public bool Implicit;

    // Is the tag optional for any non-plain style?
    public bool QuotedImplicit;

    public YamlScalarStyle Style;

    public static string TypeString(YamlEventType t) => t switch
    {
        YamlEventType.NoEvent => "none",
        YamlEventType.StreamStart => "stream start",
        YamlEventType.StreamEnd => "stream end",
        YamlEventType.DocumentStart => "document start",
        YamlEventType.DocumentEnd => "document end",
        YamlEventType.Alias => "alias",
        YamlEventType.Scalar => "scalar",
        YamlEventType.SequenceStart => "sequence start",
        YamlEventType.SequenceEnd => "sequence end",
        YamlEventType.MappingStart => "mapping start",
        YamlEventType.MappingEnd => "mapping end",
        _ => "unknown event " + (int)t,
    };
}

internal static class YamlTags
{
    public const string Null = "tag:yaml.org,2002:null";
    public const string Bool = "tag:yaml.org,2002:bool";
    public const string Str = "tag:yaml.org,2002:str";
    public const string Int = "tag:yaml.org,2002:int";
    public const string Float = "tag:yaml.org,2002:float";
    public const string Timestamp = "tag:yaml.org,2002:timestamp";
    public const string Seq = "tag:yaml.org,2002:seq";
    public const string Map = "tag:yaml.org,2002:map";
    public const string Binary = "tag:yaml.org,2002:binary";
    public const string Merge = "tag:yaml.org,2002:merge";
}

internal sealed class YamlSimpleKey
{
    public bool Possible;
    public bool Required;
    public int TokenNumber;
    public YamlMark Mark;
}

internal enum YamlParserState
{
    StreamStart,
    ImplicitDocumentStart,
    DocumentStart,
    DocumentContent,
    DocumentEnd,
    BlockNode,
    BlockNodeOrIndentlessSequence,
    FlowNode,
    BlockSequenceFirstEntry,
    BlockSequenceEntry,
    IndentlessSequenceEntry,
    BlockMappingFirstKey,
    BlockMappingKey,
    BlockMappingValue,
    FlowSequenceFirstEntry,
    FlowSequenceEntry,
    FlowSequenceEntryMappingKey,
    FlowSequenceEntryMappingValue,
    FlowSequenceEntryMappingEnd,
    FlowMappingFirstKey,
    FlowMappingKey,
    FlowMappingValue,
    FlowMappingEmptyValue,
    End,
}

internal readonly struct YamlTagDirective
{
    public readonly byte[] Handle;
    public readonly byte[] Prefix;

    public YamlTagDirective(byte[] handle, byte[] prefix)
    {
        Handle = handle;
        Prefix = prefix;
    }
}

// A growable byte string standing in for the Go []byte values the scanner appends to.
internal sealed class YamlBytes
{
    private byte[] data = new byte[32];

    public int Length { get; private set; }

    public byte this[int i] => data[i];

    public void Add(byte b)
    {
        if (Length == data.Length)
        {
            Array.Resize(ref data, data.Length * 2);
        }
        data[Length++] = b;
    }

    public void AddRange(byte[] src, int offset, int count)
    {
        if (Length + count > data.Length)
        {
            Array.Resize(ref data, Math.Max(data.Length * 2, Length + count));
        }
        Buffer.BlockCopy(src, offset, data, Length, count);
        Length += count;
    }

    public void AddRange(YamlBytes other) => AddRange(other.data, 0, other.Length);

    public void Clear() => Length = 0;

    public byte[] ToArray()
    {
        byte[] r = new byte[Length];
        Buffer.BlockCopy(data, 0, r, 0, Length);
        return r;
    }
}

// The libyaml parser object (yaml_parser_t); the reader, scanner and parser live in partial files.
internal sealed partial class YamlParser
{
    // The size of the input raw buffer.
    private const int InputRawBufferSize = 512;

    // The size of the input buffer. It should be possible to decode the whole raw buffer.
    private const int InputBufferSize = InputRawBufferSize * 3;

    public YamlErrorType Error;
    public string Problem = "";
    public int ProblemOffset;
    public int ProblemValue;
    public YamlMark ProblemMark;
    public string Context = "";
    public YamlMark ContextMark;

    private readonly byte[] input;
    private int inputPos;
    private bool eof;

    // The working buffer holds decoded UTF-8; bufferLen is the Go slice length.
    private readonly byte[] buffer = new byte[InputBufferSize + 8];
    private int bufferLen;
    private int bufferPos;
    private int unread;

    private readonly byte[] rawBuffer = new byte[InputRawBufferSize];
    private int rawBufferLen;
    private int rawBufferPos;

    private YamlEncoding encoding;
    private int offset;
    private YamlMark mark;

    private bool streamStartProduced;
    private bool streamEndProduced;
    private int flowLevel;

    private readonly List<YamlToken> tokens = new();
    private int tokensHead;
    private int tokensParsed;
    private bool tokenAvailable;

    private int indent;
    private readonly List<int> indents = new();

    private bool simpleKeyAllowed;
    private readonly List<YamlSimpleKey> simpleKeys = new();
    private Dictionary<int, int> simpleKeysByTok = new();

    private YamlParserState state;
    private readonly List<YamlParserState> states = new();
    private readonly List<YamlMark> marks = new();
    private readonly List<YamlTagDirective> tagDirectives = new();

    // yaml_parser_initialize + yaml_parser_set_input_string.
    public YamlParser(byte[] input)
    {
        this.input = input;
    }

    private void InsertToken(int pos, YamlToken token)
    {
        // Drop consumed tokens now and then; positions are relative to the head so this is invisible.
        if (tokensHead > 64 && tokensHead * 2 > tokens.Count)
        {
            tokens.RemoveRange(0, tokensHead);
            tokensHead = 0;
        }
        if (pos < 0)
        {
            tokens.Add(token);
            return;
        }
        tokens.Insert(tokensHead + pos, token);
    }

    // String read handler.
    private int StringReadHandler(byte[] buf, int off, int count, out bool atEof)
    {
        if (inputPos == input.Length)
        {
            atEof = true;
            return 0;
        }
        int n = Math.Min(count, input.Length - inputPos);
        Buffer.BlockCopy(input, inputPos, buf, off, n);
        inputPos += n;
        atEof = false;
        return n;
    }

    private static T Pop<T>(List<T> list)
    {
        T v = list[list.Count - 1];
        list.RemoveAt(list.Count - 1);
        return v;
    }

    // yamlprivateh.go character classes.

    private static bool IsAlpha(byte[] b, int i) =>
        b[i] >= '0' && b[i] <= '9' || b[i] >= 'A' && b[i] <= 'Z' || b[i] >= 'a' && b[i] <= 'z' || b[i] == '_' || b[i] == '-';

    private static bool IsDigit(byte[] b, int i) => b[i] >= '0' && b[i] <= '9';

    private static int AsDigit(byte[] b, int i) => b[i] - '0';

    private static bool IsHex(byte[] b, int i) =>
        b[i] >= '0' && b[i] <= '9' || b[i] >= 'A' && b[i] <= 'F' || b[i] >= 'a' && b[i] <= 'f';

    private static int AsHex(byte[] b, int i)
    {
        byte bi = b[i];
        if (bi >= 'A' && bi <= 'F')
        {
            return bi - 'A' + 10;
        }
        if (bi >= 'a' && bi <= 'f')
        {
            return bi - 'a' + 10;
        }
        return bi - '0';
    }

    private static bool IsZ(byte[] b, int i) => b[i] == 0x00;

    // Like libyaml, this looks at the start of the buffer, not at i.
    private static bool IsBom(byte[] b) => b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF;

    private static bool IsSpace(byte[] b, int i) => b[i] == ' ';

    private static bool IsTab(byte[] b, int i) => b[i] == '\t';

    private static bool IsBlank(byte[] b, int i) => b[i] == ' ' || b[i] == '\t';

    private static bool IsBreak(byte[] b, int i) =>
        b[i] == '\r' || b[i] == '\n' ||
        b[i] == 0xC2 && b[i + 1] == 0x85 ||
        b[i] == 0xE2 && b[i + 1] == 0x80 && b[i + 2] == 0xA8 ||
        b[i] == 0xE2 && b[i + 1] == 0x80 && b[i + 2] == 0xA9;

    private static bool IsCrlf(byte[] b, int i) => b[i] == '\r' && b[i + 1] == '\n';

    private static bool IsBreakZ(byte[] b, int i) => IsBreak(b, i) || b[i] == 0;

    private static bool IsSpaceZ(byte[] b, int i) => b[i] == ' ' || IsBreakZ(b, i);

    private static bool IsBlankZ(byte[] b, int i) => b[i] == ' ' || b[i] == '\t' || IsBreakZ(b, i);

    private static int Width(byte b)
    {
        if ((b & 0x80) == 0x00)
        {
            return 1;
        }
        if ((b & 0xE0) == 0xC0)
        {
            return 2;
        }
        if ((b & 0xF0) == 0xE0)
        {
            return 3;
        }
        if ((b & 0xF8) == 0xF0)
        {
            return 4;
        }
        return 0;
    }
}
