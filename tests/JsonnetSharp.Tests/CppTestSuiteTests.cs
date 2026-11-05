// Ports the cpp-jsonnet runners that go-jsonnet's tests.sh runs against its own binary:
// test_suite/run_tests.sh (with tests.source) and the evaluation cases of test_cmd/run_cmd_tests.sh
// (with cmd_tests.source and OVERRIDE_DIR=testdata/cpp-tests-override).

using System.Text;
using System.Text.RegularExpressions;

namespace JsonnetSharp.Tests;

public class CppTestSuiteTests
{
    private static readonly string SuiteDir = Path.Combine(AppContext.BaseDirectory, "cpp_test_suite");
    private static readonly string CmdDir = Path.Combine(AppContext.BaseDirectory, "cpp_test_cmd");
    private static readonly string OverrideDir = Path.Combine(AppContext.BaseDirectory, "testdata", "cpp-tests-override");

    // tests.source postprocess_output: redact stdlib line numbers.
    private static readonly Regex StdlibPosition = new("\tstd\\.jsonnet:[0-9]*:[0-9-]*", RegexOptions.CultureInvariant);

    private static IEnumerable<object[]> Suite(bool errorTests) =>
        Directory.GetFiles(SuiteDir, "*.jsonnet")
            .Select(Path.GetFileName)
            .Where(n => n!.StartsWith("error.", StringComparison.Ordinal) == errorTests)
            .OrderBy(n => n, StringComparer.Ordinal)
            .Select(n => new object[] { n! });

    public static IEnumerable<object[]> SuiteCases() => Suite(false);

    public static IEnumerable<object[]> SuiteErrorCases() => Suite(true);

    // tests.sh sets DISABLE_ERROR_TESTS: the goldens hold cpp-jsonnet's error texts, and go-jsonnet even
    // succeeds on a few of them (error.array_fractional_index, error.integer_left_shift_runtime,
    // error.std_makeArray_negative).
    [Theory(Skip = "go-jsonnet runs the cpp test_suite with DISABLE_ERROR_TESTS")]
    [MemberData(nameof(SuiteErrorCases))]
    public void TestSuiteError(string test) => TestSuite(test);

    [Theory]
    [MemberData(nameof(SuiteCases))]
    public void TestSuite(string test)
    {
        int expectedExitCode = test.StartsWith("error.", StringComparison.Ordinal) ? 1 : 0;
        string goldenPath = Path.Combine(SuiteDir, test + ".golden");
        // $(cat file) drops trailing newlines.
        string goldenOutput = File.Exists(goldenPath) ? File.ReadAllText(goldenPath, TestUtils.Utf8).TrimEnd('\n') : "true";

        (int exitCode, string output) = TestUtils.OnStack(TestUtils.OneMegabyte, () =>
        {
            StringWriter combined = new() { NewLine = "\n" };
            JsonnetVm vm = new() { Importer = new RootedImporter(SuiteDir) };
            vm.SetTraceOut(combined);
            if (test.StartsWith("tla.", StringComparison.Ordinal))
            {
                vm.TlaVar("var1", "test");
                vm.TlaCode("var2", "{x:1,y:2}");
            }
            else
            {
                vm.ExtVar("var1", "test");
                vm.ExtCode("var2", "{x:1,y:2}");
            }
            try
            {
                combined.Write(vm.EvaluateFile(test));
                return (0, combined.ToString());
            }
            catch (JsonnetException e)
            {
                combined.Write(e.Message + "\n");
                return (1, combined.ToString());
            }
        });

        Assert.True(expectedExitCode == exitCode, "Actual exit code " + exitCode + ", expected " + expectedExitCode + "\n" + output);

        string actual = string.Join("\n", output.TrimEnd('\n').Split('\n').Select(l => StdlibPosition.Replace(l, "\tstd.jsonnet:<stdlib_position_redacted>", 1)));
        Assert.True(goldenOutput == actual, "Expected:\n" + goldenOutput + "\nThis run's output:\n" + actual);
    }

    private enum CmdMode
    {
        Exec,
        File,
        Stdin,
        Stream,
        Multi,
    }

    private sealed record CmdCase(string Name, int ExitCode, CmdMode Mode, string Input, string[]? JPaths = null, Action<JsonnetVm>? Setup = null)
    {
        public override string ToString() => Name;
    }

    private const string Recurse100 = "local f(n, c=0) = if n == 0 then error \"whee\" else f(n - 1, c + n); f(100)";
    private static readonly string[] L1 = { "lib1" };
    private static readonly string[] L2 = { "lib2" };
    private static readonly string[] L12 = { "lib1", "lib2" };
    private static readonly string[] L21 = { "lib2", "lib1" };

    // do_test cases that only exercise the library through jsonnet's options. Skipped, because they test
    // the CLI binary itself (argument parsing/validation, usage, file output): no_args, help, version1,
    // version2, bad_out, simple4, simple5, simple_out, exec_out, max_stack5, max_stack6, jpath8, max_trace5,
    // multi1, multi2, multi3, nonewline_multi1, nonewline_multi2, nonewline_yaml1, yaml3 and all fmt_* tests.
    private static readonly CmdCase[] CmdCases =
    {
        new("simple1", 0, CmdMode.File, "test.jsonnet"),
        new("simple3", 0, CmdMode.Stdin, "simple3.stdin"),
        new("exec1", 0, CmdMode.Exec, "{ a: 1, b: 2, c: 3 }"),
        new("double_dash", 0, CmdMode.Exec, "-1"),
        new("max_stack1", 1, CmdMode.Exec, "local x = 1; x", Setup: vm => vm.MaxStack = 1),
        new("max_stack2", 1, CmdMode.Exec, "local x = 1; x", Setup: vm => vm.MaxStack = 1),
        new("max_stack3", 0, CmdMode.Exec, "local x = 1; x", Setup: vm => vm.MaxStack = 2),
        new("max_stack4", 0, CmdMode.Exec, "local f(n, c=0) = if n == 0 then c else f(n - 1, c + n) tailstrict; f(100)", Setup: vm => vm.MaxStack = 7),
        new("jpath1", 0, CmdMode.Exec, "import \"lib1_test.jsonnet\"", L1),
        new("jpath2", 0, CmdMode.Exec, "import \"lib1_test.jsonnet\"", L1),
        new("jpath3", 0, CmdMode.Exec, "import \"lib2_test.jsonnet\"", L2),
        new("jpath4", 0, CmdMode.Exec, "import \"lib2_test.jsonnet\"", L12),
        new("jpath5", 0, CmdMode.Exec, "import \"lib2_test.jsonnet\"", L21),
        new("jpath6", 0, CmdMode.Exec, "importstr \"shared.txt\"", L21),
        new("jpath7", 0, CmdMode.Exec, "importstr \"shared.txt\"", L12),
        new("jpath9", 0, CmdMode.Exec, "importbin \"shared.txt\"", L21),
        new("jpath10", 0, CmdMode.Exec, "importbin \"shared.txt\"", L12),
        new("ext1", 0, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtVar("x", "1")),
        new("ext2", 0, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtVar("x", "1")),
        new("ext3", 1, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtVar("y", "1")),
        new("ext4", 0, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtCode("x", "1+1")),
        // --ext-str-file / --ext-code-file bind "importstr @'<file>'" / "import @'<file>'" (cmd.go getVarFile).
        new("ext5", 0, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtCode("x", "importstr @'test.txt'")),
        new("ext6", 0, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtCode("x", "import @'test.jsonnet'")),
        new("ext7", 0, CmdMode.Exec, "std.extVar(\"x\")", Setup: vm => vm.ExtCode("x", "import @'lib1/lib3_test.jsonnet'")),
        new("tla1", 0, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaVar("x", "1")),
        new("tla2", 0, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaVar("x", "1")),
        new("tla3", 1, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaVar("y", "1")),
        new("tla4", 0, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaCode("x", "1+1")),
        new("tla5", 0, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaCode("x", "importstr @'test.txt'")),
        new("tla6", 0, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaCode("x", "import @'test.jsonnet'")),
        new("tla7", 1, CmdMode.Exec, "function() 3", Setup: vm => vm.TlaVar("x", "val")),
        new("tla8", 0, CmdMode.Exec, "3", Setup: vm => vm.TlaVar("x", "val")),
        new("tla9", 0, CmdMode.Exec, "function() 3"),
        new("tla10", 0, CmdMode.Exec, "function(x) x", Setup: vm => vm.TlaCode("x", "import @'lib1/lib3_test.jsonnet'")),
        new("max_trace1", 1, CmdMode.Exec, Recurse100, Setup: vm => vm.MaxTrace = 10),
        new("max_trace2", 1, CmdMode.Exec, Recurse100, Setup: vm => vm.MaxTrace = 10),
        new("max_trace3", 1, CmdMode.Exec, Recurse100, Setup: vm => vm.MaxTrace = 20),
        new("max_trace4", 1, CmdMode.Exec, Recurse100, Setup: vm => vm.MaxTrace = 0),
        new("nonewline1", 0, CmdMode.Exec, "42", Setup: vm => vm.OutputNewline = false),
        new("nonewline2", 0, CmdMode.File, "test.jsonnet", Setup: vm => vm.OutputNewline = false),
        new("nonewline3", 0, CmdMode.Exec, "\"hello\"", Setup: vm => { vm.OutputNewline = false; vm.StringOutput = true; }),
        new("multi4", 1, CmdMode.Multi, "null"),
        new("yaml1", 0, CmdMode.Stream, "[1,2,3]"),
        new("yaml2", 1, CmdMode.Stream, "null"),
        new("string1", 0, CmdMode.Exec, "\"A long\\nparagraph.\"", Setup: vm => vm.StringOutput = true),
        new("string2", 1, CmdMode.Exec, "null", Setup: vm => vm.StringOutput = true),
        // JSONNET_PATH=a:b is appended to the jpaths last entry first (cmd.go main).
        new("jsonnet_path1", 0, CmdMode.Exec, "[importstr \"shared.txt\", importbin \"shared.txt\"]", L21),
        new("jsonnet_path2", 0, CmdMode.Exec, "[importstr \"shared.txt\", importbin \"shared.txt\"]", L12),
    };

    public static IEnumerable<object[]> CmdCaseNames() => CmdCases.Select(c => new object[] { c.Name });

    // check_file: cmp against the override golden if there is one, else the test_cmd golden, else /dev/null.
    private static string Golden(string name, string ext)
    {
        foreach (string dir in new[] { OverrideDir, CmdDir })
        {
            string path = Path.Combine(dir, name + ".golden." + ext);
            if (File.Exists(path))
            {
                return File.ReadAllText(path, TestUtils.Utf8);
            }
        }
        return "";
    }

    [Theory]
    [MemberData(nameof(CmdCaseNames))]
    public void TestCmd(string name)
    {
        CmdCase c = CmdCases.Single(x => x.Name == name);
        (int exitCode, string stdout, string stderr) = TestUtils.OnStack(TestUtils.OneMegabyte, () =>
        {
            StringWriter err = new() { NewLine = "\n" };
            JsonnetVm vm = new() { Importer = new RootedImporter(CmdDir, c.JPaths) };
            vm.SetTraceOut(err);
            c.Setup?.Invoke(vm);
            try
            {
                string stdout = c.Mode switch
                {
                    CmdMode.Exec => vm.EvaluateAnonymousSnippet("<cmdline>", c.Input),
                    CmdMode.File => vm.EvaluateFile(c.Input),
                    CmdMode.Stdin => vm.EvaluateAnonymousSnippet("<stdin>", File.ReadAllText(Path.Combine(CmdDir, c.Input), TestUtils.Utf8)),
                    CmdMode.Stream => WriteOutputStream(vm.EvaluateAnonymousSnippetStream("<cmdline>", c.Input)),
                    _ => string.Join("", vm.EvaluateAnonymousSnippetMulti("<cmdline>", c.Input).Values),
                };
                return (0, stdout, err.ToString());
            }
            catch (JsonnetException e)
            {
                err.Write(e.Message + "\n");
                return (1, "", err.ToString());
            }
        });

        Assert.True(c.ExitCode == exitCode, "Actual exit code " + exitCode + ", expected " + c.ExitCode + "\nstdout:\n" + stdout + "\nstderr:\n" + stderr);
        Assert.Equal(Golden(name, "stdout"), stdout);
        Assert.Equal(Golden(name, "stderr"), stderr);
    }

    // cmd.go writeOutputStream.
    private static string WriteOutputStream(IReadOnlyList<string> output)
    {
        StringBuilder sb = new();
        foreach (string doc in output)
        {
            sb.Append("---\n").Append(doc);
        }
        if (output.Count > 0)
        {
            sb.Append("...\n");
        }
        return sb.ToString();
    }
}
