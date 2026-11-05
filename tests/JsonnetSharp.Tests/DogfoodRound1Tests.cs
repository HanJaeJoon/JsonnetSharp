using System.Collections.Concurrent;

namespace JsonnetSharp.Tests;

// Regressions found by dogfooding (rounds 1 to 3). Expected texts were produced by the go-jsonnet 0.22.0 binary.
public class DogfoodRound1Tests
{
    private static string Eval(string code) => new JsonnetVm().EvaluateAnonymousSnippet("<cmdline>", code);

    private static string EvalError(string code) => Assert.ThrowsAny<JsonnetException>(() => Eval(code)).Message;

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    private static T OnOneMegabyte<T>(Func<T> body) => TestUtils.OnStack(TestUtils.OneMegabyte, body);

    // deep-inheritance-chain-overflow, object-layer-chain-depth
    [Fact]
    public void FoldedObjectChainOfManyLayers() => Assert.Equal("[\n   1,\n   20000,\n   true\n]\n", OnOneMegabyte(() =>
        Eval("local o = std.foldl(function(a, i) a + {['k' + i]: i}, std.range(1, 20000), {}); [o.k1, std.length(o), 'k1' in o]")));

    // native-overflow-trace-differs
    [Fact]
    public void SuperChainHitsMaxStackWithGoTrace()
    {
        string frame = "\t<cmdline>:1:34-39\t\n";
        string expected = "RUNTIME ERROR: max stack frames exceeded.\n" + Repeat(frame, 10) + "\t...\n" + Repeat(frame, 8) +
            "\t<cmdline>:1:1-77\t$\n\tDuring evaluation\t\n";
        Assert.Equal(expected, OnOneMegabyte(() => EvalError("std.foldl(function(a, i) a + {v: super.v + 1}, std.range(1, 5000), {v: 0}).v")));
    }

    // net48-selfchain-within-maxstack
    [Fact]
    public void SelfChainWithinMaxStack() => Assert.Equal("480\n", OnOneMegabyte(() =>
        Eval("local o = {['f' + i]: if i == 0 then 0 else self['f' + (i - 1)] + 1 for i in std.range(0, 480)}; o['f480']")));

    // deep-equality-1mb-stack
    [Fact]
    public void DeepEqualityAndComparison() => Assert.Equal("[\n   true,\n   true,\n   1\n]\n", OnOneMegabyte(() =>
        Eval("local d = std.foldl(function(a, i) [a], std.range(1, 20000), []); [d == d, d < d + [1], std.length(std.set([d, d]))]")));

    // parse-depth-stack-bound, parse-depth-limit-rejects-valid
    [Fact]
    public void LongProgramsParse()
    {
        string locals = string.Concat(Enumerable.Range(0, 10000).Select(k => "local x" + k + " = " + k + ";\n")) + "x9999";
        Assert.Equal("9999\n", OnOneMegabyte(() => Eval(locals)));
        Assert.Equal("10000\n", OnOneMegabyte(() => Eval(string.Join("+", Enumerable.Repeat("1", 10000)))));
    }

    // small-stack-below-maxstack, small-stack-false-maxstack
    [Fact]
    public void RecursionWithinMaxStackOnSmallThread() => Assert.Equal("450\n", TestUtils.OnStack(256 * 1024, () =>
        Eval("local f(n) = if n == 0 then 0 else 1 + f(n - 1); f(450)")));

    // small-stack-bogus-parse-error
    [Fact]
    public void TrivialProgramOnTinyThread() => Assert.Equal("2\n", TestUtils.OnStack(128 * 1024, () => Eval("1+1")));

    // yaml-depth-message-deviation
    [Fact]
    public void DeepYamlLikeGo()
    {
        Assert.Equal("1\n", OnOneMegabyte(() =>
            Eval("std.length(std.parseYaml(std.join('', std.repeat(['['], 5000)) + std.join('', std.repeat([']'], 5000))))")));
        Assert.Equal("RUNTIME ERROR: failed to parse YAML: error converting YAML to JSON: yaml: exceeded max depth of 10000\n" +
            "\t<cmdline>:1:1-95\t$\n\tDuring evaluation\t\n", OnOneMegabyte(() =>
            EvalError("std.parseYaml(std.join('', std.repeat(['['], 10001)) + std.join('', std.repeat([']'], 10001)))")));
    }

    // null-args-internal-error, null-filename-nre-escapes, null-args-not-validated, null-args-internal-crash
    [Fact]
    public void NullArgumentsThrowArgumentNullException()
    {
        JsonnetVm vm = new();
        static void Null(string param, Action a) => Assert.Equal(param, Assert.Throws<ArgumentNullException>(a).ParamName);
        Null("val", () => vm.ExtVar("k", null!));
        Null("val", () => vm.ExtCode("k", null!));
        Null("val", () => vm.TlaVar("k", null!));
        Null("val", () => vm.TlaCode("k", null!));
        Null("f", () => vm.NativeFunction(null!));
        Null("filename", () => vm.EvaluateAnonymousSnippet(null!, "error 'x'"));
        Null("snippet", () => vm.EvaluateAnonymousSnippet("x", null!));
        Null("filename", () => vm.EvaluateAnonymousSnippetMulti(null!, "{}"));
        Null("snippet", () => vm.EvaluateAnonymousSnippetMulti("x", null!));
        Null("filename", () => vm.EvaluateAnonymousSnippetStream(null!, "[]"));
        Null("snippet", () => vm.EvaluateAnonymousSnippetStream("x", null!));
        Null("filename", () => vm.EvaluateFile(null!));
        Null("filename", () => vm.EvaluateFileMulti(null!));
        Null("filename", () => vm.EvaluateFileStream(null!));
        Null("importedFrom", () => vm.ResolveImport(null!, "x"));
        Null("importedPath", () => vm.ResolveImport("", null!));
        Null("importedFrom", () => vm.ImportData(null!, "x", out _));
        Null("importedPath", () => vm.ImportData("", null!, out _));
        Null("importedFrom", () => vm.FindDependencies(null!, new[] { "x" }));
        Null("importedPaths", () => vm.FindDependencies("", null!));
        Null("importedFrom", () => new FileImporter().Import(null!, "x", out _));
        Null("importedPath", () => new FileImporter().Import("", null!, out _));
        Null("value", () => new FileImporter().JPaths = null!);
        Null("value", () => new MemoryImporter().Data = null!);
    }

    private sealed class NullImporter : IJsonnetImporter
    {
        public bool NullContents;

        public JsonnetContents Import(string importedFrom, string importedPath, out string foundAt)
        {
            foundAt = NullContents ? importedPath : null!;
            return NullContents ? null! : JsonnetContents.FromString("1");
        }
    }

    // importer-contract-violation-crash-text, importer-contract-crash
    [Fact]
    public void ImporterReturningNullIsARuntimeError()
    {
        JsonnetVm vm = new() { Importer = new NullImporter { NullContents = true } };
        Assert.Equal("RUNTIME ERROR: IJsonnetImporter.Import returned null contents for \"a\"\n\tx:1:1-11\t$\n\tDuring evaluation\t\n",
            Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "import 'a'")).Message);
        vm.Importer = new NullImporter();
        Assert.Equal("RUNTIME ERROR: IJsonnetImporter.Import returned null foundAt for \"a\"\n\tx:1:1-14\t$\n\tDuring evaluation\t\n",
            Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "importstr 'a'")).Message);
    }

    // inf-nan-number-formatting, nonfinite-number-unparse
    [Fact]
    public void NonFiniteNumbersPrintLikeGo()
    {
        Assert.Equal("[\n   +Inf,\n   -Inf,\n   NaN,\n   \"+Inf\",\n   \"NaN\",\n   \"a = +Inf\",\n   \"[+Inf, -Inf, NaN]\",\n   \"019bd3d196119586af2529756fa77e5c\"\n]\n",
            Eval("local inf = std.sum([1e308, 1e308]); local nan = std.sum([inf, -inf]); [inf, -inf, nan, std.toString(inf), std.toString(nan), " +
                "std.manifestTomlEx({a: inf}, ''), std.manifestPython([inf, -inf, nan]), std.md5(std.toString(inf))]"));
        Assert.Equal("RUNTIME ERROR: substr third parameter should be an integer, got +Inf\n\t<cmdline>:1:38-63\t$\n\tDuring evaluation\t\n",
            EvalError("local inf = std.sum([1e308, 1e308]); std.substr('abc', 0, inf)"));
        Assert.Equal("RUNTIME ERROR: Assertion failed. NaN != NaN\n\t<std>:861:7-56\tfunction <anonymous>\n\t<cmdline>:1:75-100\t$\n\tDuring evaluation\t\n",
            EvalError("local nan = std.sum([std.sum([1e308, 1e308]), -std.sum([1e308, 1e308])]); std.assertEqual(nan, nan)"));
    }

    // yaml-float32-overflow-key
    [Fact]
    public void YamlFloatKeyBeyondFloat32IsInf() =>
        Assert.Equal("{\n   \"-.inf\": \"b\",\n   \".inf\": \"a\"\n}\n", Eval("std.parseYaml('{1e39: a, -1e39: b}')"));

    // net48-double-parse-misrounding (fails only on .NET Framework before the fix)
    [Fact]
    public void DecimalsParseCorrectlyRounded() => Assert.Equal(
        "[\n   true,\n   1.9096896232135139e-34,\n   2.2250738585072014e-308,\n   2.2250738585072014e-308,\n   0,\n   1.2351641146031164e-322\n]\n",
        Eval("[1.909689623213514e-34 == 1.9096896232135139e-34, 1.909689623213514e-34, std.parseJson('2.2250738585072012e-308'), " +
            "std.parseYaml('2.2250738585072012e-308'), 2.4703282292062327e-324, 123456789012345678901234567890123456789e-360]"));

    // net48-shortest-format (fails only on .NET Framework before the fix)
    [Fact]
    public void ShortestFormattingMatchesGo() => Assert.Equal(
        "[\n   \"[1000000000000000.2,852625145403825050000000000000000000000000000000000000000000000000000,7019876120529774000000000000000000]\",\n" +
        "   [\n      \"3.0348762e+06\",\n      \"36440.312\"\n   ]\n]\n",
        Eval("[std.manifestJsonMinified([1e15+0.3, 852625145403825050000000000000000000000000000000000000000000000000000, 7019876120529774000000000000000000]), " +
            "std.objectFields(std.parseYaml('{3034876.2: a, 36440.312: b}'))]"));

    // readme-zero-deps-system-memory, readme-no-deps-claim, package-readme-deps-changelog: the netstandard2.0
    // assembly (loaded on .NET Framework) needs no System.Memory package; on .NET it is part of the framework.
    [Fact]
    public void NoRuntimeDependencies()
    {
#if NETFRAMEWORK
        Assert.DoesNotContain(typeof(JsonnetVm).Assembly.GetReferencedAssemblies(), a => a.Name == "System.Memory");
#endif
    }

    // shared-fileimporter-not-threadsafe
    [Fact]
    public void SharedFileImporterIsThreadSafe()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsonnetsharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const int files = 2000;
            for (int k = 0; k < files; k++)
            {
                File.WriteAllText(Path.Combine(dir, "f" + k), "{ v: " + k + " }");
            }
            FileImporter shared = new();
            using Barrier barrier = new(16);
            ConcurrentBag<Exception> errors = new();
            List<Thread> threads = Enumerable.Range(0, 16).Select(t => new Thread(() =>
            {
                barrier.SignalAndWait();
                for (int k = 0; k < files; k++)
                {
                    try
                    {
                        shared.Import("", Path.Combine(dir, "f" + ((k + t * 131) % files)), out _);
                    }
                    catch (Exception e)
                    {
                        errors.Add(e);
                    }
                }
            })).ToList();
            threads.ForEach(t => t.Start());
            threads.ForEach(t => t.Join());
            Assert.Empty(errors);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    private sealed class ThrowingWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;

        public Exception Error = new IOException("disk full");

        public override void Write(char value) => throw Error;

        public override void Write(string? value) => throw Error;
    }

    // crash-message-points-to-go-jsonnet
    [Fact]
    public void InternalErrorsPointAtThisProject()
    {
        JsonnetVm vm = new();
        vm.SetTraceOut(new ThrowingWriter());
        // Go discards std.trace write errors.
        Assert.Equal("1\n", vm.EvaluateAnonymousSnippet("x", "std.trace('hello', 1)"));

        MemoryImporter importer = new() { Data = { ["a"] = JsonnetContents.FromString("1") } };
        vm.Importer = importer;
        vm.EvaluateAnonymousSnippet("x", "import 'a'");
        importer.Data["a"] = JsonnetContents.FromString("2");
        string msg = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("x", "import 'a'")).Message;
        Assert.StartsWith("INTERNAL ERROR: (CRASH) importer problem: a different instance of Contents returned when importing \"a\" again\n", msg);
        Assert.EndsWith("\nPlease report a bug here: https://github.com/HanJaeJoon/JsonnetSharp/issues\n", msg);
    }

    // huge-string-raw-oom
    [Fact]
    public void StringBeyondDotNetLimitIsARuntimeError()
    {
        JsonnetRuntimeException e = Assert.Throws<JsonnetRuntimeException>(() =>
            Eval("local s = std.foldl(function(acc, i) acc + acc, std.range(1, 30), 'x'); std.md5(s)"));
        Assert.StartsWith("RUNTIME ERROR: string is too long for .NET (more than 1073741791 UTF-16 code units)\n\t", e.Message);
    }

    // parse-depth-no-location
    [Fact]
    public void ParseDepthErrorHasALocation() => OnOneMegabyte(() =>
    {
        for (int depth = 400; depth <= 2000; depth += 25)
        {
            string src = "std.length(" + Repeat("{a:", depth) + "1" + Repeat("}", depth) + ")";
            try
            {
                Program.SnippetToAST("min.jsonnet", "", src);
            }
            catch (StaticError e)
            {
                Assert.StartsWith("min.jsonnet:1:", e.Message);
            }
        }
        return 0;
    });

    // native-exception-inner-lost
    [Fact]
    public void NativeFunctionExceptionIsKept()
    {
        JsonnetVm vm = new();
        vm.NativeFunction(new JsonnetNativeFunction("f", Array.Empty<string>(), _ => throw new InvalidOperationException("plain failure")));
        vm.NativeFunction(new JsonnetNativeFunction("oom", Array.Empty<string>(), _ => throw new OutOfMemoryException("oom")));
        JsonnetRuntimeException e = Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "std.native('f')()"));
        Assert.Equal("RUNTIME ERROR: plain failure\n\tx:1:1-18\t$\n\tDuring evaluation\t\n", e.Message);
        Assert.IsType<InvalidOperationException>(e.InnerException);
        Assert.Throws<OutOfMemoryException>(() => vm.EvaluateAnonymousSnippet("x", "std.native('oom')()"));
    }

    // fileimporter-windows-oserror-text (only the NUL case differs from go on every OS)
    [Fact]
    public void ImportPathWithNulIsInvalidArgument() => Assert.Equal(
        "RUNTIME ERROR: open a\0b: invalid argument\n\tx:1:1-18\t$\n\tDuring evaluation\t\n",
        Assert.Throws<JsonnetRuntimeException>(() => new JsonnetVm().EvaluateAnonymousSnippet("x", "import 'a\\u0000b'")).Message);

    // ---- Round 2 ----

    private const string MaxStackFrames = "RUNTIME ERROR: max stack frames exceeded.";

    // cyclic-equality-gc-stall: comparison depth is capped instead of filling the 256 MB stack
    [Fact]
    public void ComparisonDepthIsCapped()
    {
        Assert.StartsWith(MaxStackFrames, EvalError("local d = std.foldl(function(a, i) [a], std.range(1, 100001), []); d == d"));
        Assert.StartsWith(MaxStackFrames, EvalError("local d = std.foldl(function(a, i) [a], std.range(1, 100001), []); d < d"));
        Assert.StartsWith(MaxStackFrames, EvalError("local o = {a: self}; o == o"));
    }

    // native-dup-param-crash
    [Fact]
    public void NativeFunctionParameterNamesAreValidated()
    {
        static object? F(IReadOnlyList<object?> a) => null;
        Assert.Equal("parameters", Assert.Throws<ArgumentException>(() => new JsonnetNativeFunction("f", new[] { "a", "a" }, F)).ParamName);
        Assert.Equal("parameters", Assert.Throws<ArgumentException>(() => new JsonnetNativeFunction("f", new[] { null!, "b" }, F)).ParamName);
        string[] names = { "a", "b" };
        JsonnetNativeFunction f = new("f", names, F);
        names[1] = "a";
        Assert.Equal(new[] { "a", "b" }, f.Parameters);
    }

    // stringoutput-lone-surrogate, stringoutput-raw-lone-surrogate
    [Fact]
    public void LoneSurrogatesFromTheHostBecomeReplacementCharacters()
    {
        JsonnetVm vm = new() { StringOutput = true };
        vm.ExtVar("x", "\uD800b");
        vm.TlaVar("t", "\uDC00");
        vm.NativeFunction(new JsonnetNativeFunction("n", Array.Empty<string>(), _ => new Dictionary<string, object?> { ["k\uD800"] = "x\uD800y" }));
        Assert.Equal("\uFFFDb\n", vm.EvaluateAnonymousSnippet("x", "std.extVar('x')"));
        Assert.Equal("x\uFFFDy\n", vm.EvaluateAnonymousSnippet("x", "std.native('n')()['k\uFFFD']"));
        Assert.Equal("RUNTIME ERROR: \uFFFDb\n\tx:1:1-22\t$\n\tDuring evaluation\t\n",
            Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "error std.extVar('x')")).Message);
        Assert.Equal("\uFFFD\n", vm.EvaluateAnonymousSnippet("x", "function(t) t"));
        vm.StringOutput = false;
        vm.TlaReset();
        Assert.Equal(new[] { "\uFFFDb" }, vm.EvaluateAnonymousSnippetMulti("x", "{[std.extVar('x')]: 'v'}").Keys);
    }

    private const string YamlNullKey = "RUNTIME ERROR: failed to parse YAML: error converting YAML to JSON: unsupported map key of type: %!s(<nil>), key: <nil>, value: ";

    // yaml-nan-key-goformat-crash
    [Fact]
    public void YamlNanKeyInErrorText()
    {
        Assert.Equal(YamlNullKey + "map[interface {}]interface {}{1:\"c\", NaN:\"b\", NaN:\"a\"}\n\t<cmdline>:1:1-48\t$\n\tDuring evaluation\t\n",
            EvalError("std.parseYaml(\"null: {.nan: b, .nan: a, 1: c}\")"));
        Assert.Equal("RUNTIME ERROR: failed to parse YAML: error converting YAML to JSON: yaml: invalid map key: map[interface {}]interface {}{NaN:1}\n" +
            "\t<cmdline>:1:1-34\t$\n\tDuring evaluation\t\n", EvalError("std.parseYaml('? {.nan: 1}\\n: x')"));
    }

    // yaml-goformat-type-order, gosyntax-map-key-type-order
    [Fact]
    public void YamlMixedKeyTypesInGoOrder() => Assert.Equal(
        YamlNullKey + "map[interface {}]interface {}{\"s\":\"d\", 0xffffffffffffffff:\"a\", 9223372036854775807:\"b\", 1.5:\"e\", true:\"c\"}\n" +
        "\t<cmdline>:1:1-96\t$\n\tDuring evaluation\t\n",
        EvalError("std.parseYaml('null: {18446744073709551615: a, 9223372036854775807: b, true: c, s: d, 1.5: e}')"));

    // yaml-goformat-string-order
    [Fact]
    public void YamlStringKeysInCodePointOrder() => Assert.Equal(
        YamlNullKey + "map[interface {}]interface {}{\"a\":3, \"\uFF41\":1, \"\U0001F600\":2}\n\t<cmdline>:1:1-75\t$\n\tDuring evaluation\t\n",
        EvalError("std.parseYaml(\"null:\\n  \\\"\\uff41\\\": 1\\n  \\\"\\ud83d\\ude00\\\": 2\\n  \\\"a\\\": 3\")"));

    // yaml-goformat-invalid-utf8, yaml-binary-invalid-utf8-lossy
    [Fact]
    public void YamlBinaryKeepsInvalidBytesUntilJson()
    {
        Assert.Equal("[\n   {\n      \"\uFFFD\": \"a\"\n   },\n   {\n      \"\uFFFD\": 1\n   }\n]\n",
            Eval("[std.parseYaml('!!binary /w==: a\\n!!binary /g==: b'), std.parseYaml('{!!binary /w==: 1, !!binary 77+9: 2}')]"));
        Assert.Equal(YamlNullKey + "\"\\xff\"\n\t<cmdline>:1:1-37\t$\n\tDuring evaluation\t\n", EvalError("std.parseYaml('null: !!binary /w==')"));
    }

    // net48-equalsignorecase-old-unicode-casing (failed only on .NET Framework before the fix)
    [Fact]
    public void EqualsIgnoreCaseUsesUnicode15() => Assert.Equal("[\n   true,\n   true,\n   true,\n   true,\n   true,\n   true,\n   true,\n   false\n]\n",
        Eval("[std.equalsIgnoreCase(a[0], a[1]) for a in [['\u10D0', '\u1C90'], ['\u13A0', '\uAB70'], ['\u0261', '\uA7AC'], ['\u0525', '\u0524'], " +
            "['\u023F', '\u2C7E'], ['\u037F', '\u03F3'], ['a', 'A'], ['a', 'b']]]"));

    // quote-isprint-runtime-unicode-version (each case failed on some runtime before the fix)
    [Fact]
    public void QuotedFieldNamesUseUnicode15()
    {
        static string Field(int cp) => EvalError("{ [std.char(" + cp + ")]: error 'boom' }").Split('\n')[2];
        Assert.Equal("\tField \"\U0001F970\"\t", Field(0x1F970));
        Assert.Equal("\tField \"\u0870\"\t", Field(0x870));
        Assert.Equal("\tField \"\\U0001cc00\"\t", Field(0x1CC00));
        Assert.Equal("\tField \"\\u00ad\"\t", Field(0xAD));
    }

#if NET8_0
    // The fixed tables are .NET 8's Unicode 15.0 data.
    [Fact]
    public void UnicodeTablesMatchDotNet8()
    {
        for (int cp = 0; cp <= 0x10FFFF; cp++)
        {
            if (cp >= 0xD800 && cp <= 0xDFFF)
            {
                continue;
            }
            string s = char.ConvertFromUtf32(cp);
            System.Globalization.UnicodeCategory c = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(cp);
            bool print = cp < 0x80 ? cp >= 0x20 && cp < 0x7F : c is not (System.Globalization.UnicodeCategory.SpaceSeparator or
                System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator or
                System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format or
                System.Globalization.UnicodeCategory.PrivateUse or System.Globalization.UnicodeCategory.OtherNotAssigned);
            Assert.True(print == UnicodeTables.IsPrint(cp), cp.ToString("X"));
            Assert.True(char.ConvertToUtf32(s.ToUpperInvariant(), 0) == UnicodeTables.ToUpper(cp), cp.ToString("X"));
            Assert.True(char.ConvertToUtf32(s.ToLowerInvariant(), 0) == UnicodeTables.ToLower(cp), cp.ToString("X"));
        }
    }
#endif

    // yaml-negzero-key-not-updated
    [Fact]
    public void YamlSignedZeroKeyKeepsTheLastSpelling() => Assert.Equal(
        "[\n   {\n      \"0\": \"b\"\n   },\n   {\n      \"-0\": \"b\"\n   },\n   {\n      \"0\": \"c\"\n   }\n]\n",
        Eval("[std.parseYaml('-0.0: a\\n0.0: b'), std.parseYaml('0.0: a\\n-0.0: b'), std.parseYaml('-0.0: {a: 1}\\n0.0: {b: 2}\\n<<: {0.0: c}')]"));

    // eval-thread-deadlock-caller-locks: EvaluationStackSize = 0 keeps callbacks on the calling thread
    [Fact]
    public void CallbacksCanRunOnTheCallingThread()
    {
        object gate = new();
        int caller = Environment.CurrentManagedThreadId;
        int inNative = 0;
        JsonnetVm vm = new() { EvaluationStackSize = 0 };
        vm.NativeFunction(new JsonnetNativeFunction("f", Array.Empty<string>(), _ =>
        {
            inNative = Environment.CurrentManagedThreadId;
            lock (gate)
            {
                return "in";
            }
        }));
        lock (gate)
        {
            Assert.Equal("\"in\"\n", vm.EvaluateAnonymousSnippet("x", "std.native('f')()"));
        }
        Assert.Equal(caller, inNative);
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.EvaluationStackSize = -1);
    }

    // eval-thread-orphaned-on-abort, interrupt-orphans-evaluation
    [Fact]
    public void InterruptedCallerStopsTheEvaluation()
    {
        int ticks = 0;
        JsonnetVm vm = new();
        vm.NativeFunction(new JsonnetNativeFunction("tick", Array.Empty<string>(), _ =>
        {
            Interlocked.Increment(ref ticks);
            Thread.Sleep(5);
            return true;
        }));
        const string src = "local f(n) = if n == 0 then 0 else if std.native('tick')() then f(n - 1) tailstrict else 0; f(100000)";
        Exception? caught = null;
        Thread t = new(() =>
        {
            try
            {
                vm.EvaluateAnonymousSnippet("x", src);
            }
            catch (Exception e)
            {
                caught = e;
            }
        });
        t.Start();
        while (Volatile.Read(ref ticks) < 5)
        {
            Thread.Sleep(1);
        }
        t.Interrupt();
        t.Join();
        Assert.IsType<ThreadInterruptedException>(caught);
        int atEnd = Volatile.Read(ref ticks);
        Thread.Sleep(300);
        Assert.Equal(atEnd, Volatile.Read(ref ticks));
        Assert.Equal("2\n", vm.EvaluateAnonymousSnippet("y", "1+1"));
    }

    // x86-address-space-64mb-per-eval, net48-full-stack-commit
    [Fact]
    public void DefaultEvaluationStackFitsTheRuntime()
    {
        // net48-thread-stack-commit, net48-commit-per-evaluation: .NET Framework commits the whole stack.
        int expected = IntPtr.Size == 4 ? 16 << 20 :
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal) ? 32 << 20 : 256 << 20;
        Assert.Equal(expected, new JsonnetVm().EvaluationStackSize);
    }

    // per-eval-thread-perf-regression, per-eval-thread-overhead: evaluations reuse the evaluation thread
    [Fact]
    public void EvaluationThreadIsReused()
    {
        List<int> ids = new();
        JsonnetVm vm = new();
        vm.NativeFunction(new JsonnetNativeFunction("id", Array.Empty<string>(), _ =>
        {
            ids.Add(Environment.CurrentManagedThreadId);
            return 1.0;
        }));
        for (int k = 0; k < 20; k++)
        {
            vm.EvaluateAnonymousSnippet("x", "std.native('id')()");
        }
        Assert.Single(ids.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, ids[0]);
    }

    private sealed class ThrowingImporter : IJsonnetImporter
    {
        public Exception Error = new IOException("disk gone");

        public JsonnetContents Import(string importedFrom, string importedPath, out string foundAt) => throw Error;
    }

    // importer-exception-inner-lost-oom-swallowed
    [Fact]
    public void ImporterExceptionIsKept()
    {
        ThrowingImporter importer = new();
        JsonnetVm vm = new() { Importer = importer };
        JsonnetRuntimeException e = Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "import 'a'"));
        Assert.Equal("RUNTIME ERROR: disk gone\n\tx:1:1-11\t$\n\tDuring evaluation\t\n", e.Message);
        Assert.Same(importer.Error, e.InnerException);
        Assert.Same(importer.Error, Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "importstr 'a'")).InnerException);
        Assert.Same(importer.Error, Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateFile("a")).InnerException);
        Assert.Same(importer.Error, Assert.Throws<JsonnetException>(() => vm.ImportData("", "a", out _)).InnerException);
        importer.Error = new OutOfMemoryException("oom");
        vm.Importer = importer;
        Assert.Throws<OutOfMemoryException>(() => vm.EvaluateAnonymousSnippet("x", "import 'a'"));
    }

    // finddeps-null-element-not-ane
    [Fact]
    public void FindDependenciesRejectsNullElement() =>
        Assert.Equal("importedPaths", Assert.Throws<ArgumentNullException>(() => new JsonnetVm().FindDependencies("", new string[] { null! })).ParamName);

    // jpaths-null-entry-nre-text
    [Fact]
    public void NullJPathsEntryActsAsEmpty() => Assert.Equal(
        "RUNTIME ERROR: couldn't open import \"nonexist\": no match locally or in the Jsonnet library paths\n\tx:1:1-18\t$\n\tDuring evaluation\t\n",
        Assert.Throws<JsonnetRuntimeException>(() => new JsonnetVm { Importer = new FileImporter { JPaths = new List<string> { null! } } }
            .EvaluateAnonymousSnippet("x", "import 'nonexist'")).Message);

    // apache-attribution-missing
    [Fact]
    public void CopyrightNamesGoJsonnetAuthors() => Assert.Contains("Google Inc.",
        ((System.Reflection.AssemblyCopyrightAttribute)Attribute.GetCustomAttribute(typeof(JsonnetVm).Assembly, typeof(System.Reflection.AssemblyCopyrightAttribute))!).Copyright);

    // nested-literal-quadratic-memory: context names are built lazily, so parsing deep nesting is linear
    [Fact]
    public void DeepNestingParsesInLinearMemory() => TestUtils.OnStack(256 << 20, () =>
    {
        long before = GC.GetAllocatedBytesForCurrentThread();
        Program.SnippetToAST("x", "", "std.length(" + new string('[', 10000) + new string(']', 10000) + ")");
        Program.SnippetToAST("x", "", "local f(x) = 1; " + Repeat("f(", 10000) + "1" + new string(')', 10000));
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // The eager "thunk from <...>" strings took over 1 GB each.
        Assert.True(allocated < 200L << 20, allocated.ToString());
        return 0;
    });

    // huge-string-raw-oom-remaining-paths
    [Fact]
    public void StringBuildersStopAtTheDotNetLimit()
    {
        JsonnetRuntimeException e = Assert.Throws<JsonnetRuntimeException>(() =>
            Eval("local s = std.foldl(function(acc, i) acc + acc, std.range(1, 28), 'a'); std.length(std.strReplace(s, 'a', 'xxxxx'))"));
        Assert.StartsWith("RUNTIME ERROR: string is too long for .NET (more than 1073741791 UTF-16 code units)\n\t", e.Message);
    }

    // ---- Round 3 ----

    // eval-stack-size-small-process-crash, ess-small-value-process-crash: a stack too small to evaluate on is
    // rejected instead of killing the process.
    [Fact]
    public void EvaluationStackSizeHasAFloor()
    {
        JsonnetVm vm = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.EvaluationStackSize = 1);
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.EvaluationStackSize = 65536);
        Assert.Throws<ArgumentOutOfRangeException>(() => vm.EvaluationStackSize = (1 << 20) - 1);
        vm.EvaluationStackSize = 1 << 20;
        Assert.Equal("{\n   \"a\": 1\n}\n", vm.EvaluateAnonymousSnippet("x", "{a: 1}"));
    }

    // string-concat-2g-overflow-crash: go prints 2147483648; .NET cannot hold that many code points.
    [Fact]
    public void ConcatBeyondIntRangeIsARuntimeError()
    {
        const string k = "local a = std.repeat('x', 1048576); local b = a + a; local c = b + b; local d = c + c; local e = d + d; " +
            "local f = e + e; local g = f + f; local h = g + g; local i = h + h; local j = i + i; local k = j + j; ";
        JsonnetRuntimeException e = Assert.Throws<JsonnetRuntimeException>(() => Eval(k + "std.length(k + k)"));
        Assert.StartsWith("RUNTIME ERROR: string is too long for .NET (more than 1073741791 UTF-16 code units)\n\t", e.Message);
        Assert.Equal("1610612736\n", Eval(k + "std.length(j + k)"));
    }

    // fileimporter-os-error-localized, fileimporter-io-error-text: go's "open <path>: <English Win32 text>"
    [Fact]
    public void FileImporterReportsOsErrorsLikeGo()
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            return;
        }
        string path = Path.Combine(Path.GetTempPath(), "jsonnetsharp-" + Guid.NewGuid().ToString("N") + ".libsonnet").Replace('\\', '/');
        File.WriteAllText(path, "1");
        System.Globalization.CultureInfo ui = System.Globalization.CultureInfo.CurrentUICulture;
        try
        {
            System.Globalization.CultureInfo.CurrentUICulture = new System.Globalization.CultureInfo("ko-KR");
            using FileStream hold = new(path, FileMode.Open, FileAccess.Read, FileShare.None);
            Assert.Equal("RUNTIME ERROR: open " + path + ": The process cannot access the file because it is being used by another process.\n" +
                "\tx:1:1-" + (13 + path.Length) + "\t$\n\tDuring evaluation\t\n",
                Assert.Throws<JsonnetRuntimeException>(() => new JsonnetVm().EvaluateAnonymousSnippet("x", "importstr '" + path + "'")).Message);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = ui;
            File.Delete(path);
        }
    }

    // crash-message-mixed-newlines
    [Fact]
    public void CrashMessageUsesLineFeedsOnly()
    {
        string msg = EvalError("std.removeAt([1,2,3], -1)");
        Assert.StartsWith("INTERNAL ERROR: (CRASH) runtime error: slice bounds out of range [:-1]\n", msg);
        Assert.DoesNotContain("\r", msg);
    }

    // trace-writer-exception-internal-crash: go discards every trace write error
    [Fact]
    public void TraceWriterFailuresAreIgnored()
    {
        ThrowingWriter writer = new() { Error = new NotSupportedException("read-only") };
        JsonnetVm vm = new();
        vm.SetTraceOut(writer);
        Assert.Equal("1\n", vm.EvaluateAnonymousSnippet("x", "std.trace('m', 1)"));
        writer.Error = new InvalidOperationException("broken");
        Assert.Equal("1\n", vm.EvaluateAnonymousSnippet("x", "std.trace('m', 1)"));
    }

    // native-cyclic-result-slow: converting a native result is depth-capped like comparisons
    [Fact]
    public void CyclicNativeResultFailsFast()
    {
        Dictionary<string, object?> d = new();
        d["me"] = d;
        JsonnetVm vm = new();
        vm.NativeFunction(new JsonnetNativeFunction("cd", Array.Empty<string>(), _ => d));
        static List<object?> Nest(int depth)
        {
            List<object?> v = new();
            for (int k = 1; k < depth; k++)
            {
                v = new List<object?> { v };
            }
            return v;
        }
        vm.NativeFunction(new JsonnetNativeFunction("deep", new[] { "n" }, a => Nest((int)(double)a[0]!)));
        // Filling the 256 MB stack took 10 to 50 s; the depth is capped as for comparisons.
        Assert.StartsWith(MaxStackFrames, Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "std.native('cd')()")).Message);
        Assert.StartsWith(MaxStackFrames, Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "std.native('deep')(100001)")).Message);
        Assert.Equal("true\n", vm.EvaluateAnonymousSnippet("x", "std.isArray(std.native('deep')(10000))"));
    }

    // tostring-raw-oom: go formats "Field %#v" for every manifested field; here it is formatted only for an
    // error (a key of 2^28 U+00AD quotes to 1.6G chars, past .NET's limit, while its JSON fits).
    [Fact]
    public void FieldTraceLabelIsBuiltOnlyOnError() => TestUtils.OnStack(TestUtils.OneMegabyte, () =>
    {
        JsonnetVm vm = new() { EvaluationStackSize = 0 };
        long Cost(string ch)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            Assert.Equal("1048583\n", vm.EvaluateAnonymousSnippet("x", "local d(s, n) = if n == 0 then s else d(s + s, n - 1); std.length(std.toString({ [d('" + ch + "', 20)]: 1 }))"));
            return GC.GetAllocatedBytesForCurrentThread() - before;
        }
        Cost("a");
        long plain = Cost("a");
        long shy = Cost("\\u00ad");
        Assert.True(shy < plain + plain / 4, plain + " " + shy);
        Assert.Equal("RUNTIME ERROR: e\n\t<cmdline>:1:9-18\tobject <anonymous>\n\tField \"\\u00ad\"\t\n\tDuring manifestation\t\n",
            EvalError("{ '\u00ad': error 'e' }"));
        return 0;
    });

    // invalid-utf8-column-shift: the lexer reads the file's bytes, so an invalid byte is one column, as in go
    [Fact]
    public void InvalidUtf8SourceKeepsGoColumns()
    {
        static JsonnetContents Raw(string s) => JsonnetContents.FromBytes(s.Select(c => (byte)c).ToArray());
        MemoryImporter importer = new()
        {
            Data =
            {
                ["v3.jsonnet"] = Raw("'\u00ff' + error 'e'\n"),
                ["v8.jsonnet"] = Raw("[std.length('\u00e2\u0082A'), std.length(|||\n  \u00e2\u0082A\n|||)]\n"),
            },
        };
        JsonnetVm vm = new() { Importer = importer };
        Assert.Equal("RUNTIME ERROR: e\n\tv3.jsonnet:1:7-16\t$\n\tDuring evaluation\t\n",
            Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateFile("v3.jsonnet")).Message);
        Assert.Equal("[\n   3,\n   4\n]\n", vm.EvaluateFile("v8.jsonnet"));
    }

    // ---- Round 4 ----

    // yaml-json-depth-not-enforced: sigs.k8s.io/yaml's JSON round trip caps block and flow nesting together
    [Fact]
    public void YamlDeeperThanJsonAllowsIsRejected()
    {
        const string prefix = "RUNTIME ERROR: failed to parse YAML: error unmarshaling JSON: while decoding JSON: invalid character ";
        Assert.StartsWith(prefix + "'[' exceeded max depth\n\t<cmdline>:1:",
            EvalError("std.parseYaml('a:\\n  ' + std.repeat('[', 10000) + std.repeat(']', 10000))"));
        Assert.StartsWith(prefix + "'{' exceeded max depth\n\t<cmdline>:1:",
            EvalError("std.parseYaml('a:\\n  ' + std.repeat('{b: ', 10000) + '1' + std.repeat('}', 10000))"));
        Assert.StartsWith("RUNTIME ERROR: failed to parse YAML: error converting YAML to JSON: json: unsupported value: NaN\n",
            EvalError("std.parseYaml('a:\\n  ' + std.repeat('[', 10000) + std.repeat(']', 10000) + '\\nb: .nan')"));
        Assert.Equal("1\n", Eval("std.length(std.parseYaml('a:\\n  ' + std.repeat('[', 9999) + std.repeat(']', 9999)))"));
    }

    // native-result-conversion-crash
    [Fact]
    public void NativeResultReadErrorIsARuntimeError()
    {
        JsonnetVm vm = new();
        vm.NativeFunction(new JsonnetNativeFunction("f", Array.Empty<string>(), _ => new List<object?> { new CountThrowList { 1.0 } }));
        JsonnetRuntimeException e = Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", "std.native('f')()"));
        Assert.Equal("RUNTIME ERROR: count boom\n\tx:1:1-18\t$\n\tDuring evaluation\t\n", e.Message);
        Assert.IsType<InvalidOperationException>(e.InnerException);
    }

    private sealed class CountThrowList : List<object?>, IList<object?>
    {
        int ICollection<object?>.Count => throw new InvalidOperationException("count boom");
    }

    // memoryimporter-null-importedfrom
    [Fact]
    public void MemoryImporterRejectsNullArguments()
    {
        MemoryImporter m = new() { Data = { ["k"] = JsonnetContents.FromString("1") } };
        Assert.Equal("importedFrom", Assert.Throws<ArgumentNullException>(() => m.Import(null!, "k", out _)).ParamName);
        Assert.Equal("importedPath", Assert.Throws<ArgumentNullException>(() => m.Import("", null!, out _)).ParamName);
    }

    // public-api-go-naming (ImportData): go's string carries the raw bytes
    [Fact]
    public void ImportDataKeepsRawBytes()
    {
        byte[] bytes = { 0xFF, 0x41, 0xEF, 0xBB, 0xBF };
        JsonnetVm vm = new() { Importer = new MemoryImporter { Data = { ["b"] = JsonnetContents.FromBytes(bytes) } } };
        Assert.Equal(bytes, vm.ImportData("", "b", out string foundAt).Data);
        Assert.Equal("b", foundAt);
    }

    // net48-long-path-import-not-found: go adds the \\?\ prefix to long paths itself
    [Fact]
    public void FileImporterReadsLongPaths()
    {
        if (Path.DirectorySeparatorChar != '\\')
        {
            return;
        }
        string root = Path.Combine(Path.GetTempPath(), "jsonnetsharp-" + Guid.NewGuid().ToString("N"));
        string dir = root;
        for (int k = 0; k < 8; k++)
        {
            dir = Path.Combine(dir, "abcdefghijklmnopqrstuvwxyz0123456789");
        }
        string file = Path.Combine(dir, "deep.libsonnet");
        Directory.CreateDirectory(@"\\?\" + dir);
        File.WriteAllText(@"\\?\" + file, "{ deep: true }");
        try
        {
            const string expected = "{\n   \"deep\": true\n}\n";
            Assert.Equal(expected, new JsonnetVm().EvaluateFile(file));
            Assert.Equal(expected, new JsonnetVm().EvaluateAnonymousSnippet("x", "import '" + file.Replace('\\', '/') + "'"));
            JsonnetVm vm = new() { Importer = new FileImporter { JPaths = { dir } } };
            Assert.Equal(expected, vm.EvaluateAnonymousSnippet("x", "import 'deep.libsonnet'"));
        }
        finally
        {
            Directory.Delete(@"\\?\" + root, true);
        }
    }
}

[CollectionDefinition(nameof(SerialCollection), DisableParallelization = true)]
public class SerialCollection
{
}

[Collection(nameof(SerialCollection))]
public class SerialDogfoodTests
{
    // splitlimitr-quadratic: copying the rest of the string on every split made this quadratic. Serial: timing.
    // Compares two sizes instead of a wall-clock limit: 4x the input takes ~4x as long when linear, ~16x when quadratic.
    [Fact]
    public void SplitLimitRIsLinear()
    {
        static TimeSpan Time(int n)
        {
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            Assert.Equal((n + 1) + "\n", new JsonnetVm().EvaluateAnonymousSnippet("x",
                "std.length(std.splitLimitR(std.repeat('a,', " + n + "), ',', -1))"));
            return sw.Elapsed;
        }

        Time(20000);
        TimeSpan small = Time(100000), large = Time(400000);
        Assert.True(large.Ticks < (small.Ticks * 8) + TimeSpan.FromSeconds(1).Ticks, small + " vs " + large);
        Assert.Equal("[\n   \"a,b\",\n   \"c\",\n   \"d\"\n]\n",
            new JsonnetVm().EvaluateAnonymousSnippet("x", "std.splitLimitR('a,b,c,d', ',', 2)"));
    }

    // x86-concurrent-eval-oom-crash: past the stack budget (512 MB in a 32-bit process) an evaluation runs on the
    // calling thread instead of reserving address space until the process dies. Serial: it lowers the budget.
    [Fact]
    public void EvaluationPastTheStackBudgetRunsOnTheCaller()
    {
        long limit = EvaluationThread.ReserveLimit;
        EvaluationThread.ReserveLimit = 0;
        try
        {
            int inNative = 0;
            // A size no pooled thread has.
            JsonnetVm vm = new() { EvaluationStackSize = (3 << 20) + 4096 };
            vm.NativeFunction(new JsonnetNativeFunction("id", Array.Empty<string>(), _ =>
            {
                inNative = Environment.CurrentManagedThreadId;
                return 1.0;
            }));
            Assert.Equal("1\n", vm.EvaluateAnonymousSnippet("x", "std.native('id')()"));
            Assert.Equal(Environment.CurrentManagedThreadId, inNative);
        }
        finally
        {
            EvaluationThread.ReserveLimit = limit;
        }
    }

    // utf8-over-2gb-internal-crash: 738197504 three-byte code points are over 2 GB of UTF-8. Serial: several GB.
    [Fact]
    public void HugeUtf8StringsHashAndFailCleanly()
    {
        if (System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal))
        {
            // Its code points (a 3 GB int[]) pass the 2 GB object limit without gcAllowVeryLargeObjects.
            return;
        }
        const string y = "local d(s, n) = if n == 0 then s else d(s + s, n - 1); local a = d('\\u4e2d', 26); " +
            "local c = a + a + a + a; local y = c + c + a + a + a; ";
        JsonnetVm vm = new();
        Assert.Equal("\"cea9eee6f97ae1aa98f8ca38d7a6f8c2\"\n", vm.EvaluateAnonymousSnippet("x", y + "std.md5(y)"));
        Assert.StartsWith("RUNTIME ERROR: string is too long for .NET (its UTF-8 form is more than 2147483647 bytes)\n\t",
            Assert.Throws<JsonnetRuntimeException>(() => vm.EvaluateAnonymousSnippet("x", y + "std.parseYaml(y)")).Message);
    }

    // interrupt-latency-whole-builtin: std.makeArray(3e7) ran for 5 s after the interrupt. Serial: timing.
    [Fact]
    public void InterruptStopsLongBuiltins()
    {
        foreach (string code in new[] { "std.length(std.makeArray(30000000, function(i) i))", "std.length(std.range(1, 30000000))" })
        {
            Exception? error = null;
            System.Diagnostics.Stopwatch sw = new();
            Thread t = new(() =>
            {
                try
                {
                    new JsonnetVm().EvaluateAnonymousSnippet("x", code);
                }
                catch (Exception e)
                {
                    error = e;
                }
                sw.Stop();
            });
            t.Start();
            Thread.Sleep(200);
            sw.Start();
            t.Interrupt();
            t.Join();
            Assert.IsType<ThreadInterruptedException>(error);
            Assert.True(sw.ElapsedMilliseconds < 5000, code + ": " + sw.ElapsedMilliseconds + " ms");
        }
    }
}
