namespace JsonnetSharp.Tests;

// Every builtin of go-jsonnet's funcBuiltins table (results, named parameters, defaults, error texts),
// plus the std.jsonnet functions built on them. testdata/builtins_refcases.txt holds the snippets and the
// output (or error) the go-jsonnet 0.22.0 binary produced for them: cases are separated by "=====" lines,
// and a "-----" line separates a snippet from its expected "OK\n<output>" or "ERR\n<error text>".
public class BuiltinTests
{
    private static readonly string[][] Cases = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "testdata", "builtins_refcases.txt"))
        .Replace("\r\n", "\n")
        .Split(new[] { "\n=====\n" }, StringSplitOptions.None)
        .Select(c => c.Split(new[] { "\n-----\n" }, 2, StringSplitOptions.None))
        .ToArray();

    public static IEnumerable<object[]> CaseIndexes() => Enumerable.Range(0, Cases.Length).Select(i => new object[] { i });

    [Theory]
    [MemberData(nameof(CaseIndexes))]
    public void MatchesGoJsonnet(int index)
    {
        string snippet = Cases[index][0];
        string expected = Cases[index][1];
        if (index == Cases.Length - 1)
        {
            expected = expected.Substring(0, expected.Length - 1); // the file ends with a newline
        }

        string actual;
        try
        {
            actual = "OK\n" + new JsonnetVm().EvaluateAnonymousSnippet("<cmdline>", snippet);
        }
        catch (JsonnetException e)
        {
            actual = "ERR\n" + e.Message;
        }

        // A Go panic prints a Go stack trace; only the panic message can match.
        if (expected.StartsWith("ERR\nINTERNAL ERROR: (CRASH)", StringComparison.Ordinal))
        {
            expected = expected.Split('\n')[1];
            actual = actual.Split('\n')[1];
        }
        Assert.True(expected == actual, "snippet: " + snippet + "\nexpected:\n" + expected + "\nactual:\n" + actual);
    }

    [Fact]
    public void CoversEveryBuiltin()
    {
        string all = string.Join("\n", Cases.Select(c => c[0]));
        foreach (string name in Builtins.FuncBuiltins.Keys.Where(n => !n.StartsWith("$", StringComparison.Ordinal)))
        {
            Assert.True(all.Contains("std." + name + "("), "no reference case for std." + name);
        }
    }
}
