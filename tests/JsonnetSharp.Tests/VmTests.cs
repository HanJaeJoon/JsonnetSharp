using System.Text;

namespace JsonnetSharp.Tests;

// Expected texts were produced by the go-jsonnet 0.22.0 binary (jsonnet -e ...).
public class VmTests
{
    private static string Eval(string code) => new JsonnetVm().EvaluateAnonymousSnippet("<cmdline>", code);

    private static string EvalError(string code) =>
        Assert.ThrowsAny<JsonnetException>(() => Eval(code)).Message;

    [Theory]
    [InlineData("null", "null\n")]
    [InlineData("true", "true\n")]
    [InlineData("'a\\u0001\\u007f\\u0080é😀\"'", "\"a\\u0001\\u007f\\u0080é😀\\\"\"\n")]
    [InlineData("[]", "[ ]\n")]
    [InlineData("{}", "{ }\n")]
    [InlineData("[[]]", "[\n   [ ]\n]\n")]
    [InlineData("{a: 1, b: [], c: {d: null}}", "{\n   \"a\": 1,\n   \"b\": [ ],\n   \"c\": {\n      \"d\": null\n   }\n}\n")]
    [InlineData("-0", "-0\n")]
    [InlineData("1e20", "100000000000000000000\n")]
    [InlineData("0.1", "0.10000000000000001\n")]
    [InlineData("1/3", "0.33333333333333331\n")]
    [InlineData("123456789012345680000", "123456789012345683968\n")]
    [InlineData("1e-7", "9.9999999999999995e-08\n")]
    [InlineData("2e-300", "2.0000000000000001e-300\n")]
    [InlineData("5e-324", "4.9406564584124654e-324\n")]
    [InlineData("1e-400", "0\n")]
    [InlineData("0.5", "0.5\n")]
    [InlineData("1.5e300 / 1e290", "15000000000\n")]
    public void Literals(string code, string expected) => Assert.Equal(expected, Eval(code));

    [Fact]
    public void HugeIntegerIsPrintedExactly() => Assert.Equal(
        "1000000000000000052504760255204420248704468581108159154915854115511802457988908195786371375080447864043704443832883878176942523235360430575644792184786706982848387200926575803737830233794788090059368953234970799945081119038967640880074652742780142494579258788820056842838115669472196386865459400540160\n",
        Eval("1e300"));

    [Theory]
    [InlineData("{b: 2, a: 1, 'é': 0, '😀': 0, '￿': 0, h:: 3}", "{\n   \"a\": 1,\n   \"b\": 2,\n   \"é\": 0,\n   \"￿\": 0,\n   \"😀\": 0\n}\n")]
    [InlineData("local o = {a: 1, b: self.a + 1}; o { a: 10 }", "{\n   \"a\": 10,\n   \"b\": 11\n}\n")]
    [InlineData("{a: {x: 1}} + {a+: {y: 2}}", "{\n   \"a\": {\n      \"x\": 1,\n      \"y\": 2\n   }\n}\n")]
    [InlineData("{a: 1} + {a: super.a + 1, b:: 5} + {c: self.b}", "{\n   \"a\": 2,\n   \"c\": 5\n}\n")]
    [InlineData("{a:: 1} + {a: 2}", "{ }\n")]
    [InlineData("{a:: 1} + {a::: 2}", "{\n   \"a\": 2\n}\n")]
    [InlineData("local x = {a: 1}; 'a' in x && !('b' in x) && ('a' in {a: 1} + {})", "true\n")]
    [InlineData("{[k]: k + '!' for k in ['x', 'y']}", "{\n   \"x\": \"x!\",\n   \"y\": \"y!\"\n}\n")]
    [InlineData("{local l = 3, a: l, assert self.a == 3}", "{\n   \"a\": 3\n}\n")]
    [InlineData("std.objectFields({b: 1, a: 2, c:: 3})", "[\n   \"a\",\n   \"b\"\n]\n")]
    [InlineData("std.objectRemoveKey({a: 1, b: 2}, 'a')", "{\n   \"b\": 2\n}\n")]
    public void Objects(string code, string expected) => Assert.Equal(expected, Eval(code));

    [Theory]
    [InlineData("local f(x, y=10) = x + y; [f(1), f(1, 2), f(y=3, x=1)]", "[\n   11,\n   3,\n   4\n]\n")]
    [InlineData("local fib(n) = if n < 2 then n else fib(n - 1) + fib(n - 2); fib(15)", "610\n")]
    [InlineData("[x * x for x in [1, 2, 3] if x != 2]", "[\n   1,\n   9\n]\n")]
    [InlineData("std.length([1, 2]) + std.length('héllo') + std.length({a: 1})", "8\n")]
    [InlineData("std.makeArray(3, function(i) i * 2)", "[\n   0,\n   2,\n   4\n]\n")]
    [InlineData("std.map(function(x) x + 1, [1, 2])", "[\n   2,\n   3\n]\n")]
    [InlineData("'abc'[1] + [1, 2, 3][2:] + 'x' + 1 + true + null + [1, 'a'] + {a: 1}", "\"b[3]x1truenull[1, \\\"a\\\"]{\\\"a\\\": 1}\"\n")]
    [InlineData("[7 % 3, -7 % 3, 1 << 3, 5 & 3, 5 | 3, 5 ^ 3, ~5, -(2), +3, !true, 3 >= 3, 'a' < 'b', [1, 2] < [1, 3]]",
        "[\n   1,\n   -1,\n   8,\n   1,\n   7,\n   6,\n   -6,\n   -2,\n   3,\n   false,\n   true,\n   true,\n   true\n]\n")]
    [InlineData("[1, [2], {a: [3]}] == [1, [2], {a: [3]}]", "true\n")]
    [InlineData("local s = std.foldl(function(a, b) a + b, std.makeArray(100, function(i) 'x'), ''); std.length(s)", "100\n")]
    [InlineData("std.type(std.native('nope'))", "\"null\"\n")]
    [InlineData("local f(n) = if n == 0 then 'done' else f(n - 1) tailstrict; f(200)", "\"done\"\n")]
    public void Functions(string code, string expected) => Assert.Equal(expected, Eval(code));

    [Fact]
    public void DeepStringTreeFlattensWithoutRecursion()
    {
        ValueString s = new ValueFlatString(new string('a', 40));
        for (int k = 0; k < 200000; k++)
        {
            s = Builtins.ConcatStrings(s, new ValueFlatString("😀"));
        }
        Assert.Equal(200040, s.Length());
        Assert.Equal(200040 + 200000, s.GetGoString().Length);
        Assert.Equal(0x1F600, s.GetRunes()[200039]);
    }

    [Theory]
    [InlineData("error 'boom'", "RUNTIME ERROR: boom\n\t<cmdline>:1:1-13\t$\n\tDuring evaluation\t\n")]
    [InlineData("local f(x) = if x == 0 then error \"boom\" else f(x-1) + 1; f(3)",
        "RUNTIME ERROR: boom\n\t<cmdline>:1:29-41\tfunction <f>\n\t<cmdline>:1:47-53\tfunction <f>\n\t<cmdline>:1:47-53\tfunction <f>\n\t<cmdline>:1:47-53\tfunction <f>\n\t<cmdline>:1:59-63\t$\n\tDuring evaluation\t\n")]
    [InlineData("[1,2,3][1e20]", "RUNTIME ERROR: Index -9223372036854775808 out of bounds, not within [0, 3)\n\t<cmdline>:1:1-14\t$\n\tDuring evaluation\t\n")]
    [InlineData("1e400", "RUNTIME ERROR: overflow\n\t<cmdline>:1:1-6\t$\n\tDuring evaluation\t\n")]
    [InlineData("{a: error \"x\"}.a", "RUNTIME ERROR: x\n\t<cmdline>:1:5-14\t\n\t<cmdline>:1:1-17\t$\n\tDuring evaluation\t\n")]
    [InlineData("\"abc\"[-1]", "RUNTIME ERROR: Index -1 out of bounds, not within [0, 3)\n\t<cmdline>:1:1-10\t$\n\tDuring evaluation\t\n")]
    [InlineData("{a: 1 +}", "<cmdline>:1:8-9 Unexpected: \"}\" while parsing terminal\n\n{a: 1 +}\n\n")]
    [InlineData("{a: function() 1}", "RUNTIME ERROR: couldn't manifest function as JSON\n\tField \"a\"\t\n\tDuring manifestation\t\n")]
    [InlineData("{} + 1", "RUNTIME ERROR: Unexpected type number, expected object\n\t<cmdline>:1:1-7\t$\n\tDuring evaluation\t\n")]
    [InlineData("{['a']: 1, ['a']: 2}", "RUNTIME ERROR: Duplicate field name: \"a\"\n\t<cmdline>:1:1-21\t$\n\tDuring evaluation\t\n")]
    [InlineData("{[k]: 1 for k in ['a','a']}", "RUNTIME ERROR: Duplicate field name: \"a\"\n\t<cmdline>:1:1-28\t\n\tDuring evaluation\t\n")]
    [InlineData("[x for x in {}]", "RUNTIME ERROR: Unexpected type object, expected array\n\t<cmdline>:1:1-16\t\n\tDuring evaluation\t\n")]
    [InlineData("std.native('x')(1)", "RUNTIME ERROR: Unexpected type null, expected function\n\t<cmdline>:1:1-19\t$\n\tDuring evaluation\t\n")]
    [InlineData("std.extVar('nope')", "RUNTIME ERROR: Undefined external variable: nope\n\t<cmdline>:1:1-19\t$\n\tDuring evaluation\t\n")]
    [InlineData("local f(x) = x; f(1, 2)", "RUNTIME ERROR: function expected 1 positional argument(s), but got 2\n\t<cmdline>:1:17-24\t$\n\tDuring evaluation\t\n")]
    [InlineData("{assert false : 'no'}", "RUNTIME ERROR: no\n\t<cmdline>:1:2-21\t\n\tChecking object assertions\t\n\tDuring manifestation\t\n")]
    [InlineData("1 / 0", "RUNTIME ERROR: Division by zero.\n\t<cmdline>:1:1-6\t$\n\tDuring evaluation\t\n")]
    [InlineData("true.x", "RUNTIME ERROR: Value non indexable: *jsonnet.valueBoolean\n\t<cmdline>:1:1-7\t$\n\tDuring evaluation\t\n")]
    [InlineData("import 'nope.jsonnet'", "RUNTIME ERROR: couldn't open import \"nope.jsonnet\": no match locally or in the Jsonnet library paths\n\t<cmdline>:1:1-22\t$\n\tDuring evaluation\t\n")]
    public void Errors(string code, string expected) => Assert.Equal(expected, EvalError(code));

    [Fact]
    public void MaxStackError()
    {
        string msg = EvalError("local f(x) = f(x+1) + 1; f(0)");
        string frame = "\t<cmdline>:1:14-20\tfunction <f>\n";
        string expected = "RUNTIME ERROR: max stack frames exceeded.\n" + string.Concat(Enumerable.Repeat(frame, 10)) + "\t...\n" +
            string.Concat(Enumerable.Repeat(frame, 8)) + "\t<cmdline>:1:26-30\t$\n\tDuring evaluation\t\n";
        Assert.Equal(expected, msg);
    }

    [Fact]
    public void ErrorTypes()
    {
        Assert.IsType<JsonnetRuntimeException>(Assert.ThrowsAny<JsonnetException>(() => Eval("error 'x'")));
        Assert.IsType<JsonnetStaticException>(Assert.ThrowsAny<JsonnetException>(() => Eval("{")));
    }

    // Runs body on a thread with a 1 MB stack, the .NET Framework / IIS default.
    private static void OnOneMegabyteStack(Action body)
    {
        Exception? error = null;
        Thread t = new(() =>
        {
            try
            {
                body();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, 1024 * 1024);
        t.Start();
        t.Join();
        Assert.Null(error);
    }

    // The largest n that go-jsonnet 0.22.0 accepts with MaxStack = 500, per recursion shape.
    [Theory]
    [InlineData("local f(n) = if n == 0 then 0 else 1 + f(n - 1); f(N)", 497)]
    [InlineData("local f(n) = local m = n - 1; if n == 0 then 0 else 1 + f(m); f(N)", 496)]
    [InlineData("local f(n) = if n == 0 then {v: 0} else {v: f(n - 1).v + 1}; f(N).v", 497)]
    [InlineData("local f(n) = if n == 0 then [] else [f(n - 1)]; f(N)", 497)]
    [InlineData("local arr = std.makeArray(N, function(i) if i == 0 then 0 else arr[i - 1] + 1); arr[N - 1]", 249)]
    public void RecursionUpToMaxStackFitsInOneMegabyteStack(string template, int max) => OnOneMegabyteStack(() =>
    {
        Eval(template.Replace("N", max.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        string msg = EvalError(template.Replace("N", (max + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.StartsWith("RUNTIME ERROR: max stack frames exceeded.\n", msg);
    });

    [Fact]
    public void TailstrictRecursionDoesNotUseTheNativeStack() => OnOneMegabyteStack(() =>
        // go-jsonnet trims tailstrict frames from the Jsonnet stack, so this is not limited by MaxStack.
        Assert.Equal("\"done\"\n", Eval("local f(n) = if n == 0 then 'done' else f(n - 1) tailstrict; f(100000)")));

    [Fact]
    public void DeepNativeRecursionIsAnErrorNotACrash() => OnOneMegabyteStack(() =>
    {
        string msg = Assert.ThrowsAny<JsonnetException>(() => new JsonnetVm { MaxStack = 10000000 }
            .EvaluateAnonymousSnippet("x", "local f(n) = if n == 0 then 0 else 1 + f(n - 1); f(1000000)")).Message;
        Assert.StartsWith("RUNTIME ERROR: max stack frames exceeded.\n\tx:1:", msg);
    });

    [Fact]
    public void Imports()
    {
        JsonnetVm vm = new()
        {
            Importer = new MemoryImporter
            {
                Data =
                {
                    ["lib.libsonnet"] = JsonnetContents.FromString("{ x: 1, file: std.thisFile }"),
                    ["data.txt"] = JsonnetContents.FromString("héllo"),
                    ["bin"] = JsonnetContents.FromBytes(new byte[] { 0, 255 }),
                    ["main.jsonnet"] = JsonnetContents.FromString("local lib = import 'lib.libsonnet'; [lib.x, lib.file, importstr 'data.txt', importbin 'bin', std.thisFile]"),
                    ["bad.jsonnet"] = JsonnetContents.FromString("{"),
                },
            },
        };
        Assert.Equal("[\n   1,\n   \"lib.libsonnet\",\n   \"héllo\",\n   [\n      0,\n      255\n   ],\n   \"main.jsonnet\"\n]\n", vm.EvaluateFile("main.jsonnet"));
        Assert.Equal("[\n   1,\n   \"lib.libsonnet\",\n   \"héllo\",\n   [\n      0,\n      255\n   ],\n   \"\"\n]\n",
            vm.EvaluateAnonymousSnippet("snippet", "local lib = import 'lib.libsonnet'; [lib.x, lib.file, importstr 'data.txt', importbin 'bin', std.thisFile]"));
        Assert.Equal("lib.libsonnet", vm.ResolveImport("", "lib.libsonnet"));
        Assert.Equal("héllo", vm.ImportData("", "data.txt", out string foundAt).ToString());
        Assert.Equal("data.txt", foundAt);

        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("snippet", "import 'missing'"));
        Assert.Equal("RUNTIME ERROR: import not available missing\n\tsnippet:1:1-17\t$\n\tDuring evaluation\t\n", e.Message);
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("snippet", "import 'bad.jsonnet'"));
        Assert.Equal("RUNTIME ERROR: bad.jsonnet:1:2 Unexpected: end of file while parsing field definition\n\tsnippet:1:1-21\t$\n\tDuring evaluation\t\n", e.Message);
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateFile("missing"));
        Assert.Equal("INTERNAL ERROR: import not available missing\nPlease report a bug here: https://github.com/HanJaeJoon/JsonnetSharp/issues\n", e.Message);
        Assert.Equal(new[] { "bin", "data.txt", "lib.libsonnet" }, vm.FindDependencies("", new[] { "main.jsonnet" }));
    }

    [Fact]
    public void FileImporterUsesJPaths()
    {
        string dir = Path.Combine(Path.GetTempPath(), "jsonnetsharp-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "lib", "a.libsonnet"), "{ a: 1 }");
            File.WriteAllText(Path.Combine(dir, "main.jsonnet"), "(import 'a.libsonnet') + { b: 2 }");
            JsonnetVm vm = new() { Importer = new FileImporter { JPaths = { Path.Combine(dir, "lib") } } };
            Assert.Equal("{\n   \"a\": 1,\n   \"b\": 2\n}\n", vm.EvaluateFile(Path.Combine(dir, "main.jsonnet")));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ExtVarsAndTlas()
    {
        JsonnetVm vm = new();
        vm.ExtVar("s", "str");
        vm.ExtCode("c", "{a: 1 + 1}");
        Assert.Equal("[\n   \"str\",\n   {\n      \"a\": 2\n   }\n]\n", vm.EvaluateAnonymousSnippet("x", "[std.extVar('s'), std.extVar('c')]"));

        vm.TlaVar("a", "A");
        vm.TlaCode("b", "[1]");
        Assert.Equal("[\n   \"A\",\n   [\n      1\n   ],\n   3\n]\n", vm.EvaluateAnonymousSnippet("x", "function(a, b, c=3) [a, b, c]"));
        Assert.Equal("1\n", vm.EvaluateAnonymousSnippet("x", "1"));

        vm.ExtCode("bad", "{");
        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("x", "std.extVar('bad')"));
        Assert.Equal("<extvar:bad>:1:2 Unexpected: end of file while parsing field definition\n\n{\n\n", e.Message);

        vm.TlaReset();
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("<cmdline>", "function(a) a"));
        Assert.Equal("RUNTIME ERROR: Missing argument: a\n\tTop-level function call\t\n", e.Message);
        vm.TlaCode("a", "error \"tla\"");
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("<cmdline>", "function(a) a"));
        Assert.Equal("RUNTIME ERROR: tla\n\t<top-level-arg:a>:1:1-12\t$\n\t<cmdline>:1:13-14\tfunction <anonymous>\n\tTop-level function call\t\n", e.Message);
        vm.TlaReset();
        vm.TlaVar("z", "1");
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("<cmdline>", "function(a=1) a"));
        Assert.Equal("RUNTIME ERROR: function has no parameter z\n\tTop-level function call\t\n", e.Message);
        vm.ExtCode("e", "error \"ext\"");
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("<cmdline>", "std.extVar(\"e\")"));
        Assert.Equal("RUNTIME ERROR: ext\n\t<extvar:e>:1:1-12\t$\n\t<cmdline>:1:1-16\t$\n\tDuring evaluation\t\n", e.Message);
    }

    [Fact]
    public void EndlessTailstrictLoopIsAnError()
    {
        // go-jsonnet dies with a fatal native stack overflow here.
        string msg = EvalError("local f(x) = f(x) tailstrict; f(0)");
        Assert.Equal("RUNTIME ERROR: max stack frames exceeded.\n\t<cmdline>:1:14-18\tfunction <f>\n\t<cmdline>:1:31-35\t$\n\tDuring evaluation\t\n", msg);
    }

    [Fact]
    public void NativeFunctions()
    {
        JsonnetVm vm = new();
        vm.NativeFunction(new JsonnetNativeFunction("concat", new[] { "a", "b" }, args =>
            new List<object?> { args[0], args[1], (long)7, new Dictionary<string, object?> { ["k"] = true } }));
        vm.NativeFunction(new JsonnetNativeFunction("fail", new[] { "x" }, args => throw new InvalidOperationException("nope " + args[0])));
        Assert.Equal("[\n   {\n      \"x\": [\n         1\n      ]\n   },\n   \"s\",\n   7,\n   {\n      \"k\": true\n   }\n]\n",
            vm.EvaluateAnonymousSnippet("x", "std.native('concat')({x: [1]}, 's')"));
        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("x", "std.native('fail')(1.5)"));
        Assert.Equal("RUNTIME ERROR: nope 1.5\n\tx:1:1-24\t$\n\tDuring evaluation\t\n", e.Message);
    }

    [Fact]
    public void MultiAndStreamOutput()
    {
        JsonnetVm vm = new();
        IReadOnlyDictionary<string, string> multi = vm.EvaluateAnonymousSnippetMulti("x", "{b: [1], a: 'x'}");
        Assert.Equal(new[] { "a", "b" }, multi.Keys);
        Assert.Equal("\"x\"\n", multi["a"]);
        Assert.Equal("[\n   1\n]\n", multi["b"]);

        IReadOnlyList<string> stream = vm.EvaluateAnonymousSnippetStream("x", "[1, {a: 2}]");
        Assert.Equal(new[] { "1\n", "{\n   \"a\": 2\n}\n" }, stream);

        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippetStream("x", "{}"));
        Assert.Equal("RUNTIME ERROR: stream mode: top-level object was a object, should be an array whose elements hold the JSON for each document in the stream.\n\tDuring manifestation\t\n", e.Message);

        vm.StringOutput = true;
        Assert.Equal("x\n", vm.EvaluateAnonymousSnippetMulti("x", "{a: 'x'}")["a"]);
        e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippetMulti("x", "{a: 1, b: [1]}"));
        Assert.Equal("RUNTIME ERROR: multi mode: top-level object's key a has a value of type float64, should be a string\n\tDuring manifestation\t\n", e.Message);
    }

    [Fact]
    public void StringOutputAndNewline()
    {
        JsonnetVm vm = new() { StringOutput = true, OutputNewline = false };
        Assert.Equal("a\nb", vm.EvaluateAnonymousSnippet("x", "'a\\nb'"));
        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("x", "1"));
        Assert.Equal("RUNTIME ERROR: expected string result, got: number\n\tDuring manifestation\t\n", e.Message);
    }

    [Fact]
    public void Trace()
    {
        JsonnetVm vm = new();
        StringWriter w = new();
        vm.SetTraceOut(w);
        Assert.Equal("2\n", vm.EvaluateAnonymousSnippet("x", "std.trace('hi', 2)"));
        Assert.Equal("TRACE: x:1 hi\n", w.ToString());
    }

    [Fact]
    public void MaxTraceCropsStackTrace()
    {
        JsonnetVm vm = new() { MaxTrace = 2, MaxStack = 10 };
        JsonnetException e = Assert.ThrowsAny<JsonnetException>(() => vm.EvaluateAnonymousSnippet("x", "local f(x) = f(x+1) + 1; f(0)"));
        Assert.Equal("RUNTIME ERROR: max stack frames exceeded.\n\tx:1:14-20\tfunction <f>\n\t...\n\tDuring evaluation\t\n", e.Message);
    }

    [Fact]
    public void VersionIsGoJsonnetVersion() => Assert.Equal("v0.22.0", JsonnetVm.Version);
}
