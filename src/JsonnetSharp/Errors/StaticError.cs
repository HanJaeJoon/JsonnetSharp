// Ports go-jsonnet internal/errors/static_error.go.

using System.Runtime.CompilerServices;
using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    // StaticError represents an error during parsing/lexing or static analysis.
    // It is thrown instead of returned; Message equals Go's Error() string.
    internal sealed class StaticError : Exception
    {
        public readonly string Msg;
        private readonly LocationRange _loc;

        // MakeStaticErrorMsg
        public StaticError(string msg)
            : this(msg, default)
        {
        }

        // MakeStaticError
        public StaticError(string msg, LocationRange lr)
            : base(Format(msg, lr))
        {
            Msg = msg;
            _loc = lr;
        }

        // Loc returns the place in the source code that triggered the error.
        public LocationRange Loc => _loc;

        // WithContext returns a new StaticError with additional context after the error message.
        public StaticError WithContext(string context) => new(Msg + " while " + context, _loc);

        // Error returns the string representation of a StaticError.
        public string Error() => Message;

        private static string Format(string msg, LocationRange lr)
        {
            string loc = "";
            if (lr.IsSet())
            {
                loc = lr.ToString();
            }
            return loc + " " + msg;
        }
    }

    internal static class StackGuard
    {
        // go-jsonnet has no depth limit and dies with a fatal stack overflow on pathological
        // nesting (about 100000 levels on its 1 GB stack); we report the error cpp-jsonnet uses
        // for its parser depth limit instead.
        internal const string MaxDepthMessage = "Exceeded maximum parse depth limit.";

        internal static void Ensure(LocationRange loc)
        {
            if (!loc.IsSet())
            {
                // Synthetic nodes (e.g. desugared field names) have no location for the error; their
                // located parent probed the stack one frame up, which leaves ample headroom.
                return;
            }
            try
            {
                RuntimeHelpers.EnsureSufficientExecutionStack();
            }
            catch (InsufficientExecutionStackException)
            {
                throw new StaticError(MaxDepthMessage, loc);
            }
        }
    }
}
