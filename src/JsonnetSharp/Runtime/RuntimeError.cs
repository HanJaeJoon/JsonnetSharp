// Ports go-jsonnet runtime_error.go.

using JsonnetSharp.Ast;

namespace JsonnetSharp;

// RuntimeError is an error discovered during evaluation of the program.
// Thrown instead of returned; Message equals Go's Error().
internal sealed class RuntimeError : Exception
{
    public readonly string Msg;
    public readonly List<TraceFrame> StackTraceFrames;

    // The .NET exception behind the error (a native function's), for JsonnetException.InnerException.
    public readonly Exception? Cause;

    public RuntimeError(string msg, List<TraceFrame> stackTrace, Exception? cause = null)
        : base("RUNTIME ERROR: " + msg)
    {
        Msg = msg;
        StackTraceFrames = stackTrace;
        Cause = cause;
    }

    public string Error() => Message;
}

// A string longer than .NET can hold (Go has no such limit). The VM reports it as a RuntimeError
// at the current Jsonnet stack, like native stack exhaustion.
internal sealed class StringTooLongException : Exception
{
    // .NET's maximum string length in UTF-16 code units.
    internal const int MaxLength = 0x3FFFFFDF;

    // The most code points a string value can have: the largest int[] .NET Framework allocates.
    internal const int MaxRunes = 0x7FEFFFFF;

    public StringTooLongException()
        : base("string is too long for .NET (more than 1073741791 UTF-16 code units)")
    {
    }

    public StringTooLongException(string message)
        : base(message)
    {
    }
}

// The stack

// TraceFrame is tracing information about a single frame of the call stack.
internal readonly struct TraceFrame
{
    public readonly string Name;
    public readonly LocationRange Loc;

    public TraceFrame(string name, LocationRange loc)
    {
        Name = name;
        Loc = loc;
    }

    public static TraceFrame FromTraceElement(in TraceElement trace) =>
        // TODO(sbarzowski) maybe context should never be null
        new(trace.Context ?? "", trace.Loc);
}

// traceElement represents tracing information, including a location range and a
// surrounding context. Go keeps pointers to the node's location and context; here a single
// reference to the node (or to a TraceLoc for other locations) keeps the struct small, which
// matters because one is saved per evaluation frame.
internal readonly struct TraceElement
{
    private readonly object? _src;

    // The trace of evaluating node: its location and context.
    public TraceElement(Node node) => _src = node;

    public TraceElement(in LocationRange loc, string? context) => _src = new TraceLoc(loc, context);

    private TraceElement(string fieldName) => _src = fieldName;

    // Manifesting a field: Go formats "Field %#v" up front, but quoting a huge key can pass .NET's string
    // limit while its JSON fits, so the message is built only when a trace is read.
    public static TraceElement Field(string fieldName) => new(fieldName);

    // Go: loc == nil && context == nil. A trace never has a context without a location.
    public bool IsEmpty => _src == null;

    public bool HasLoc => _src != null;

    public LocationRange Loc => _src switch
    {
        Node n => n.LocRange,
        string fieldName => LocationRange.MakeMessage("Field " + Yaml.GoFormat.Quote(fieldName)),
        _ => ((TraceLoc)_src!).Loc,
    };

    public string? Context => _src is Node n ? n.Ctx : (_src as TraceLoc)?.Context;

    private sealed class TraceLoc
    {
        public readonly LocationRange Loc;
        public readonly string? Context;

        public TraceLoc(in LocationRange loc, string? context)
        {
            Loc = loc;
            Context = context;
        }
    }
}
