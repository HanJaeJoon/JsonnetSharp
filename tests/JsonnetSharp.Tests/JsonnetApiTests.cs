// Ports go-jsonnet jsonnet_test.go (interpreter_test.go has no tests).

using System.Text;

namespace JsonnetSharp.Tests;

public class JsonnetApiTests
{
    private static void GenericTestErrorMessage((string Name, string Input, string ErrString)[] tests, Func<string, string> format)
    {
        foreach ((string name, string input, string errString) in tests)
        {
            JsonnetVm vm = new();
            JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet(name, input));
            Assert.IsType<JsonnetRuntimeException>(e);
            Assert.Equal(errString, format(e.Message));
        }
    }

    [Fact]
    public void TestOneLineError() => GenericTestErrorMessage(
        new[] { ("error", "error \"x\"", "RUNTIME ERROR: x") },
        // RuntimeError.Error() is the first line of the formatted error.
        m => m.Substring(0, m.IndexOf('\n')));

    [Fact]
    public void TestMinimalError() => GenericTestErrorMessage(
        new[]
        {
            ("error", "error \"x\"", "RUNTIME ERROR: x\n" +
                "\terror:1:1-10\t$\n" +
                "\tDuring evaluation\t\n"),
            ("error_in_func", "local x(n) = if n == 0 then error \"x\" else x(n - 1); x(3)", "RUNTIME ERROR: x\n" +
                "\terror_in_func:1:29-38\tfunction <x>\n" +
                "\terror_in_func:1:44-52\tfunction <x>\n" +
                "\terror_in_func:1:44-52\tfunction <x>\n" +
                "\terror_in_func:1:44-52\tfunction <x>\n" +
                "\terror_in_func:1:54-58\t$\n" +
                "\tDuring evaluation\t\n"),
            ("error_in_error", "error (error \"x\")", "RUNTIME ERROR: x\n" +
                "\terror_in_error:1:8-17\t$\n" +
                "\tDuring evaluation\t\n"),
        },
        // termErrorFormatter{maxStackTraceSize: 20} is the VM's default formatter.
        m => m);

    private static string RemoveExcessiveWhitespace(string s)
    {
        StringBuilder buf = new();
        bool needsSeparation = false;
        foreach (char c in s)
        {
            if (c == '\n' || c == ' ')
            {
                needsSeparation = true;
            }
            else
            {
                if (needsSeparation)
                {
                    buf.Append(' ');
                    needsSeparation = false;
                }
                buf.Append(c);
            }
        }
        return buf.ToString();
    }

    [Fact]
    public void TestCustomImporter()
    {
        JsonnetVm vm = new()
        {
            Importer = new MemoryImporter
            {
                Data =
                {
                    ["a.jsonnet"] = JsonnetContents.FromString("2 + 2"),
                    ["b.jsonnet"] = JsonnetContents.FromString("3 + 3"),
                    ["c.bin"] = JsonnetContents.FromBytes(new byte[] { 0xff, 0xfe, 0xfd }),
                },
            },
        };
        string actual = vm.EvaluateAnonymousSnippet("custom_import.jsonnet", "[import \"a.jsonnet\", importstr \"b.jsonnet\", importbin \"c.bin\"]");
        Assert.Equal("[ 4, \"3 + 3\", [ 255, 254, 253 ] ]", RemoveExcessiveWhitespace(actual));
    }

    private sealed class ImporterWithHistory : IJsonnetImporter
    {
        public readonly MemoryImporter I = new();
        public readonly List<(string ImportedFrom, string ImportedPath)> History = new();

        public JsonnetContents Import(string importedFrom, string importedPath, out string foundAt)
        {
            History.Add((importedFrom, importedPath));
            return I.Import(importedFrom, importedPath, out foundAt);
        }
    }

    private static ImporterWithHistory HistoryImporter()
    {
        ImporterWithHistory importer = new();
        importer.I.Data["a.jsonnet"] = JsonnetContents.FromString("2 + 2");
        return importer;
    }

    [Fact]
    public void TestExtVarImportedFrom()
    {
        JsonnetVm vm = new();
        vm.ExtCode("aaa", "import 'a.jsonnet'");
        ImporterWithHistory importer = HistoryImporter();
        vm.Importer = importer;
        Assert.Equal("4", RemoveExcessiveWhitespace(vm.EvaluateAnonymousSnippet("blah.jsonnet", "std.extVar('aaa')")));
        Assert.Equal(new[] { ("", "a.jsonnet") }, importer.History);
    }

    [Fact]
    public void TestTLAImportedFrom()
    {
        JsonnetVm vm = new();
        vm.TlaCode("aaa", "import 'a.jsonnet'");
        ImporterWithHistory importer = HistoryImporter();
        vm.Importer = importer;
        Assert.Equal("4", RemoveExcessiveWhitespace(vm.EvaluateAnonymousSnippet("blah.jsonnet", "function(aaa) aaa")));
        Assert.Equal(new[] { ("", "a.jsonnet") }, importer.History);
    }

    [Fact]
    public void TestAnonymousImportedFrom()
    {
        JsonnetVm vm = new();
        ImporterWithHistory importer = HistoryImporter();
        vm.Importer = importer;
        Assert.Equal("4", RemoveExcessiveWhitespace(vm.EvaluateAnonymousSnippet("blah.jsonnet", "import \"a.jsonnet\"")));
        Assert.Equal(new[] { ("", "a.jsonnet") }, importer.History);
    }

    [Fact]
    public void TestContents()
    {
        string a = "aaa";
        JsonnetContents c1 = JsonnetContents.FromString(a);
        a = "bbb";
        Assert.Equal("aaa", c1.ToString());
        JsonnetContents c2 = JsonnetContents.FromString(a);
        JsonnetContents c3 = JsonnetContents.FromString(a);
        // Contents should distinguish between different instances even if they have the same data inside.
        Assert.NotEqual(c2, c3);
    }

    [Fact]
    public void TestExtReset()
    {
        JsonnetVm vm = new();
        vm.ExtVar("fooString", "bar");
        vm.ExtCode("fooCode", "true");
        vm.EvaluateAnonymousSnippet("test.jsonnet", "{ str: std.extVar('fooString'), code: std.extVar('fooCode') }");
        vm.ExtReset();
        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() =>
            vm.EvaluateAnonymousSnippet("test.jsonnet", "{ str: std.extVat('fooString'), code: std.extVar('fooCode') }"));
        Assert.Contains("Undefined external variable", e.Message);
    }

    [Fact]
    public void TestTLAReset()
    {
        JsonnetVm vm = new();
        vm.TlaVar("fooString", "bar");
        vm.TlaCode("fooCode", "true");
        vm.EvaluateAnonymousSnippet("test.jsonnet", "function (fooString, fooCode) { str: fooString, code: fooCode }");
        vm.TlaReset();
        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() =>
            vm.EvaluateAnonymousSnippet("test.jsonnet", "function(fooString, fooCode) { str: fooString, code: fooCode }"));
        Assert.Contains("Missing argument", e.Message);
    }

    [Fact(Skip = "ExtNode/TLANode are not part of the JsonnetSharp public API")]
    public void TestExtTypes()
    {
    }

    [Fact(Skip = "ExtNode/TLANode are not part of the JsonnetSharp public API")]
    public void TestTLATypes()
    {
    }

    [Fact]
    public void TestSetTraceOut()
    {
        StringWriter traceOut = new();
        JsonnetVm vm = new();
        vm.SetTraceOut(traceOut);

        const string Filename = "blah.jsonnet";
        const string Msg = "TestSetTraceOut Trace Message";
        vm.EvaluateAnonymousSnippet(Filename, "std.trace('" + Msg + "', 'rest')");
        Assert.Equal("TRACE: " + Filename + ":1 " + Msg, RemoveExcessiveWhitespace(traceOut.ToString()));
    }
}
