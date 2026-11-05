namespace JsonnetSharp;

/// <summary>
/// An error reported by Jsonnet. <see cref="Exception.Message"/> is exactly the text go-jsonnet's
/// returned error prints, including the stack trace and the trailing newline.
/// </summary>
/// <remarks>
/// Errors that go-jsonnet formats as "INTERNAL ERROR" (for example an importer failure while loading
/// the main file in <see cref="JsonnetVm.EvaluateFile"/>) are thrown as this base type; their bug report
/// link points at the JsonnetSharp issue tracker instead of go-jsonnet's.
/// Importers may also throw it to report a failure.
/// </remarks>
public class JsonnetException : Exception
{
    /// <summary>Creates an exception with the given message.</summary>
    /// <param name="message">The error text.</param>
    public JsonnetException(string message)
        : base(message)
    {
    }

    /// <summary>Creates an exception with the given message and cause.</summary>
    /// <param name="message">The error text.</param>
    /// <param name="innerException">The underlying exception.</param>
    public JsonnetException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An error raised while evaluating a Jsonnet program ("RUNTIME ERROR: ..." followed by a stack trace).
/// When a native function threw, <see cref="Exception.InnerException"/> is that exception.
/// </summary>
public sealed class JsonnetRuntimeException : JsonnetException
{
    internal JsonnetRuntimeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// An error found before evaluation: a lexing, parsing or static analysis error.
/// </summary>
public sealed class JsonnetStaticException : JsonnetException
{
    internal JsonnetStaticException(string message)
        : base(message)
    {
    }
}
