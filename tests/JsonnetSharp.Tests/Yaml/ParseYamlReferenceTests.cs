using System.Text;
using JsonnetSharp.Yaml;

namespace JsonnetSharp.Tests;

// std.parseYaml cases whose expected output (or error) was produced by the go-jsonnet 0.22.0 binary.
// testdata/parseyaml_refcases.txt lines: name, base64 YAML, "ok" or "error", canonical value or base64 error text.
public class ParseYamlReferenceTests
{
    // parseyaml_fuzz.txt holds random token soups, which mostly exercise scanner and parser error paths.
    private static readonly string[][] Cases = new[] { "parseyaml_refcases.txt", "parseyaml_fuzz.txt" }
        .SelectMany(f => File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "testdata", f)))
        .Where(l => l.Length > 0)
        .Select(l => l.Split('\t'))
        .ToArray();

    public static IEnumerable<object[]> CaseNames() => Cases.Select(c => new object[] { c[0] });

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void MatchesGoJsonnet(string name)
    {
        string[] c = Cases.Single(e => e[0] == name);
        string yaml = Unbase64(c[1]);
        if (c[2] == "error")
        {
            var ex = Assert.Throws<YamlException>(() => YamlToJson.ParseYaml(yaml));
            Assert.Equal(Unbase64(c[3]), "failed to parse YAML: " + ex.Message);
            return;
        }
        Assert.Equal(c[3], Canon(YamlToJson.ParseYaml(yaml)));
    }

    internal static string Unbase64(string s) => Encoding.UTF8.GetString(Convert.FromBase64String(s));

    // The canonical form the generator writes: doubles as their IEEE bits, strings as UTF-8 hex, object
    // members sorted.
    internal static string Canon(object? v) => v switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        double d => "n" + BitConverter.DoubleToInt64Bits(d).ToString("x16"),
        string s => "s" + string.Concat(Encoding.UTF8.GetBytes(s).Select(x => x.ToString("x2"))),
        List<object?> list => "[" + string.Join(",", list.Select(Canon)) + "]",
        Dictionary<string, object?> map => "{" + string.Join(",",
            map.Select(kv => Canon(kv.Key) + ":" + Canon(kv.Value)).OrderBy(x => x, StringComparer.Ordinal)) + "}",
        _ => throw new InvalidOperationException("unexpected value type " + v.GetType()),
    };
}
