using System.Globalization;
using JsonnetSharp.Yaml;

namespace JsonnetSharp.Tests;

// Ports of goyaml.v2 decode_test.go and limit_test.go cases that decode into interface{} (or whose result is the same
// as with an interface{} target). Values are dumped with their Go types: int(..), uint64(..), float(..).
public class YamlDecodeTests
{
    private static string Dump(object? v) => v switch
    {
        null => "nil",
        bool b => b ? "true" : "false",
        long l => "int(" + l.ToString(CultureInfo.InvariantCulture) + ")",
        ulong u => "uint64(" + u.ToString(CultureInfo.InvariantCulture) + ")",
        double d => "float(" + GoFormat.FormatFloatG(d, 64) + ")",
        // Invalid bytes kept from a !!binary scalar print as \xNN, as in the goyaml test data.
        string s => "\"" + string.Concat(s.Select((c, k) => GoString.EscapedByte(s, k) is int b and >= 0 ? "\\x" + b.ToString("x2", CultureInfo.InvariantCulture) : c.ToString())) + "\"",
        List<object?> list => "[" + string.Join(", ", list.Select(Dump)) + "]",
        YamlMap m => "{" + string.Join(", ", m.Entries
            .Select(kv => Dump(kv.Key == YamlMap.NullKey ? null : kv.Key) + ": " + Dump(kv.Value))
            .OrderBy(s => s, StringComparer.Ordinal)) + "}",
        _ => throw new InvalidOperationException(v.GetType().ToString()),
    };

    private static string Decode(string yaml) => Dump(YamlDecoder.Unmarshal(System.Text.Encoding.UTF8.GetBytes(yaml)));

    public static TheoryData<string, string> UnmarshalTests() => new()
    {
        { "", "nil" },
        { "{}", "{}" },
        { "v: hi", "{\"v\": \"hi\"}" },
        { "v: true", "{\"v\": true}" },
        { "v: 10", "{\"v\": int(10)}" },
        { "v: 0b10", "{\"v\": int(2)}" },
        { "v: 0xA", "{\"v\": int(10)}" },
        { "v: 4294967296", "{\"v\": int(4294967296)}" },
        { "v: 0.1", "{\"v\": float(0.1)}" },
        { "v: .1", "{\"v\": float(0.1)}" },
        { "v: .Inf", "{\"v\": float(+Inf)}" },
        { "v: -.Inf", "{\"v\": float(-Inf)}" },
        { "v: -10", "{\"v\": int(-10)}" },
        { "v: -.1", "{\"v\": float(-0.1)}" },
        { "123", "int(123)" },
        { "canonical: 6.8523e+5", "{\"canonical\": float(685230)}" },
        { "expo: 685.230_15e+03", "{\"expo\": float(685230.15)}" },
        { "fixed: 685_230.15", "{\"fixed\": float(685230.15)}" },
        { "neginf: -.inf", "{\"neginf\": float(-Inf)}" },
        { "canonical: y", "{\"canonical\": true}" },
        { "answer: NO", "{\"answer\": false}" },
        { "logical: True", "{\"logical\": true}" },
        { "option: on", "{\"option\": true}" },
        { "canonical: 685230", "{\"canonical\": int(685230)}" },
        { "decimal: +685_230", "{\"decimal\": int(685230)}" },
        { "octal: 02472256", "{\"octal\": int(685230)}" },
        { "hexa: 0x_0A_74_AE", "{\"hexa\": int(685230)}" },
        { "bin: 0b1010_0111_0100_1010_1110", "{\"bin\": int(685230)}" },
        { "bin: -0b101010", "{\"bin\": int(-42)}" },
        { "bin: -0b1000000000000000000000000000000000000000000000000000000000000000", "{\"bin\": int(-9223372036854775808)}" },
        { "empty:", "{\"empty\": nil}" },
        { "canonical: ~", "{\"canonical\": nil}" },
        { "english: null", "{\"english\": nil}" },
        { "~: null key", "{nil: \"null key\"}" },
        { "seq: [A,B]", "{\"seq\": [\"A\", \"B\"]}" },
        { "seq: [A,B,C,]", "{\"seq\": [\"A\", \"B\", \"C\"]}" },
        { "seq: [A,1,C]", "{\"seq\": [\"A\", int(1), \"C\"]}" },
        { "seq:\n - A\n - B", "{\"seq\": [\"A\", \"B\"]}" },
        { "seq:\n - A\n - 1\n - C", "{\"seq\": [\"A\", int(1), \"C\"]}" },
        { "scalar: | # Comment\n\n literal\n\n \ttext\n\n", "{\"scalar\": \"\nliteral\n\n\ttext\n\"}" },
        {
            "scalar: > # Comment\n\n folded\n line\n \n next\n line\n  * one\n  * two\n\n last\n line\n\n",
            "{\"scalar\": \"\nfolded line\nnext line\n * one\n * two\n\nlast line\n\"}"
        },
        { "a: {b: c}", "{\"a\": {\"b\": \"c\"}}" },
        { "hello: world", "{\"hello\": \"world\"}" },
        { "a:", "{\"a\": nil}" },
        { "a: 1.0", "{\"a\": float(1)}" },
        { "a: [1, 2]", "{\"a\": [int(1), int(2)]}" },
        { "a: y", "{\"a\": true}" },
        { "v: 42", "{\"v\": int(42)}" },
        { "v: -4294967296", "{\"v\": int(-4294967296)}" },
        { "int_max: 2147483647", "{\"int_max\": int(2147483647)}" },
        { "int_min: -2147483648", "{\"int_min\": int(-2147483648)}" },
        { "int64_max: 9223372036854775807", "{\"int64_max\": int(9223372036854775807)}" },
        {
            "int64_max_base2: 0b111111111111111111111111111111111111111111111111111111111111111",
            "{\"int64_max_base2\": int(9223372036854775807)}"
        },
        { "int64_min: -9223372036854775808", "{\"int64_min\": int(-9223372036854775808)}" },
        {
            "int64_neg_base2: -0b111111111111111111111111111111111111111111111111111111111111111",
            "{\"int64_neg_base2\": int(-9223372036854775807)}"
        },
        { "int64_overflow: 9223372036854775808", "{\"int64_overflow\": uint64(9223372036854775808)}" },
        { "uint_underflow: -1", "{\"uint_underflow\": int(-1)}" },
        { "uint64_max: 18446744073709551615", "{\"uint64_max\": uint64(18446744073709551615)}" },
        {
            "uint64_max_base2: 0b1111111111111111111111111111111111111111111111111111111111111111",
            "{\"uint64_max_base2\": uint64(18446744073709551615)}"
        },
        {
            "float32_max: 3.40282346638528859811704183484516925440e+38",
            "{\"float32_max\": float(3.4028234663852886e+38)}"
        },
        {
            "float32_nonzero: 1.401298464324817070923729583289916131280e-45",
            "{\"float32_nonzero\": float(1.401298464324817e-45)}"
        },
        { "float32_maxuint64+1: 18446744073709551616", "{\"float32_maxuint64+1\": float(1.8446744073709552e+19)}" },
        {
            "float64_max: 1.797693134862315708145274237317043567981e+308",
            "{\"float64_max\": float(1.7976931348623157e+308)}"
        },
        {
            "float64_nonzero: 4.940656458412465441765687928682213723651e-324",
            "{\"float64_nonzero\": float(5e-324)}"
        },
        { "v: 4294967297", "{\"v\": int(4294967297)}" },
        { "'1': '\"2\"'", "{\"1\": \"\"2\"\"}" },
        { "v:\n- A\n- 'B\n\n  C'\n", "{\"v\": [\"A\", \"B\nC\"]}" },
        { "v: !!float '1.1'", "{\"v\": float(1.1)}" },
        { "v: !!float 0", "{\"v\": float(0)}" },
        { "v: !!float -1", "{\"v\": float(-1)}" },
        { "v: !!null ''", "{\"v\": nil}" },
        { "%TAG !y! tag:yaml.org,2002:\n---\nv: !y!int '1'", "{\"v\": int(1)}" },
        { "v: ! test", "{\"v\": \"test\"}" },
        { "a: &x 1\nb: &y 2\nc: *x\nd: *y\n", "{\"a\": int(1), \"b\": int(2), \"c\": int(1), \"d\": int(2)}" },
        { "a: &a {c: 1}\nb: *a", "{\"a\": {\"c\": int(1)}, \"b\": {\"c\": int(1)}}" },
        { "a: &a [1, 2]\nb: *a", "{\"a\": [int(1), int(2)], \"b\": [int(1), int(2)]}" },
        { "foo: ''", "{\"foo\": \"\"}" },
        { "foo: null", "{\"foo\": nil}" },
        { "foo: ~", "{\"foo\": nil}" },
        { "a: 1\nb: 2\n", "{\"a\": int(1), \"b\": int(2)}" },
        {
            "%YAML 1.1\n--- !!str\n\"Generic line break (no glyph)\\n\\\n Generic line break (glyphed)\\n\\\n" +
            " Line separator\u2028\\\n Paragraph separator\u2029\"\n",
            "\"Generic line break (no glyph)\nGeneric line break (glyphed)\nLine separator\u2028Paragraph separator\u2029\""
        },
        { "a: -b_c", "{\"a\": \"-b_c\"}" },
        { "a: +b_c", "{\"a\": \"+b_c\"}" },
        { "a: 50cent_of_dollar", "{\"a\": \"50cent_of_dollar\"}" },
        { "a: {b: https://github.com/go-yaml/yaml}", "{\"a\": {\"b\": \"https://github.com/go-yaml/yaml\"}}" },
        { "a: [https://github.com/go-yaml/yaml]", "{\"a\": [\"https://github.com/go-yaml/yaml\"]}" },
        { "a: 3s", "{\"a\": \"3s\"}" },
        { "a: <foo>", "{\"a\": \"<foo>\"}" },
        { "a: 1:1\n", "{\"a\": \"1:1\"}" },
        { "a: !!binary gIGC\n", "{\"a\": \"\\x80\\x81\\x82\"}" },
        { "a: !!binary |\n  " + string.Concat(Enumerable.Repeat("kJCQ", 17)) + "kJ\n  CQ\n", "{\"a\": \"" + string.Concat(Enumerable.Repeat("\\x90", 54)) + "\"}" },
        { "a: !!binary |\n  " + new string('A', 70) + "\n  ==\n", "{\"a\": \"" + new string('\0', 52) + "\"}" },
        {
            "{b: 2, a: 1, d: 4, c: 3, sub: {e: 5}}",
            "{\"a\": int(1), \"b\": int(2), \"c\": int(3), \"d\": int(4), \"sub\": {\"e\": int(5)}}"
        },
        { "a:\n b:\n  c: d\n", "{\"a\": {\"b\": {\"c\": \"d\"}}}" },
        { "a: 1.2.3.4\n", "{\"a\": \"1.2.3.4\"}" },
        { "a: 2015-02-24T18:19:39Z\n", "{\"a\": \"2015-02-24T18:19:39Z\"}" },
        { "a: 2015-01-01\n", "{\"a\": \"2015-01-01\"}" },
        { "a: 2015-02-24T18:19:39.12Z\n", "{\"a\": \"2015-02-24T18:19:39.12Z\"}" },
        { "a: 2015-2-3T3:4:5Z", "{\"a\": \"2015-2-3T3:4:5Z\"}" },
        { "a: 2015-02-24t18:19:39Z\n", "{\"a\": \"2015-02-24t18:19:39Z\"}" },
        { "a: 2015-02-24 18:19:39\n", "{\"a\": \"2015-02-24 18:19:39\"}" },
        { "a: !!str 2015-01-01", "{\"a\": \"2015-01-01\"}" },
        { "a: !!timestamp \"2015-01-01\"", "{\"a\": \"2015-01-01\"}" },
        { "a: !!timestamp 2015-01-01", "{\"a\": \"2015-01-01\"}" },
        { "a: \"2015-01-01\"", "{\"a\": \"2015-01-01\"}" },
        { "a: []", "{\"a\": []}" },
        { "a: 123456e1\n", "{\"a\": float(1.23456e+06)}" },
        { "a: 123456E1\n", "{\"a\": float(1.23456e+06)}" },
        {
            "First occurrence: &anchor Foo\nSecond occurrence: *anchor\nOverride anchor: &anchor Bar\nReuse anchor: *anchor\n",
            "{\"First occurrence\": \"Foo\", \"Override anchor\": \"Bar\", \"Reuse anchor\": \"Bar\", \"Second occurrence\": \"Foo\"}"
        },
        { "---\nhello\n...\n}not yaml", "\"hello\"" },
        { "a: 5\n", "{\"a\": int(5)}" },
        { "a: 5.5\n", "{\"a\": float(5.5)}" },
        { "\na:\n  b\nb:\n  ? a\n  : a", "{\"a\": \"b\", \"b\": {\"a\": \"a\"}}" },
        { "notanum: .NaN", "{\"notanum\": float(NaN)}" },
    };

    [Theory]
    [MemberData(nameof(UnmarshalTests))]
    public void Unmarshal(string data, string expected)
    {
        Assert.Equal(expected, Decode(data));
    }

    public static TheoryData<string, string> UnmarshalErrorTests() => new()
    {
        { "v: !!float 'error'", "yaml: cannot decode !!str `error` as a !!float" },
        { "v: [A,", "yaml: line 1: did not find expected node content" },
        { "v:\n- [A,", "yaml: line 2: did not find expected node content" },
        { "a:\n- b: *,", "yaml: line 2: did not find expected alphabetic or numeric character" },
        { "a: *b\n", "yaml: unknown anchor 'b' referenced" },
        { "a: &a\n  b: *a\n", "yaml: anchor 'a' value contains itself" },
        { "a: &x null\n<<:\n- *x\nb: &x {}\n", "yaml: map merge requires map or sequence of maps as the value" },
        { "value: -", "yaml: block sequence entries are not allowed in this context" },
        { "a: !!binary ==", "yaml: !!binary value contains invalid base64 data" },
        { "{[.]}", "yaml: invalid map key: []interface {}{\".\"}" },
        { "{{.}}", "yaml: invalid map key: map[interface {}]interface {}{\".\":interface {}(nil)}" },
        { "b: *a\na: &a {c: 1}", "yaml: unknown anchor 'a' referenced" },
        { "%TAG !%79! tag:yaml.org,2002:\n---\nv: !%79!int '1'", "yaml: did not find expected whitespace" },
        { "a:\n  1:\nb\n  2:", "yaml: line 4: could not find expected ':'" },
        {
            "a: &a [00,00,00,00,00,00,00,00,00]\n" +
            "b: &b [*a,*a,*a,*a,*a,*a,*a,*a,*a]\n" +
            "c: &c [*b,*b,*b,*b,*b,*b,*b,*b,*b]\n" +
            "d: &d [*c,*c,*c,*c,*c,*c,*c,*c,*c]\n" +
            "e: &e [*d,*d,*d,*d,*d,*d,*d,*d,*d]\n" +
            "f: &f [*e,*e,*e,*e,*e,*e,*e,*e,*e]\n" +
            "g: &g [*f,*f,*f,*f,*f,*f,*f,*f,*f]\n" +
            "h: &h [*g,*g,*g,*g,*g,*g,*g,*g,*g]\n" +
            "i: &i [*h,*h,*h,*h,*h,*h,*h,*h,*h]\n",
            "yaml: document contains excessive aliasing"
        },
    };

    [Theory]
    [MemberData(nameof(UnmarshalErrorTests))]
    public void UnmarshalErrors(string data, string error)
    {
        var ex = Assert.Throws<YamlException>(() => Decode(data));
        Assert.Equal(error, ex.Message);
    }

    private const string MergeTests = @"
anchors:
  list:
    - &CENTER { ""x"": 1, ""y"": 2 }
    - &LEFT   { ""x"": 0, ""y"": 2 }
    - &BIG    { ""r"": 10 }
    - &SMALL  { ""r"": 1 }

# All the following maps are equal:

plain:
  # Explicit keys
  ""x"": 1
  ""y"": 2
  ""r"": 10
  label: center/big

mergeOne:
  # Merge one map
  << : *CENTER
  ""r"": 10
  label: center/big

mergeMultiple:
  # Merge multiple maps
  << : [ *CENTER, *BIG ]
  label: center/big

override:
  # Override
  << : [ *BIG, *LEFT, *SMALL ]
  ""x"": 1
  label: center/big

shortTag:
  # Explicit short merge tag
  !!merge ""<<"" : [ *CENTER, *BIG ]
  label: center/big

longTag:
  # Explicit merge long tag
  !<tag:yaml.org,2002:merge> ""<<"" : [ *CENTER, *BIG ]
  label: center/big

inlineMap:
  # Inlined map
  << : {""x"": 1, ""y"": 2, ""r"": 10}
  label: center/big

inlineSequenceMap:
  # Inlined map in sequence
  << : [ *CENTER, {""r"": 10} ]
  label: center/big
";

    [Fact]
    public void Merge()
    {
        var m = (YamlMap)YamlDecoder.Unmarshal(System.Text.Encoding.UTF8.GetBytes(MergeTests.Replace("\r\n", "\n")))!;
        Assert.Equal(9, m.Entries.Count);
        foreach (KeyValuePair<object, object?> kv in m.Entries)
        {
            if ((string)kv.Key == "anchors")
            {
                continue;
            }
            Assert.Equal("{\"label\": \"center/big\", \"r\": int(10), \"x\": int(1), \"y\": int(2)}", Dump(kv.Value));
        }
    }

    public static TheoryData<string, string> LimitTests() => new()
    {
        { "{a: &a [{a}" + Repeat(",{a}", 1000 * 1024 / 4 - 100) + "], b: &b [*a" + Repeat(",*a", 99) + "]}", "yaml: document contains excessive aliasing" },
        { Repeat("[", 1000 * 1024), "yaml: exceeded max depth of 10000" },
        { "x: " + Repeat("{", 1000 * 1024), "yaml: exceeded max depth of 10000" },
        { Repeat("- ", 1000 * 1024), "yaml: exceeded max depth of 10000" },
        { Repeat(Repeat("- ", 1000) + "\n", 1024 / 2), "" },
        { "a: &a [{a}" + Repeat(",{a}", 1 * 1024 / 4 - 1) + "]", "" },
        { "a: &a [{a}" + Repeat(",{a}", 10 * 1024 / 4 - 1) + "]", "" },
        { "a: &a [{a}" + Repeat(",{a}", 100 * 1024 / 4 - 1) + "]", "" },
        { "a: &a [{a}" + Repeat(",{a}", 1000 * 1024 / 4 - 1) + "]", "" },
        { Repeat("[", 10000) + "1" + Repeat(",1", 1000 * 1024 / 2 - 20000 - 1) + Repeat("]", 10000), "" },
        { "{a,b:\n" + Repeat(" {a,b:", 10000 - 2) + " [1" + Repeat(",1", 1000 * 1024 / 2 - 6 * 10000 - 1) + "]" + Repeat("}", 10000 - 1), "" },
        { Repeat("- " + Repeat("[", 10000) + Repeat("]", 10000) + "\n", 1000 * 1024 / 20000), "" },
    };

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    // Go grows its stacks; the port gets the same results given a big enough one.
    [Theory]
    [MemberData(nameof(LimitTests))]
    public void Limits(string data, string error)
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                YamlDecoder.Unmarshal(System.Text.Encoding.UTF8.GetBytes(data));
            }
            catch (Exception e)
            {
                caught = e;
            }
        }, 256 * 1024 * 1024);
        thread.Start();
        thread.Join();
        if (error.Length == 0)
        {
            Assert.Null(caught);
        }
        else
        {
            Assert.Equal(error, Assert.IsType<YamlException>(caught).Message);
        }
    }

    // On a 1 MB stack, nesting Go accepts either decodes or fails cleanly; it never crashes the process.
    [Fact]
    public void DeepNestingOnSmallStack()
    {
        Exception? caught = null;
        var thread = new Thread(() =>
        {
            try
            {
                YamlToJson.ParseYaml(Repeat("[", 10000) + Repeat("]", 10000));
            }
            catch (Exception e)
            {
                caught = e;
            }
        }, 1024 * 1024);
        thread.Start();
        thread.Join();
        if (caught != null)
        {
            Assert.Equal("error converting YAML to JSON: yaml: exceeded max depth", Assert.IsType<YamlException>(caught).Message);
        }
    }

    [Theory]
    [InlineData("2015-01-01", true)]
    [InlineData("2015-1-1", true)]
    [InlineData("2016-02-29", true)]
    [InlineData("2015-02-29", false)]
    [InlineData("2015-13-01", false)]
    [InlineData("2015-01-01T10:00:00Z", true)]
    [InlineData("2015-01-01t10:00:00.123456789+09:00", true)]
    [InlineData("2015-01-01T24:00:00Z", false)]
    [InlineData("2015-01-01T10:00:00", false)]
    [InlineData("2015-01-01 10:00:00", true)]
    [InlineData("2015-01-01   10:00:00,5", true)]
    [InlineData("2015-01-01 10:00:00Z", false)]
    [InlineData("2015-01-01T10:00:00+0900", false)]
    [InlineData("20150-01-01", false)]
    public void Timestamps(string s, bool ok)
    {
        Assert.Equal(ok, YamlResolve.ParseTimestamp(s));
    }
}
