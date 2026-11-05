// Ports go-jsonnet internal/program/program.go.

using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    // API for AST pre-processing (desugaring, static analysis).
    internal static partial class Program
    {
        // SnippetToAST converts a Jsonnet code snippet to a desugared and analyzed AST.
        // Throws StaticError.
        internal static Node SnippetToAST(string diagnosticFilename, string importedFilename, string snippet) =>
            Process(Parser.SnippetToRawAST(diagnosticFilename, importedFilename, snippet).Node);

        // snippet holds the raw bytes of an imported file, as Go's string does.
        internal static Node SnippetToAST(string diagnosticFilename, string importedFilename, byte[] snippet) =>
            Process(Parser.SnippetToRawAST(diagnosticFilename, importedFilename, snippet).Node);

        private static Node Process(Node node)
        {
            node = DesugarAST(node);
            Analyze(node);
            return node;
        }
    }
}
