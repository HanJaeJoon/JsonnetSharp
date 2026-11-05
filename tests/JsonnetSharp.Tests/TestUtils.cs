// Ports go-jsonnet internal/testutils/testutils.go, plus helpers the ported harnesses share.

using System.Text;

namespace JsonnetSharp.Tests;

internal static class TestUtils
{
    internal static readonly UTF8Encoding Utf8 = new(false);

    // CompareWithGolden: byte-exact. Go prints a diffmatchpatch diff; the message shows both texts instead.
    internal static string? CompareWithGolden(string result, byte[] golden)
    {
        byte[] actual = Utf8.GetBytes(result);
        if (actual.SequenceEqual(golden))
        {
            return null;
        }
        int k = 0;
        while (k < actual.Length && k < golden.Length && actual[k] == golden[k])
        {
            k++;
        }
        string At(byte[] b) => k < b.Length ? "0x" + b[k].ToString("x2") : "EOF";
        return "first difference at byte " + k + ": expected " + At(golden) + ", actual " + At(actual) +
            "\nexpected:\n" + Utf8.GetString(golden) + "\nactual:\n" + result;
    }

    // Go grows goroutine stacks on demand; the harness picks the native stack size per case.
    internal static T OnStack<T>(int stackSize, Func<T> body)
    {
        T result = default!;
        Exception? error = null;
        Thread t = new(() =>
        {
            try
            {
                result = body();
            }
            catch (Exception e)
            {
                error = e;
            }
        }, stackSize);
        t.Start();
        t.Join();
        if (error != null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
        return result;
    }

    internal const int OneMegabyte = 1024 * 1024;
}

// A FileImporter that behaves as if the process ran with its working directory at root (as the Go tests
// and the cpp-jsonnet shell runners do), without changing the process-wide current directory.
// Found paths are reported relative to root with '/' separators, as Go reports them on Linux,
// where the goldens were generated.
internal sealed class RootedImporter : IJsonnetImporter
{
    private readonly FileImporter _inner = new();
    private readonly string _root;

    public RootedImporter(string root, IEnumerable<string>? jpaths = null)
    {
        _root = (Path.DirectorySeparatorChar == '\\' ? root.Replace('\\', '/') : root).TrimEnd('/') + "/";
        _inner.JPaths = (jpaths ?? Array.Empty<string>()).Select(p => GoFilePath.IsAbs(p) ? p : _root + p).ToList();
    }

    public JsonnetContents Import(string importedFrom, string importedPath, out string foundAt)
    {
        JsonnetContents c;
        string at;
        try
        {
            c = _inner.Import(GoFilePath.IsAbs(importedFrom) ? importedFrom : _root + importedFrom, importedPath, out at);
        }
        catch (JsonnetException e) when (e.Message.StartsWith("read ", StringComparison.Ordinal))
        {
            // Go names the path relative to the working directory this importer emulates.
            string msg = Path.DirectorySeparatorChar == '\\' ? e.Message.Replace('\\', '/') : e.Message;
            throw new JsonnetException(msg.Replace(_root, ""));
        }
        if (Path.DirectorySeparatorChar == '\\')
        {
            at = at.Replace('\\', '/');
        }
        foundAt = at.StartsWith(_root, StringComparison.Ordinal) ? at.Substring(_root.Length) : at;
        return c;
    }
}
