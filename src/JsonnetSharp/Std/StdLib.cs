// Replaces go-jsonnet astgen/stdast.go (the generated std AST): std.jsonnet is embedded and
// parsed once, as cmd/dumpstdlibast does: SnippetToAST("<std>", "", std.jsonnet).

using System.Reflection;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

internal static class StdLib
{
    private static readonly Lazy<Node> LazyAst = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);

    // The desugared and analyzed std AST. It is shared by all VMs and never mutated.
    internal static Node Ast => LazyAst.Value;

    private static Node Load()
    {
        using Stream stream = typeof(StdLib).GetTypeInfo().Assembly.GetManifestResourceStream("JsonnetSharp.std.jsonnet")
            ?? throw new InvalidOperationException("std.jsonnet resource not found");
        using StreamReader reader = new(stream, GoUtf8.Utf8);
        return Program.SnippetToAST("<std>", "", reader.ReadToEnd());
    }
}
