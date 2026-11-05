// Ports go-jsonnet main_test.go (TestEval, TestEvalUnusualFilenames, TestEvalCachedErrorTwice).

using System.Globalization;
using System.Reflection;
using System.Text;
using Node = JsonnetSharp.Ast.Node;

namespace JsonnetSharp.Tests;

public class MainTests
{
    private static readonly string Root = AppContext.BaseDirectory;

    private sealed class TestMetadata
    {
        public Dictionary<string, string> ExtVars = new();
        public Dictionary<string, string> ExtCode = new();
    }

    private static readonly Dictionary<string, string> StandardExtVars = new()
    {
        ["stringVar"] = "2 + 2",
    };

    private static readonly Dictionary<string, string> StandardExtCode = new()
    {
        ["codeVar"] = "3 + 3",
        ["errorVar"] = "error 'xxx'",
        ["staticErrorVar"] = ")",
        ["UndeclaredX"] = "x",
        ["selfRecursiveVar"] = "[42, std.extVar(\"selfRecursiveVar\")[0] + 1]",
        ["mutuallyRecursiveVar1"] = "[42, std.extVar(\"mutuallyRecursiveVar2\")[0] + 1]",
        ["mutuallyRecursiveVar2"] = "[42, std.extVar(\"mutuallyRecursiveVar1\")[0] + 1]",
    };

    private static readonly Dictionary<string, TestMetadata> MetadataForTests = new[]
    {
        "testdata/extvar_code",
        "testdata/extvar_error",
        "testdata/extvar_hermetic",
        "testdata/extvar_mutually_recursive",
        "testdata/extvar_self_recursive",
        "testdata/extvar_static_error",
        "testdata/extvar_string",
    }.ToDictionary(n => n, _ => new TestMetadata { ExtVars = StandardExtVars, ExtCode = StandardExtCode });

    private static readonly JsonnetNativeFunction JsonToString = new("jsonToString", new[] { "x" }, x => GoJsonMarshal(x[0]));

    private static readonly JsonnetNativeFunction NativeError = new("nativeError", Array.Empty<string>(), _ => throw new InvalidOperationException("native function error"));

    // A C# native function has no separate panic channel: every exception becomes the returned error.
    // The harness throws the text Go's recover produces, so the golden still checks the error plumbing.
    private static readonly JsonnetNativeFunction NativePanic = new("nativePanic", Array.Empty<string>(), _ => throw new InvalidOperationException("native function \"nativePanic\" panicked: native function panic"));

    private enum EvalKind
    {
        Regular,
        Multi,
    }

    private sealed class JsonnetInput
    {
        public string Name = "";
        public byte[] Input = Array.Empty<byte>();
        public EvalKind EKind;
        public bool StringOutputMode;
        public bool NoNewline;
        public Dictionary<string, string> ExtVars = new();
        public Dictionary<string, string> ExtCode = new();
        public string ImporterRoot = Root;
    }

    private sealed class JsonnetResult
    {
        public string Output = "";
        public Dictionary<string, string>? OutputMulti;
        public bool IsError;
    }

    private static void TestChildren(Node node)
    {
        foreach (Node child in Parser.Children(node))
        {
            TestChildren(child);
        }
    }

    // vm.evaluateSnippet is unexported in Go; the test calls the private counterpart the same way.
    private static readonly MethodInfo EvaluateSnippetMethod = typeof(JsonnetVm).GetMethod("EvaluateSnippet", BindingFlags.NonPublic | BindingFlags.Instance)!;
    private static readonly Type VmEvalKind = typeof(JsonnetVm).GetNestedType("EvalKind", BindingFlags.NonPublic)!;
    private static readonly FieldInfo ErrorFormatterField = typeof(JsonnetVm).GetField("_errorFormatter", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private static JsonnetResult RunInternalJsonnet(JsonnetInput i)
    {
        JsonnetVm vm = new()
        {
            Importer = new RootedImporter(i.ImporterRoot),
            StringOutput = i.StringOutputMode,
            OutputNewline = !i.NoNewline,
        };
        // errFormatter := termErrorFormatter{pretty: true, maxStackTraceSize: 9}; the VM formats its own errors.
        TermErrorFormatter errFormatter = new(9, true);
        TermErrorFormatter vmFormatter = (TermErrorFormatter)ErrorFormatterField.GetValue(vm)!;
        vmFormatter.Pretty = true;
        vmFormatter.MaxStackTraceSize = 9;

        foreach (KeyValuePair<string, string> kv in i.ExtVars)
        {
            vm.ExtVar(kv.Key, kv.Value);
        }
        foreach (KeyValuePair<string, string> kv in i.ExtCode)
        {
            vm.ExtCode(kv.Key, kv.Value);
        }

        vm.NativeFunction(JsonToString);
        vm.NativeFunction(NativeError);
        vm.NativeFunction(NativePanic);

        string input = JsonnetContents.FromBytes(i.Input).ToString();

        Node rawAST;
        try
        {
            rawAST = Parser.SnippetToRawAST(i.Name, "", input).Node;
        }
        catch (StaticError e)
        {
            return new JsonnetResult { Output = errFormatter.Format(e) + "\n", IsError = true };
        }
        TestChildren(rawAST);

        Node desugaredAST;
        try
        {
            desugaredAST = Program.SnippetToAST(i.Name, i.Name, input);
        }
        catch (StaticError e)
        {
            return new JsonnetResult { Output = errFormatter.Format(e) + "\n", IsError = true };
        }
        TestChildren(desugaredAST);

        object rawOutput;
        try
        {
            rawOutput = EvaluateSnippetMethod.Invoke(vm, new object[] { i.Name, i.Name, input, Enum.ToObject(VmEvalKind, (int)i.EKind) })!;
        }
        catch (TargetInvocationException e) when (e.InnerException is JsonnetException je)
        {
            return new JsonnetResult { Output = je.Message + "\n", IsError = true };
        }
        return i.EKind == EvalKind.Multi
            ? new JsonnetResult { OutputMulti = (Dictionary<string, string>)rawOutput }
            : new JsonnetResult { Output = (string)rawOutput };
    }

    private static List<string> CompareSingleGolden(string path, JsonnetResult result)
    {
        if (result.OutputMulti != null)
        {
            return new List<string> { "outputMulti is populated in a single-file test for " + path };
        }
        string? diff = TestUtils.CompareWithGolden(result.Output, File.ReadAllBytes(path));
        return diff == null ? new List<string>() : new List<string> { "golden file " + path + " has diff:\n" + diff };
    }

    private static List<string> CompareMultifileGolden(string path, JsonnetResult result)
    {
        List<string> errs = new();
        Dictionary<string, byte[]> goldenContent = new();
        foreach (string f in Directory.GetFiles(path).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal)!)
        {
            byte[] golden = File.ReadAllBytes(Path.Combine(path, f));
            if (!result.OutputMulti!.ContainsKey(f))
            {
                errs.Add("jsonnet did not output expected file " + f);
                continue;
            }
            goldenContent[f] = golden;
        }
        foreach (KeyValuePair<string, string> kv in result.OutputMulti!)
        {
            if (!goldenContent.TryGetValue(kv.Key, out byte[]? golden))
            {
                errs.Add("jsonnet outputted file " + kv.Key + " which does not exist in goldens");
                continue;
            }
            string? diff = TestUtils.CompareWithGolden(kv.Value, golden);
            if (diff != null)
            {
                errs.Add("golden file " + kv.Key + " has diff:\n" + diff);
            }
        }
        return errs;
    }

    private static void RunTest(string name, string inputPath, string goldenPath, TestMetadata meta, string importerRoot)
    {
        byte[] input = File.ReadAllBytes(inputPath);

        EvalKind eKind = EvalKind.Regular;
        Func<string, JsonnetResult, List<string>> compareFunc = CompareSingleGolden;

        // If the golden path is a directory, this is a multi-test.
        if (Directory.Exists(goldenPath))
        {
            eKind = EvalKind.Multi;
            compareFunc = CompareMultifileGolden;
        }

        JsonnetResult result = TestUtils.OnStack(TestUtils.OneMegabyte, () => RunInternalJsonnet(new JsonnetInput
        {
            Name = name,
            Input = input,
            EKind = eKind,
            StringOutputMode = goldenPath.EndsWith("_string_output.golden", StringComparison.Ordinal),
            NoNewline = goldenPath.Contains("_no_newline"),
            ExtVars = meta.ExtVars,
            ExtCode = meta.ExtCode,
            ImporterRoot = importerRoot,
        }));

        if (eKind == EvalKind.Multi && result.IsError)
        {
            // If it's an error, then result.output is populated instead. The golden directory holds it in an "error" file.
            result.OutputMulti = new Dictionary<string, string> { ["error"] = result.Output };
            result.Output = "";
        }

        List<string> errs = compareFunc(goldenPath, result);
        Assert.True(errs.Count == 0, string.Join("\n", errs));
    }

    public static IEnumerable<object[]> EvalCases() =>
        Directory.GetFiles(Path.Combine(Root, "testdata"), "*.jsonnet")
            .Select(f => "testdata/" + Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(EvalCases))]
    public void TestEval(string name)
    {
        TestMetadata meta = MetadataForTests.TryGetValue(name, out TestMetadata? m) ? m : new TestMetadata();
        RunTest(name, Path.Combine(Root, name + ".jsonnet"), Path.Combine(Root, name + ".golden"), meta, Root);
    }

    private sealed class FactNotOnWindowsAttribute : FactAttribute
    {
        public FactNotOnWindowsAttribute()
        {
            if (Path.DirectorySeparatorChar == '\\')
            {
                // Escaped filenames exist because their unescaped forms are invalid on Windows.
                Skip = "the file names \" and ' are invalid on Windows (Go skips this test there too)";
            }
        }
    }

    [FactNotOnWindows]
    public void TestEvalUnusualFilenames()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsonnet" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // These files are imported by files below, but their round-trip behavior is covered by TestEval.
            File.Copy(Path.Combine(Root, "testdata", "true.jsonnet"), Path.Combine(dir, "true.jsonnet"));

            const string Marker = "// This file is there only for its filename: to test escaping in imports\n{}\n";
            (string Name, string Content, string Golden)[] files =
            {
                ("\"", Marker, "{ }\n"),
                ("'", Marker, "{ }\n"),
                (
                    "import_various_literals_escaped",
                    "[\n\timport \"\\u0074rue.jsonnet\",\n\timport '\\u0074rue.jsonnet',\n\timportstr @\"\"\".jsonnet\",\n\timportstr @'''.jsonnet',\n]\n",
                    "[\n   true,\n   true,\n   \"// This file is there only for its filename: to test escaping in imports\\n{}\\n\",\n   \"// This file is there only for its filename: to test escaping in imports\\n{}\\n\"\n]\n"
                ),
                (
                    "importstr_various_literals_escaped",
                    "[\n\timportstr \"\\u0074rue.jsonnet\",\n\timportstr '\\u0074rue.jsonnet',\n\timportstr @\"\"\".jsonnet\",\n\timportstr @'''.jsonnet',\n]\n",
                    "[\n   \"true\\n\",\n   \"true\\n\",\n   \"// This file is there only for its filename: to test escaping in imports\\n{}\\n\",\n   \"// This file is there only for its filename: to test escaping in imports\\n{}\\n\"\n]\n"
                ),
            };
            foreach ((string name, string content, string golden) in files)
            {
                File.WriteAllText(Path.Combine(dir, name + ".jsonnet"), content, TestUtils.Utf8);
                File.WriteAllText(Path.Combine(dir, name + ".golden"), golden, TestUtils.Utf8);
            }
            foreach ((string name, _, _) in files)
            {
                RunTest(name, Path.Combine(dir, name + ".jsonnet"), Path.Combine(dir, name + ".golden"), new TestMetadata(), dir);
            }
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TestEvalCachedErrorTwice()
    {
        // Evaluate the same erroneous file twice in the same VM.
        // See https://github.com/google/go-jsonnet/issues/822
        JsonnetVm vm = new() { Importer = new RootedImporter(Root) };
        string err1 = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateFile("testdata/syntax_error.jsonnet")).Message;
        string err2 = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateFile("testdata/syntax_error.jsonnet")).Message;
        Assert.Equal(err1, err2);
    }

    // encoding/json.Marshal of the values a native function receives.
    private static string GoJsonMarshal(object? v)
    {
        StringBuilder sb = new();
        Marshal(sb, v);
        return sb.ToString();
    }

    private static void Marshal(StringBuilder sb, object? v)
    {
        switch (v)
        {
            case null:
                sb.Append("null");
                break;
            case bool b:
                sb.Append(b ? "true" : "false");
                break;
            case double d:
                sb.Append(MarshalFloat(d));
                break;
            case string s:
                // Marshal escapes HTML characters; EncodeString is the SetEscapeHTML(false) form.
                sb.Append(GoJson.EncodeString(s).Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026"));
                break;
            case List<object?> list:
                sb.Append('[');
                for (int k = 0; k < list.Count; k++)
                {
                    if (k > 0)
                    {
                        sb.Append(',');
                    }
                    Marshal(sb, list[k]);
                }
                sb.Append(']');
                break;
            case Dictionary<string, object?> dict:
                sb.Append('{');
                bool first = true;
                foreach (string key in dict.Keys.OrderBy(k => k, Util.GoStringComparer))
                {
                    if (!first)
                    {
                        sb.Append(',');
                    }
                    first = false;
                    Marshal(sb, key);
                    sb.Append(':');
                    Marshal(sb, dict[key]);
                }
                sb.Append('}');
                break;
            default:
                throw new InvalidOperationException("json: unsupported type: " + v.GetType());
        }
    }

    // shortcut: the 'e' branch uses .NET "R" formatting, shortest only on .NET Core 3.0+; port strconv 'e' -1 if a golden needs it.
    private static string MarshalFloat(double f)
    {
        double abs = Math.Abs(f);
        if (abs == 0 || (abs >= 1e-6 && abs < 1e21))
        {
            return GoStrconv.FormatFloatShortestF(f);
        }
        string r = f.ToString("R", CultureInfo.InvariantCulture);
        int e = r.IndexOf('E');
        int exp = int.Parse(r.Substring(e + 1), CultureInfo.InvariantCulture);
        // Go's 'e' format has at least two exponent digits, and Marshal then trims "e-0X" to "e-X".
        string expText = exp < 0 ? "-" + (-exp).ToString(CultureInfo.InvariantCulture) : "+" + exp.ToString("00", CultureInfo.InvariantCulture);
        return r.Substring(0, e) + "e" + expText;
    }
}
