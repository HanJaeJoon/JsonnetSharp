// Ports the public part of go-jsonnet's NativeFunction (thunks.go); the call plumbing is NativeCallable.

namespace JsonnetSharp;

/// <summary>
/// A function implemented in .NET that Jsonnet code can call through <c>std.native(name)</c>.
/// </summary>
/// <remarks>
/// Arguments and the result are JSON values: <see langword="null"/>, <see cref="bool"/>, <see cref="double"/>,
/// <see cref="string"/>, <see cref="List{T}"/> of <see cref="object"/> and <see cref="Dictionary{TKey, TValue}"/>
/// of <see cref="string"/> to <see cref="object"/>. The result may also be an <see cref="int"/>, <see cref="long"/>,
/// <see cref="short"/>, <see cref="sbyte"/>, any <see cref="IList{T}"/> of <see cref="object"/> or any
/// <see cref="IDictionary{TKey, TValue}"/> of <see cref="string"/> to <see cref="object"/>.
/// Throw an exception to report an error; its message becomes the Jsonnet runtime error message and the
/// exception becomes the <see cref="Exception.InnerException"/> of the <see cref="JsonnetRuntimeException"/>.
/// An <see cref="OutOfMemoryException"/> is not caught.
/// </remarks>
public sealed class JsonnetNativeFunction
{
    /// <summary>Creates a native function.</summary>
    /// <param name="name">The name used with <c>std.native</c>.</param>
    /// <param name="parameters">The parameter names, which must be distinct; the list is copied.</param>
    /// <param name="func">The implementation; receives one argument per parameter.</param>
    /// <exception cref="ArgumentException">A parameter name is <see langword="null"/> or appears twice.</exception>
    public JsonnetNativeFunction(string name, IReadOnlyList<string> parameters, Func<IReadOnlyList<object?>, object?> func)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        if (parameters == null)
        {
            throw new ArgumentNullException(nameof(parameters));
        }
        string[] copy = parameters.ToArray();
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (string p in copy)
        {
            // go-jsonnet does not check either and panics in flattenArgs on a duplicate.
            if (p == null || !seen.Add(p))
            {
                throw new ArgumentException(p == null ? "A parameter name is null." : "Duplicate parameter name \"" + p + "\".", nameof(parameters));
            }
        }
        Parameters = copy;
        Func = func ?? throw new ArgumentNullException(nameof(func));
    }

    /// <summary>Gets the name used with <c>std.native</c>.</summary>
    public string Name { get; }

    /// <summary>Gets the parameter names.</summary>
    public IReadOnlyList<string> Parameters { get; }

    /// <summary>Gets the implementation.</summary>
    public Func<IReadOnlyList<object?>, object?> Func { get; }
}
