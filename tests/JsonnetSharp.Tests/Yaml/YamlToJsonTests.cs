using System.Text;
using JsonnetSharp.Yaml;

namespace JsonnetSharp.Tests;

// Port of the sigs.k8s.io/yaml yaml_test.go TestYAMLToJSON cases. Go compares the JSON bytes; here the expected JSON
// was decoded the way Unmarshal into an interface{} does (numbers become float64) by the test data generator and
// stored in canonical form (testdata/sigs_yamltojson.txt lines: base64 YAML, canonical value).
public class YamlToJsonTests
{
    public static IEnumerable<object[]> Cases() => File.ReadAllLines(
            Path.Combine(AppContext.BaseDirectory, "testdata", "sigs_yamltojson.txt"))
        .Where(l => l.Length > 0)
        .Select(l => l.Split((char)9))
        .Select(c => new object[] { ParseYamlReferenceTests.Unbase64(c[0]), c[1] });

    [Theory]
    [MemberData(nameof(Cases))]
    public void YamlToJsonCase(string yaml, string canon)
    {
        Assert.Equal(canon, ParseYamlReferenceTests.Canon(YamlToJson.Unmarshal(Encoding.UTF8.GetBytes(yaml))));
    }

    // The "~ key" and "null key" cases, which sigs.k8s.io/yaml expects to fail.
    [Theory]
    [InlineData("~: a")]
    [InlineData("null: a")]
    public void NullKeyFails(string yaml)
    {
        var ex = Assert.Throws<YamlException>(() => YamlToJson.Unmarshal(Encoding.UTF8.GetBytes(yaml)));
        Assert.Equal("error converting YAML to JSON: unsupported map key of type: %!s(<nil>), key: <nil>, value: \"a\"", ex.Message);
    }

    [Theory]
    [InlineData(1.5, "1.5")]
    [InlineData(1e20, "1e+20")]
    [InlineData(3.14159265358979, "3.1415927")]
    [InlineData(100000.0, "100000")]
    [InlineData(1000000.0, "1e+06")]
    [InlineData(0.0001, "0.0001")]
    [InlineData(0.00001, "1e-05")]
    [InlineData(-0.0, "-0")]
    [InlineData(16777217.0, "1.6777216e+07")]
    public void FormatFloat32(double f, string expected)
    {
        Assert.Equal(expected, GoFormat.FormatFloatG(f, 32));
    }

    [Theory]
    [InlineData("a", "\"a\"")]
    [InlineData("q\t\u00e9\"\\", "\"q\\t\u00e9\\\"\\\\\"")]
    [InlineData("\u0001\u007f\u200b\U0001F600\u00a0", "\"\\x01\\x7f\\u200b\U0001F600\\u00a0\"")]
    public void Quote(string s, string expected)
    {
        Assert.Equal(expected, GoFormat.Quote(s));
    }
}
