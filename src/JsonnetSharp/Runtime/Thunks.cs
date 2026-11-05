// Ports go-jsonnet thunks.go.

using System.Runtime.CompilerServices;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

// readyValue
// -------------------------------------

// readyValue is a wrapper which allows to use a concrete value where normally
// some evaluation would be expected (e.g. object fields). It's not part
// of the value interface for increased type safety (it would be very easy
// to "overevaluate" otherwise) and conveniently it also saves us from implementing
// these methods for all value types.
internal sealed class ReadyValue : IUnboundField
{
    private readonly Value _content;

    public ReadyValue(Value content) => _content = content;

    public Value Evaluate(Interpreter i, SelfBinding sb, BindingFrame origBinding, string fieldName) => _content;

    public LocationRange Loc() => default;
}

// potentialValues
// -------------------------------------

// cachedThunk is a wrapper that caches the value of a potentialValue after
// the first evaluation.
// Note: All potentialValues are required to provide the same value every time,
// so it's only there for efficiency.
internal sealed class CachedThunk
{
    // The environment is a reference because it may be a cyclic structure.  A thunk
    // may refer to itself, so inside `env` there will be a variable bound back to us.
    internal Env? Env;
    internal Node? Body;
    // If null, use Err.
    internal Value? Content;
    // If also null, content is not cached yet.
    internal Exception? Err;

    public CachedThunk(Env? env, Node? body)
    {
        Env = env;
        Body = body;
    }

    private CachedThunk(Value content) => Content = content;

    internal static CachedThunk ErrorThunk(Exception err) => new(null, null) { Err = err };

    public static CachedThunk ReadyThunk(Value content) => new(content);

    [MethodImpl(Interpreter.HotPath)]
    public Value GetValue(Interpreter i)
    {
        if (Content != null)
        {
            return Content;
        }
        if (Err != null)
        {
            throw Err;
        }
        Value v = i.EvalInCleanEnv(Env!, Body!, false);
        Content = v;
        // No need to keep the environment around anymore.
        // So, this might reduce memory pressure:
        Env = null;
        return v;
    }
}

// unboundFields
// -------------------------------------

internal sealed class CodeUnboundField : IUnboundField
{
    private readonly Node _body;

    public CodeUnboundField(Node body) => _body = body;

    [MethodImpl(Interpreter.HotPath)]
    public Value Evaluate(Interpreter i, SelfBinding sb, BindingFrame origBindings, string fieldName) =>
        i.EvalInCleanEnv(new Env(origBindings, sb), _body, false);

    public LocationRange Loc() => _body.LocRange;
}

// Provide additional bindings for a field. It shadows bindings from the object.
internal sealed class BindingsUnboundField : IUnboundField
{
    private readonly IUnboundField _inner;
    // in addition to "generic" binding frame from the object
    private readonly BindingFrame _bindings;

    public BindingsUnboundField(IUnboundField inner, BindingFrame bindings)
    {
        _inner = inner;
        _bindings = bindings;
    }

    public Value Evaluate(Interpreter i, SelfBinding sb, BindingFrame origBindings, string fieldName)
    {
        BindingFrame upValues = new(origBindings.Count + _bindings.Count);
        foreach (KeyValuePair<string, CachedThunk> kv in origBindings)
        {
            upValues[kv.Key] = kv.Value;
        }
        foreach (KeyValuePair<string, CachedThunk> kv in _bindings)
        {
            upValues[kv.Key] = kv.Value;
        }
        return _inner.Evaluate(i, sb, upValues, fieldName);
    }

    public LocationRange Loc() => _inner.Loc();
}

// plusSuperUnboundField represents a `field+: ...` that hasn't been bound to an object.
internal sealed class PlusSuperUnboundField : IUnboundField
{
    private const string PlusContext = "+:";

    private readonly IUnboundField _inner;

    public PlusSuperUnboundField(IUnboundField inner) => _inner = inner;

    public Value Evaluate(Interpreter i, SelfBinding sb, BindingFrame origBinding, string fieldName)
    {
        i.NewCall(Env.Empty, false);
        int stackSize = i.Stack.Count;

        i.Stack.SetCurrentTrace(new TraceElement(Loc(), PlusContext));

        Value result;
        Value right = _inner.Evaluate(i, sb, origBinding, fieldName);
        if (!Objects.ObjectHasField(sb.Super(), fieldName))
        {
            result = right;
        }
        else
        {
            Value left = Objects.ObjectIndex(i, sb.Super(), fieldName);
            result = Builtins.BuiltinPlus(i, left, right);
        }

        // Go's deferred calls, in reverse order of registration.
        i.Stack.ClearCurrentTrace();
        i.Stack.PopIfExists(stackSize);
        return result;
    }

    public LocationRange Loc() => _inner.Loc();
}

// evalCallables
// -------------------------------------

internal sealed class Closure : IEvalCallable
{
    // base environment of a closure
    // arguments should be added to it, before executing it
    private readonly Env _env;
    private readonly Function _function;
    private readonly NamedParameter[] _params;

    public Closure(Env env, Function function)
    {
        _env = env;
        _function = function;
        _params = PrepareClosureParameters(function.Parameters);
    }

    private static void ForceThunks(Interpreter i, BindingFrame args)
    {
        foreach (KeyValuePair<string, CachedThunk> arg in args)
        {
            arg.Value.GetValue(i);
        }
    }

    internal Node Body => _function.Body;

    [MethodImpl(Interpreter.HotPath)]
    public Value EvalCall(CallArguments arguments, Interpreter i) =>
        i.EvalInCleanEnv(BindArguments(arguments, i), _function.Body, arguments.TailStrict);

    // evalCall up to EvalInCleanEnv: the environment of the call.
    internal Env BindArguments(CallArguments arguments, Interpreter i)
    {
        BindingFrame upValues = _env.UpValues!;
        // addBindings(closure.env.upValues, argThunks) is built directly: the argument
        // thunks shadow the captured variables.
        NamedParameter[] parameters = _params;
        BindingFrame argThunks = new(arguments.Named.Length + arguments.Positional.Length);
        for (int k = 0; k < arguments.Positional.Length; k++)
        {
            argThunks[parameters[k].Name] = arguments.Positional[k];
        }

        foreach (NamedCallArgument arg in arguments.Named)
        {
            argThunks[arg.Name] = arg.Pv;
        }

        // Filled in below; default arguments are evaluated in the same environment as the function body.
        Env calledEnvironment = new(null, default);

        foreach (NamedParameter param in parameters)
        {
            if (!argThunks.ContainsKey(param.Name))
            {
                argThunks[param.Name] = new CachedThunk(calledEnvironment, param.DefaultArg);
            }
        }

        if (arguments.TailStrict)
        {
            ForceThunks(i, argThunks);
        }

        calledEnvironment.UpValues = Interpreter.AddBindings(upValues, argThunks);
        calledEnvironment.SelfBinding = _env.SelfBinding;
        return calledEnvironment;
    }

    public NamedParameter[] Parameters() => _params;

    private static NamedParameter[] PrepareClosureParameters(List<Parameter> parameters)
    {
        NamedParameter[] prepared = new NamedParameter[parameters.Count];
        for (int k = 0; k < prepared.Length; k++)
        {
            prepared[k] = new NamedParameter(parameters[k].Name, parameters[k].DefaultArg);
        }
        return prepared;
    }
}

// The evalCallable side of go-jsonnet's NativeFunction.
internal sealed class NativeCallable : IEvalCallable
{
    private readonly JsonnetNativeFunction _native;
    private readonly NamedParameter[] _params;

    public NativeCallable(JsonnetNativeFunction native)
    {
        _native = native;
        _params = new NamedParameter[native.Parameters.Count];
        for (int k = 0; k < _params.Length; k++)
        {
            _params[k] = new NamedParameter(native.Parameters[k], null);
        }
    }

    // evalCall evaluates a call to a NativeFunction and returns the result.
    public Value EvalCall(CallArguments arguments, Interpreter i)
    {
        CachedThunk?[] flatArgs = Builtins.FlattenArgs(arguments, _params, System.Array.Empty<Value?>());
        List<object?> nativeArgs = new(flatArgs.Length);
        foreach (CachedThunk? arg in flatArgs)
        {
            Value v = i.EvaluatePV(arg!);
            nativeArgs.Add(i.ManifestJSON(v));
        }
        object? resultJSON;
        try
        {
            resultJSON = _native.Func(nativeArgs);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A thrown exception plays the role of the error a Go native function returns.
            throw new RuntimeError(e.Message, i.GetCurrentStackTrace(), e);
        }
        try
        {
            return Interpreter.JsonToValue(i, resultJSON);
        }
        catch (Exception e) when (!(e is RuntimeError || e is InsufficientExecutionStackException ||
            e is StringTooLongException || e is OutOfMemoryException))
        {
            // Thrown by the result's own IList / IDictionary code; a Go native result is plain data.
            throw new RuntimeError(e.Message, i.GetCurrentStackTrace(), e);
        }
    }

    public NamedParameter[] Parameters() => _params;
}
