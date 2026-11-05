// Ports go-jsonnet interpreter.go.
//
// Errors are exceptions. Like in Go, an error aborts the whole evaluation and the interpreter is
// discarded, so state that Go restores in `defer` on the error path (current trace) is only
// restored on the success path here. The call stack is not unwound on error in either version,
// which lets the VM build the stack trace for a native stack overflow after the fact.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

internal sealed class Env
{
    // Bindings introduced in this frame. The way previous bindings are treated
    // depends on the type of a frame.
    // If cleanEnv == true then previous bindings are ignored (it's a clean
    // environment with just the variables we have here).
    // If cleanEnv == false then if this frame doesn't contain a binding
    // previous bindings will be used.
    public BindingFrame? UpValues;
    public SelfBinding SelfBinding;

    public Env(BindingFrame? upValues, SelfBinding sb)
    {
        UpValues = upValues;
        SelfBinding = sb;
    }

    // Go's environment{} (never mutated).
    public static readonly Env Empty = new(null, default);
}

internal struct CallFrame
{
    // Tracing information about the place where it was called from.
    public TraceElement Trace;

    public Env Env;

    // True if it switches to a clean environment (function call or array element)
    // False otherwise, e.g. for local
    public bool CleanEnv;

    // Whether this frame can be removed from the stack when it doesn't affect
    // the evaluation result, but in case of an error, it won't appear on the
    // stack trace.
    // It's used for tail call optimization.
    public bool Trimmable;
}

internal enum TailCallStatus
{
    NonTailCall,
    TailCall,
}

internal sealed class CallStack
{
    public TraceElement CurrentTrace;
    private CallFrame[] _stack = new CallFrame[64];
    public int Count;
    public int Calls;
    public readonly int Limit;

    public CallStack(int limit) => Limit = limit;

    public ref CallFrame this[int index] => ref _stack[index];

    private ref CallFrame Top() => ref _stack[Count - 1];

    private void Push(in CallFrame frame)
    {
        if (Count == _stack.Length)
        {
            System.Array.Resize(ref _stack, _stack.Length * 2);
        }
        _stack[Count++] = frame;
    }

    private void TruncateTo(int n)
    {
        for (int k = n; k < Count; k++)
        {
            _stack[k] = default;
        }
        Count = n;
    }

    // It might've been popped already by tail call optimization.
    // We check if it was trimmed by comparing the current stack size to the position
    // of the frame we want to pop.
    public void PopIfExists(int whichFrame)
    {
        if (Count == whichFrame)
        {
            if (Top().CleanEnv)
            {
                Calls--;
            }
            SetCurrentTrace(Top().Trace);
            TruncateTo(Count - 1);
        }
    }

    // If there is a trimmable frame followed by some locals, pop them all.
    public void TailCallTrimStack()
    {
        for (int k = Count - 1; k >= 0; k--)
        {
            if (_stack[k].CleanEnv)
            {
                if (!_stack[k].Trimmable)
                {
                    return;
                }
                // Remove this stack frame and everything above it
                TruncateTo(k);
                Calls--;
                return;
            }
        }
    }

    public void SetCurrentTrace(in TraceElement trace)
    {
        if (!CurrentTrace.IsEmpty)
        {
            throw new InvalidOperationException("Tried to change the traceElement while the old one was still there.");
        }
        CurrentTrace = trace;
    }

    public void ClearCurrentTrace() => CurrentTrace = default;

    public void NewCall(Env env, bool trimmable)
    {
        if (CurrentTrace.IsEmpty)
        {
            throw new InvalidOperationException("Saving empty traceElement on stack");
        }
        Push(new CallFrame
        {
            CleanEnv = true,
            Trace = CurrentTrace,
            Env = env,
            Trimmable = trimmable,
        });
        ClearCurrentTrace();
        Calls++;
    }

    public void NewLocal(BindingFrame vars)
    {
        Push(new CallFrame
        {
            Env = new Env(vars, default),
            Trace = CurrentTrace,
        });
        ClearCurrentTrace();
    }

    // getSelfBinding resolves the self construct
    public SelfBinding GetSelfBinding()
    {
        for (int k = Count - 1; k >= 0; k--)
        {
            if (_stack[k].CleanEnv)
            {
                return _stack[k].Env.SelfBinding;
            }
        }
        throw new InvalidOperationException("malformed stack");
    }

    // lookUpVar finds for the closest variable in scope that matches the given name.
    public CachedThunk? LookUpVar(string id)
    {
        for (int k = Count - 1; k >= 0; k--)
        {
            BindingFrame? upValues = _stack[k].Env.UpValues;
            if (upValues != null && upValues.TryGetValue(id, out CachedThunk? bind))
            {
                return bind;
            }
            if (_stack[k].CleanEnv)
            {
                // Nothing beyond the captured environment of the thunk / closure.
                break;
            }
        }
        return null;
    }

    public CachedThunk LookUpVarOrPanic(string id) =>
        LookUpVar(id) ?? throw new InvalidOperationException("RUNTIME: Unknown variable: " + id + " (we should have caught this statically)");

    public Env GetCurrentEnv(Node ast) => new(Capture(ast.FreeVars), GetSelfBinding());

    // Build a binding frame containing specified variables.
    public BindingFrame Capture(string[] freeVars)
    {
        BindingFrame env = new(freeVars.Length);
        foreach (string fv in freeVars)
        {
            env[fv] = LookUpVarOrPanic(fv);
        }
        return env;
    }
}

// astMakeArrayElement wraps the function argument of std.makeArray so that
// it can be embedded in cachedThunk without needing to execute it ahead of
// time.  It is equivalent to `local i = 42; func(i)`.  It therefore has no
// free variables and needs only an empty environment to execute.
internal sealed class AstMakeArrayElement : Node
{
    public readonly ValueFunction Function;
    public readonly int Index;

    public AstMakeArrayElement(ValueFunction function, int index)
    {
        Function = function;
        Index = index;
    }
}

// Keeps current execution context and evaluates things
internal sealed partial class Interpreter
{
    // MethodImplOptions.AggressiveOptimization (absent from netstandard2.0): the recursive evaluation
    // methods skip unoptimized tier-0 code, whose larger frames and missing inlining would make a
    // 1 MB thread run out of stack before MaxStack = 500 Jsonnet frames.
    internal const MethodImplOptions HotPath = (MethodImplOptions)512;

    // Output stream for trace() for
    internal readonly TextWriter TraceOut;

    // External variables
    internal Dictionary<string, CachedThunk> ExtVars = new();

    // Native functions
    internal readonly Dictionary<string, JsonnetNativeFunction> NativeFuncs;

    // A part of std object common to all files
    internal ValueObject BaseStd = null!;

    // Keeps imports
    internal readonly ImportCache ImportCache;

    // Current stack. It is used for:
    // 1) Keeping environment (object we're in, variables)
    // 2) Diagnostic information in case of failure
    internal readonly CallStack Stack;

    private Interpreter(int maxStack, ImportCache ic, TextWriter traceOut, Dictionary<string, JsonnetNativeFunction> nativeFuncs)
    {
        Stack = new CallStack(maxStack);
        ImportCache = ic;
        TraceOut = traceOut;
        NativeFuncs = nativeFuncs;
    }

    internal List<TraceFrame> GetCurrentStackTrace()
    {
        List<TraceFrame> result = new();
        for (int k = 0; k < Stack.Count; k++)
        {
            ref CallFrame f = ref Stack[k];
            if (f.CleanEnv)
            {
                result.Add(TraceFrame.FromTraceElement(f.Trace));
            }
        }
        if (Stack.CurrentTrace.HasLoc)
        {
            result.Add(TraceFrame.FromTraceElement(Stack.CurrentTrace));
        }
        return result;
    }

    // Map union, b takes precedence when keys collide.
    internal static BindingFrame AddBindings(BindingFrame a, BindingFrame b)
    {
        BindingFrame result = new(a.Count + b.Count);
        foreach (KeyValuePair<string, CachedThunk> kv in a)
        {
            result[kv.Key] = kv.Value;
        }
        foreach (KeyValuePair<string, CachedThunk> kv in b)
        {
            result[kv.Key] = kv.Value;
        }
        return result;
    }

    // Set by JsonnetVm when the caller stopped waiting; checked at each call so a runaway evaluation stops.
    internal CancelFlag Cancel = CancelFlag.Never;

    // For builtin loops that make no calls and can run for seconds (std.range, std.makeArray).
    internal void PollCancel(long k)
    {
        if ((k & 0xFFFF) == 0 && Cancel.Requested)
        {
            throw new OperationCanceledException();
        }
    }

    internal void NewCall(Env env, bool trimmable)
    {
        if (Cancel.Requested)
        {
            throw new OperationCanceledException();
        }
        CallStack s = Stack;
        if (s.Calls >= s.Limit)
        {
            throw new RuntimeError("max stack frames exceeded.", GetCurrentStackTrace());
        }
        s.NewCall(env, trimmable);
    }

    // Sizes for popIfExists owed by frames that Evaluate's loop entered in place of Go's nested
    // evaluate calls; see Evaluate.
    private readonly List<int> _pendingPops = new();

    // Nesting of the value comparison in progress (Builtins.RawEquals, Builtins.ValueCmp).
    internal int CompareDepth;

    private const int MaxPendingPops = 1000000;

    // Go's evaluate + rawevaluate. Nodes whose result is the result of another evaluation in tail
    // position (a conditional's branch, a local's body, a closure's body) are not evaluated by a
    // nested call but by the loop below, which keeps the native stack flat for those. The stack
    // pops Go does as each nested evaluate returns are replayed afterwards, innermost first.
    [MethodImpl(Interpreter.HotPath)]
    internal Value Evaluate(Node a, TailCallStatus tc)
    {
        // Deep native recursion surfaces as InsufficientExecutionStackException, which the VM
        // reports as "max stack frames exceeded." with the stack trace at that point.
        RuntimeHelpers.EnsureSufficientExecutionStack();
        TraceElement oldTrace = Stack.CurrentTrace;
        int pendingBase = _pendingPops.Count;
        Value v;
        while (true)
        {
            Stack.CurrentTrace = new TraceElement(a);
            if (a is Conditional cond)
            {
                a = EvalCondition(cond) ? cond.BranchTrue : cond.BranchFalse!;
                continue;
            }
            if (a is Local local)
            {
                EnterLocal(local);
                a = local.Body;
                continue;
            }
            if (a is Apply apply)
            {
                Node? body = EvalApply(apply, tc, out v);
                if (body == null)
                {
                    break;
                }
                a = body;
                tc = TailCallStatus.TailCall;
                continue;
            }
            v = RawEvaluate(a, tc);
            break;
        }
        if (_pendingPops.Count > pendingBase)
        {
            PopPending(pendingBase);
        }
        Stack.CurrentTrace = oldTrace;
        return v;
    }

    private void PopPending(int pendingBase)
    {
        for (int k = _pendingPops.Count - 1; k >= pendingBase; k--)
        {
            // Each nested evaluate had restored the empty trace its frame push left behind.
            Stack.ClearCurrentTrace();
            Stack.PopIfExists(_pendingPops[k]);
        }
        _pendingPops.RemoveRange(pendingBase, _pendingPops.Count - pendingBase);
    }

    [MethodImpl(Interpreter.HotPath)]
    private bool EvalCondition(Conditional node)
    {
        Value cond = Evaluate(node.Cond, TailCallStatus.NonTailCall);
        return GetBoolean(cond).Value;
    }

    // The ast.Local case up to evaluating the body.
    private void EnterLocal(Local node)
    {
        BindingFrame vars = new(node.Binds.Count);
        Env bindEnv = Stack.GetCurrentEnv(node);
        foreach (LocalBind bind in node.Binds)
        {
            CachedThunk th = new(bindEnv, bind.Body);

            // recursive locals
            vars[bind.Variable] = th;
            bindEnv.UpValues![bind.Variable] = th;
        }
        Stack.NewLocal(vars);
        // Add new stack frame, with new thunk for this variable
        // execute body WRT stack frame.
        _pendingPops.Add(Stack.Count);
    }

    // The ast.Apply case. For a closure it pushes the call frame and returns the body to evaluate
    // (closure.evalCall up to EvalInCleanEnv's evaluate); otherwise it calls the function.
    [MethodImpl(Interpreter.HotPath)]
    private Node? EvalApply(Apply node, TailCallStatus tc, out Value result)
    {
        // Eval target
        Value target = Evaluate(node.Target, TailCallStatus.NonTailCall);
        ValueFunction function = GetFunction(target);

        // environment in which we can evaluate arguments
        Env argEnv = Stack.GetCurrentEnv(node);
        List<CommaSeparatedExpr> positional = node.Arguments.Positional;
        List<NamedArgument> named = node.Arguments.Named;
        CachedThunk[] pos = positional.Count == 0 ? System.Array.Empty<CachedThunk>() : new CachedThunk[positional.Count];
        for (int k = 0; k < pos.Length; k++)
        {
            pos[k] = new CachedThunk(argEnv, positional[k].Expr);
        }
        NamedCallArgument[] nam = named.Count == 0 ? System.Array.Empty<NamedCallArgument>() : new NamedCallArgument[named.Count];
        for (int k = 0; k < nam.Length; k++)
        {
            nam[k] = new NamedCallArgument(named[k].Name, new CachedThunk(argEnv, named[k].Arg));
        }
        CallArguments arguments = new(pos, nam, node.TailStrict);

        // evaluateTailCall
        if (tc == TailCallStatus.TailCall)
        {
            Stack.TailCallTrimStack();
        }
        if (function.Ec is Closure closure)
        {
            if (_pendingPops.Count >= MaxPendingPops)
            {
                // Go recurses natively here and dies with a fatal stack overflow (at about 130000
                // levels of `f(n - 1) tailstrict`); an endless tailstrict loop must not run forever.
                throw new RuntimeError("max stack frames exceeded.", GetCurrentStackTrace());
            }
            ValueFunction.CheckArguments(this, arguments, closure.Parameters());
            Env env = closure.BindArguments(arguments, this);
            NewCall(env, arguments.TailStrict);
            _pendingPops.Add(Stack.Count);
            result = null!;
            return closure.Body;
        }
        result = function.Call(this, arguments);
        return null;
    }

    [MethodImpl(MethodImplOptions.NoInlining | HotPath)]
    private Value RawEvaluate(Node a, TailCallStatus tc)
    {
        switch (a)
        {
            case Var node:
                return Stack.LookUpVarOrPanic(node.Id).GetValue(this);

            case Ast.Index node:
                return EvalIndex(node);

            case LiteralString node:
                return new ValueFlatString(node.Value);

            case Binary node:
                return EvalBinary(node, tc);

            case LiteralNumber node:
                return EvalLiteralNumber(node);

            case Function node:
                return new ValueFunction(new Closure(Stack.GetCurrentEnv(a), node));

            case DesugaredObject node:
                return EvalObject(node);

            case Self:
                return Stack.GetSelfBinding().Self!;

            case Unary node:
                return EvalUnary(node, tc);

            case Ast.Array node:
                return EvalArray(node);

            case LiteralBoolean node:
                return node.Value ? ValueBoolean.True : ValueBoolean.False;

            case LiteralNull:
                return ValueNull.Instance;

            case Ast.Error node:
                return EvalError(node);

            case SuperIndex node:
                return EvalSuperIndex(node);

            case InSuper node:
                return EvalInSuper(node);

            case Import node:
                return ImportCache.ImportCode(node.LocRange.FileName, node.File.Value, this);

            case ImportStr node:
                return ImportCache.ImportString(node.LocRange.FileName, node.File.Value, this);

            case ImportBin node:
                return ImportCache.ImportBinary(node.LocRange.FileName, node.File.Value, this);

            case AstMakeArrayElement node:
            {
                CallArguments arguments = CallArguments.Args(CachedThunk.ReadyThunk(new ValueNumber(node.Index)));
                return EvaluateTailCall(node.Function, arguments, tc);
            }

            default:
                throw new InvalidOperationException("Executing this AST type not implemented: " + a.GetType());
        }
    }

    [MethodImpl(Interpreter.HotPath)]
    private Value EvalUnary(Unary node, TailCallStatus tc)
    {
        Value value = Evaluate(node.Expr, tc);
        return Builtins.UopBuiltins[(int)node.Op].Function(this, value);
    }

    private Value EvalSuperIndex(SuperIndex node)
    {
        Value index = Evaluate(node.Index!, TailCallStatus.NonTailCall);
        ValueString indexStr = GetString(index);
        return Objects.ObjectIndex(this, Stack.GetSelfBinding().Super(), indexStr.GetGoString());
    }

    private Value EvalInSuper(InSuper node)
    {
        Value index = Evaluate(node.Index, TailCallStatus.NonTailCall);
        ValueString indexStr = GetString(index);
        bool hasField = Objects.ObjectHasField(Stack.GetSelfBinding().Super(), indexStr.GetGoString());
        return hasField ? ValueBoolean.True : ValueBoolean.False;
    }

    private Value EvalArray(Ast.Array node)
    {
        SelfBinding sb = Stack.GetSelfBinding();
        CachedThunk[] elements = new CachedThunk[node.Elements.Count];
        for (int k = 0; k < elements.Length; k++)
        {
            Node el = node.Elements[k].Expr;
            Env env = new(Stack.Capture(el.FreeVars), sb);
            elements[k] = new CachedThunk(env, el);
        }
        return new ValueArray(elements);
    }

    [MethodImpl(Interpreter.HotPath)]
    private Value EvalBinary(Binary node, TailCallStatus tc)
    {
        if (node.Op == BinaryOp.BopAnd)
        {
            // Special case for shortcut semantics.
            Value xv = Evaluate(node.Left, TailCallStatus.NonTailCall);
            ValueBoolean x = GetBoolean(xv);
            if (!x.Value)
            {
                return x;
            }
            Value yv = Evaluate(node.Right, tc);
            return GetBoolean(yv);
        }
        else if (node.Op == BinaryOp.BopOr)
        {
            // Special case for shortcut semantics.
            Value xv = Evaluate(node.Left, TailCallStatus.NonTailCall);
            ValueBoolean x = GetBoolean(xv);
            if (x.Value)
            {
                return x;
            }
            Value yv = Evaluate(node.Right, tc);
            return GetBoolean(yv);
        }
        else
        {
            Value left = Evaluate(node.Left, TailCallStatus.NonTailCall);
            Value right = Evaluate(node.Right, tc);
            BinaryBuiltin builtin = Builtins.BopBuiltins[(int)node.Op]
                ?? throw new InvalidOperationException("No builtin for binary operator " + node.Op);
            return builtin.Function(this, left, right);
        }
    }

    private Value EvalObject(DesugaredObject node)
    {
        // Evaluate all the field names.  Check for null, dups, etc.
        Dictionary<string, SimpleObjectField> fields = new(node.Fields.Count);
        foreach (DesugaredObjectField field in node.Fields)
        {
            Value fieldNameValue = Evaluate(field.Name, TailCallStatus.NonTailCall);
            string fieldName;
            switch (fieldNameValue)
            {
                case ValueString s:
                    fieldName = s.GetGoString();
                    break;
                case ValueNull:
                    // Omitted field.
                    continue;
                default:
                    throw Error("Field name must be string, got " + fieldNameValue.Type.Name);
            }

            if (fields.ContainsKey(fieldName))
            {
                throw Error(Objects.DuplicateFieldNameErrMsg(fieldName));
            }
            IUnboundField f = new CodeUnboundField(field.Body);
            if (field.PlusSuper)
            {
                f = new PlusSuperUnboundField(f);
            }
            fields[fieldName] = new SimpleObjectField(f, field.Hide);
        }
        List<IUnboundField> asserts = new(node.Asserts.Count);
        foreach (Node assert in node.Asserts)
        {
            asserts.Add(new CodeUnboundField(assert));
        }
        List<ObjectLocal> locals = new(node.Locals.Count);
        foreach (LocalBind local in node.Locals)
        {
            locals.Add(new ObjectLocal(local.Variable, local.Body));
        }
        BindingFrame upValues = Stack.Capture(node.FreeVars);
        return Objects.MakeValueSimpleObject(upValues, fields, asserts, locals);
    }

    private Value EvalError(Ast.Error node)
    {
        Value msgVal = Evaluate(node.Expr, TailCallStatus.NonTailCall);
        if (msgVal.Type != ValType.String)
        {
            msgVal = Builtins.BuiltinToString(this, msgVal);
        }
        ValueString msg = GetString(msgVal);
        throw Error(msg.GetGoString());
    }

    [MethodImpl(Interpreter.HotPath)]
    private Value EvalIndex(Ast.Index node)
    {
        Value targetValue = Evaluate(node.Target, TailCallStatus.NonTailCall);
        Value index = Evaluate(node.IndexExpr!, TailCallStatus.NonTailCall);
        switch (targetValue)
        {
            case ValueObject target:
                return target.Index(this, GetString(index).GetGoString());
            case ValueArray target:
                // TODO(https://github.com/google/jsonnet/issues/377): non-integer indexes should be an error
                return target.Index(this, Util.GoFloatToInt64(GetNumber(index).Value));
            case ValueString target:
                // TODO(https://github.com/google/jsonnet/issues/377): non-integer indexes should be an error
                return target.Index(this, Util.GoFloatToInt64(GetNumber(index).Value));
        }
        throw Error("Value non indexable: " + GoReflectType(targetValue));
    }

    // reflect.TypeOf(v) of a Go value as printed by %v.
    private static string GoReflectType(Value v) => v switch
    {
        ValueBoolean => "*jsonnet.valueBoolean",
        ValueNumber => "*jsonnet.valueNumber",
        ValueNull => "*jsonnet.valueNull",
        ValueFunction => "*jsonnet.valueFunction",
        _ => "*jsonnet." + v.GetType().Name,
    };

    private Value EvalLiteralNumber(LiteralNumber node)
    {
        // Since the lexer ensures that OriginalString is of
        // the right form, this will only fail if the number is
        // too large to fit in a double.
        if (!GoStrconv.TryParseFloat(node.OriginalString, out double num) ||
            double.IsInfinity(num))
        {
            throw Error("overflow");
        }
        return new ValueNumber(num);
    }

    // unparseString Wraps in "" and escapes stuff to make the string JSON-compliant and human-readable.
    internal static string UnparseString(string v)
    {
        StringBuilder buf = new(v.Length + 2);
        UnparseString(buf, v);
        return Util.ToStringChecked(buf);
    }

    internal static void UnparseString(StringBuilder buf, string v)
    {
        buf.Append('"');
        for (int k = 0; k < v.Length; k++)
        {
            if ((k & 0xFFFF) == 0)
            {
                // Up to 6 chars per char: stop at .NET's string limit before StringBuilder's int overflows.
                Util.CheckLength(buf);
            }
            char c = v[k];
            switch (c)
            {
                case '"':
                    buf.Append("\\\"");
                    break;
                case '\\':
                    buf.Append("\\\\");
                    break;
                case '\b':
                    buf.Append("\\b");
                    break;
                case '\f':
                    buf.Append("\\f");
                    break;
                case '\n':
                    buf.Append("\\n");
                    break;
                case '\r':
                    buf.Append("\\r");
                    break;
                case '\t':
                    buf.Append("\\t");
                    break;
                case '\0':
                    buf.Append("\\u0000");
                    break;
                default:
                    if (c < 0x20 || (c >= 0x7f && c <= 0x9f))
                    {
                        buf.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else if (char.IsHighSurrogate(c) && k + 1 < v.Length && char.IsLowSurrogate(v[k + 1]))
                    {
                        buf.Append(c).Append(v[k + 1]);
                        k++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        // Go ranges over UTF-8; an unpaired surrogate is invalid and becomes U+FFFD.
                        buf.Append((char)0xFFFD);
                    }
                    else
                    {
                        buf.Append(c);
                    }
                    break;
            }
        }
        buf.Append('"');
    }

    internal static string UnparseNumber(double v)
    {
        // fmt's %.0f and %.17g print +Inf, -Inf and NaN.
        if (GoStrconv.NonFinite(v) is string special)
        {
            return special;
        }
        if (v == Math.Floor(v))
        {
            return Util.FormatIntegral(v);
        }

        // See "What Every Computer Scientist Should Know About Floating-Point Arithmetic"
        // Theorem 15
        // http://docs.oracle.com/cd/E19957-01/806-3568/ncg_goldberg.html
        return Util.FormatG17(v);
    }

    // manifestJSON converts to standard JSON representation as in "encoding/json" package:
    // null, bool, double, string, List<object?> and Dictionary<string, object?> (keys in Go order).
    [MethodImpl(Interpreter.HotPath)]
    internal object? ManifestJSON(Value v)
    {
        // TODO(sbarzowski) Add nice stack traces indicating the part of the code which
        // evaluates to non-manifestable value (that might require passing context about
        // the root value)
        if (Stack.CurrentTrace.IsEmpty)
        {
            throw new InvalidOperationException("manifesting JSON with empty traceElement");
        }
        RuntimeHelpers.EnsureSufficientExecutionStack();

        // Fresh frame for better stack traces
        NewCall(Env.Empty, false);
        int stackSize = Stack.Count;

        object? result;
        switch (v)
        {
            case ValueBoolean b:
                result = b.Value;
                break;

            case ValueFunction:
                throw new RuntimeError("couldn't manifest function as JSON", GetCurrentStackTrace());

            case ValueNumber n:
                result = n.Value;
                break;

            case ValueString s:
                result = s.GetGoString();
                break;

            case ValueNull:
                result = null;
                break;

            case ValueArray arr:
            {
                List<object?> list = new(arr.Elements.Length);
                for (int index = 0; index < arr.Elements.Length; index++)
                {
                    Stack.SetCurrentTrace(new TraceElement(
                        LocationRange.MakeMessage("Array element " + index.ToString(CultureInfo.InvariantCulture)), null));
                    Value elVal = EvaluatePV(arr.Elements[index]);
                    list.Add(ManifestJSON(elVal));
                    Stack.ClearCurrentTrace();
                }
                result = list;
                break;
            }

            case ValueObject obj:
            {
                List<string> fieldNames = Objects.ObjectFields(obj, Hidden.WithoutHidden);
                fieldNames.Sort(Util.GoStringComparer);

                Stack.SetCurrentTrace(new TraceElement(LocationRange.MakeMessage("Checking object assertions"), null));
                Objects.CheckAssertions(this, obj);
                Stack.ClearCurrentTrace();

                Dictionary<string, object?> dict = new(fieldNames.Count);
                foreach (string fieldName in fieldNames)
                {
                    Stack.SetCurrentTrace(TraceElement.Field(fieldName));
                    Value fieldVal = obj.Index(this, fieldName);
                    dict[fieldName] = ManifestJSON(fieldVal);
                    Stack.ClearCurrentTrace();
                }
                result = dict;
                break;
            }

            default:
                throw new RuntimeError("manifesting this value not implemented yet: " + v.GetType(), GetCurrentStackTrace());
        }
        Stack.PopIfExists(stackSize);
        return result;
    }

    internal static void SerializeJSON(object? v, bool multiline, string indent, StringBuilder buf)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Util.CheckLength(buf);
        switch (v)
        {
            case null:
                buf.Append("null");
                break;

            case List<object?> list:
                if (list.Count == 0)
                {
                    buf.Append("[ ]");
                }
                else
                {
                    string prefix;
                    string indent2;
                    if (multiline)
                    {
                        prefix = "[\n";
                        indent2 = indent + "   ";
                    }
                    else
                    {
                        prefix = "[";
                        indent2 = indent;
                    }
                    foreach (object? elem in list)
                    {
                        buf.Append(prefix);
                        buf.Append(indent2);
                        SerializeJSON(elem, multiline, indent2, buf);
                        prefix = multiline ? ",\n" : ", ";
                    }
                    if (multiline)
                    {
                        buf.Append('\n');
                    }
                    buf.Append(indent);
                    buf.Append(']');
                }
                break;

            case bool b:
                buf.Append(b ? "true" : "false");
                break;

            case double d:
                buf.Append(UnparseNumber(d));
                break;

            case Dictionary<string, object?> dict:
            {
                List<string> fieldNames = new(dict.Keys);
                fieldNames.Sort(Util.GoStringComparer);

                if (fieldNames.Count == 0)
                {
                    buf.Append("{ }");
                }
                else
                {
                    string prefix;
                    string indent2;
                    if (multiline)
                    {
                        prefix = "{\n";
                        indent2 = indent + "   ";
                    }
                    else
                    {
                        prefix = "{";
                        indent2 = indent;
                    }
                    foreach (string fieldName in fieldNames)
                    {
                        object? fieldVal = dict[fieldName];

                        buf.Append(prefix);
                        buf.Append(indent2);

                        UnparseString(buf, fieldName);
                        buf.Append(": ");

                        SerializeJSON(fieldVal, multiline, indent2, buf);

                        prefix = multiline ? ",\n" : ", ";
                    }

                    if (multiline)
                    {
                        buf.Append('\n');
                    }
                    buf.Append(indent);
                    buf.Append('}');
                }
                break;
            }

            case string s:
                UnparseString(buf, s);
                break;

            default:
                throw new InvalidOperationException("Unsupported value for serialization " + v.GetType());
        }
    }

    internal void ManifestAndSerializeJSON(StringBuilder buf, Value v, bool multiline, string indent)
    {
        object? manifested = ManifestJSON(v);
        SerializeJSON(manifested, multiline, indent, buf);
    }

    // manifestString expects the value to be a string and returns it.
    internal void ManifestString(StringBuilder buf, Value v)
    {
        if (v is ValueString s)
        {
            buf.Append(s.GetGoString());
            return;
        }
        throw new RuntimeError("expected string result, got: " + v.Type.Name, GetCurrentStackTrace());
    }

    // %T of a manifested JSON value.
    private static string GoTypeName(object? v) => v switch
    {
        null => "<nil>",
        bool => "bool",
        double => "float64",
        string => "string",
        List<object?> => "[]interface {}",
        _ => "map[string]interface {}",
    };

    internal Dictionary<string, string> ManifestAndSerializeMulti(Value v, bool stringOutputMode, bool outputNewline)
    {
        Dictionary<string, string> r = new();
        object? json = ManifestJSON(v);
        if (json is Dictionary<string, object?> dict)
        {
            // Go iterates the map in random order; sorted order makes the first error deterministic.
            List<string> filenames = new(dict.Keys);
            filenames.Sort(Util.GoStringComparer);
            foreach (string filename in filenames)
            {
                object? fileJSON = dict[filename];
                StringBuilder buf = new();
                if (stringOutputMode)
                {
                    if (fileJSON is string val)
                    {
                        buf.Append(val);
                    }
                    else
                    {
                        string msg = "multi mode: top-level object's key " + filename + " has a value of type " +
                            GoTypeName(fileJSON) + ", should be a string";
                        throw new RuntimeError(msg, GetCurrentStackTrace());
                    }
                }
                else
                {
                    SerializeJSON(fileJSON, true, "", buf);
                }
                if (outputNewline)
                {
                    buf.Append('\n');
                }
                r[filename] = Util.ToStringChecked(buf);
            }
        }
        else
        {
            string msg = "multi mode: top-level object was a " + v.Type.Name + ", " +
                "should be an object whose keys are filenames and values hold " +
                "the JSON for that file.";
            throw new RuntimeError(msg, GetCurrentStackTrace());
        }
        return r;
    }

    internal List<string> ManifestAndSerializeYAMLStream(Value v)
    {
        List<string> r = new();
        object? json = ManifestJSON(v);
        if (json is List<object?> list)
        {
            foreach (object? doc in list)
            {
                StringBuilder buf = new();
                SerializeJSON(doc, true, "", buf);
                buf.Append('\n');
                r.Add(Util.ToStringChecked(buf));
            }
        }
        else
        {
            string msg = "stream mode: top-level object was a " + v.Type.Name + ", " +
                "should be an array whose elements hold " +
                "the JSON for each document in the stream.";
            throw new RuntimeError(msg, GetCurrentStackTrace());
        }
        return r;
    }

    // jsonToValue. Accepts the manifested representation (see ManifestJSON) plus the other
    // signed integer types and any IList / IDictionary, as native functions may return them.
    internal static Value JsonToValue(Interpreter i, object? v)
    {
        // Go recurses until its 1 GB stack dies on a cyclic result; filling 256 MB here takes tens of seconds.
        Builtins.EnterCompare(i);
        Value result = JsonToValueAux(i, v);
        i.CompareDepth--;
        return result;
    }

    private static Value JsonToValueAux(Interpreter i, object? v)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (v)
        {
            case null:
                return ValueNull.Instance;

            case IList<object?> list:
            {
                CachedThunk[] elems = new CachedThunk[list.Count];
                for (int counter = 0; counter < elems.Length; counter++)
                {
                    elems[counter] = CachedThunk.ReadyThunk(JsonToValue(i, list[counter]));
                }
                return new ValueArray(elems);
            }

            case bool b:
                return b ? ValueBoolean.True : ValueBoolean.False;
            case int n:
                return Builtins.MakeDoubleCheck(i, n);
            case sbyte n:
                return Builtins.MakeDoubleCheck(i, n);
            case short n:
                return Builtins.MakeDoubleCheck(i, n);
            case long n:
                return Builtins.MakeDoubleCheck(i, n);
            case double d:
                return Builtins.MakeDoubleCheck(i, d);

            case IDictionary<string, object?> dict:
            {
                Dictionary<string, Value> fieldMap = new(dict.Count);
                foreach (KeyValuePair<string, object?> kv in dict)
                {
                    fieldMap[Util.WellFormed(kv.Key)] = JsonToValue(i, kv.Value);
                }
                return BuildObject(ObjectFieldHide.ObjectFieldInherit, fieldMap);
            }

            case string s:
                return new ValueFlatString(s);

            default:
                throw i.Error("Not a json type: " + Convert.ToString(v, CultureInfo.InvariantCulture));
        }
    }

    [MethodImpl(Interpreter.HotPath)]
    internal Value EvalInCleanEnv(Env env, Node ast, bool trimmable)
    {
        NewCall(env, trimmable);
        int stackSize = Stack.Count;

        Value val = Evaluate(ast, TailCallStatus.TailCall);

        Stack.PopIfExists(stackSize);

        return val;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Value EvaluatePV(CachedThunk ph) => ph.GetValue(this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Value EvaluateTailCall(ValueFunction function, CallArguments args, TailCallStatus tc)
    {
        if (tc == TailCallStatus.TailCall)
        {
            Stack.TailCallTrimStack();
        }
        return function.Call(this, args);
    }

    // Go's i.Error: returns the error so that callers can `throw i.Error(...)`.
    internal RuntimeError Error(string s) => new(s, GetCurrentStackTrace());

    internal RuntimeError TypeErrorSpecific(Value bad, ValType good) =>
        Error("Unexpected type " + bad.Type.Name + ", expected " + good.Name);

    internal RuntimeError TypeErrorGeneral(Value bad) => Error("Unexpected type " + bad.Type.Name);

    // fmt's %v of a float64.
    internal static string FormatFloatV(double f) => Yaml.GoFormat.FormatFloatG(f, 64);

    internal ValueNumber GetNumber(Value val) => val as ValueNumber ?? throw TypeErrorSpecific(val, ValType.Number);

    internal ValueNumber EvaluateNumber(CachedThunk pv) => GetNumber(EvaluatePV(pv));

    internal int GetInt(Value val)
    {
        ValueNumber num = GetNumber(val);
        // We conservatively convert ot int32, so that it can be machine-sized int
        // on any machine. And it's used only for indexing anyway.
        double d = num.Value;
        if (!(d >= int.MinValue && d <= int.MaxValue) || (int)d != d)
        {
            throw Error("Expected an integer, but got " + FormatFloatV(d));
        }
        return (int)d;
    }

    internal int EvaluateInt(CachedThunk pv) => GetInt(EvaluatePV(pv));

    internal long GetInt64(Value val)
    {
        ValueNumber num = GetNumber(val);
        long intNum = Util.GoFloatToInt64(num.Value);
        if ((double)intNum != num.Value)
        {
            throw Error("Expected an integer, but got " + FormatFloatV(num.Value));
        }
        return intNum;
    }

    internal long EvaluateInt64(CachedThunk pv) => GetInt64(EvaluatePV(pv));

    internal ValueString GetString(Value val) => val as ValueString ?? throw TypeErrorSpecific(val, ValType.String);

    internal ValueString EvaluateString(CachedThunk pv) => GetString(EvaluatePV(pv));

    internal ValueBoolean GetBoolean(Value val) => val as ValueBoolean ?? throw TypeErrorSpecific(val, ValType.Boolean);

    internal ValueBoolean EvaluateBoolean(CachedThunk pv) => GetBoolean(EvaluatePV(pv));

    internal ValueArray GetArray(Value val) => val as ValueArray ?? throw TypeErrorSpecific(val, ValType.Array);

    internal ValueArray EvaluateArray(CachedThunk pv) => GetArray(EvaluatePV(pv));

    internal ValueFunction GetFunction(Value val) => val as ValueFunction ?? throw TypeErrorSpecific(val, ValType.Function);

    internal ValueFunction EvaluateFunction(CachedThunk pv) => GetFunction(EvaluatePV(pv));

    internal ValueObject GetObject(Value val) => val as ValueObject ?? throw TypeErrorSpecific(val, ValType.Object);

    internal ValueObject EvaluateObject(CachedThunk pv) => GetObject(EvaluatePV(pv));

    private static ValueObject BuildStdObject(Interpreter i)
    {
        ValueObject objVal = (ValueObject)EvaluateStd(i);
        SimpleObject obj = (SimpleObject)objVal.Uncached;
        foreach (KeyValuePair<string, IEvalCallable> kv in Builtins.FuncBuiltins)
        {
            // TODO(sbarzowski) better way to build function value
            obj.Fields[kv.Key] = new SimpleObjectField(new ReadyValue(new ValueFunction(kv.Value)), ObjectFieldHide.ObjectFieldHidden);
        }
        return objVal;
    }

    private static Value EvaluateStd(Interpreter i)
    {
        // We are bootstrapping std before it is properly available.
        // We need "$std" for desugaring.
        // So, we're creating an empty thunk (evaluating will panic).
        // We will fill it manually before it's evaluated, though.
        // This "std" does not have std.thisFile.
        CachedThunk stdThunk = new(null, null);
        Env beforeStdEnv = new(new BindingFrame { ["$std"] = stdThunk }, SelfBinding.MakeUnbound());
        TraceElement evalTrace = new(LocationRange.MakeMessage("During evaluation of std"), null);
        Node node = StdLib.Ast;
        i.Stack.SetCurrentTrace(evalTrace);
        Value content = i.EvalInCleanEnv(beforeStdEnv, node, false);
        i.Stack.ClearCurrentTrace();
        stdThunk.Content = content;
        return content;
    }

    internal static Dictionary<string, CachedThunk> PrepareExtVars(Interpreter i, Dictionary<string, VmExt> ext, string kind)
    {
        Dictionary<string, CachedThunk> result = new(ext.Count);
        foreach (KeyValuePair<string, VmExt> kv in ext)
        {
            string diagnosticFile = "<" + kind + ":" + kv.Key + ">";
            result[kv.Key] = kv.Value.Kind == ExtKind.Code
                ? ImportCache.CodeToPV(i, diagnosticFile, kv.Value.Value)
                : CachedThunk.ReadyThunk(new ValueFlatString(kv.Value.Value));
        }
        return result;
    }

    internal static ValueObject BuildObject(ObjectFieldHide hide, Dictionary<string, Value> fields)
    {
        Dictionary<string, SimpleObjectField> fieldMap = new(fields.Count);
        foreach (KeyValuePair<string, Value> kv in fields)
        {
            fieldMap[kv.Key] = new SimpleObjectField(new ReadyValue(kv.Value), hide);
        }
        return Objects.MakeValueSimpleObject(new BindingFrame(), fieldMap, null, null);
    }

    // i is assigned before std is evaluated, so the caller can build a stack trace if that fails.
    internal static void BuildInterpreter(Dictionary<string, VmExt> ext, Dictionary<string, JsonnetNativeFunction> nativeFuncs, int maxStack, ImportCache ic, TextWriter traceOut, out Interpreter i)
    {
        i = new Interpreter(maxStack, ic, traceOut, nativeFuncs);
        i.BaseStd = BuildStdObject(i);
        i.ExtVars = PrepareExtVars(i, ext, "extvar");
    }

    internal static Env MakeInitialEnv(string filename, ValueObject baseStd)
    {
        ValueObject fileSpecific = BuildObject(ObjectFieldHide.ObjectFieldHidden, new Dictionary<string, Value>
        {
            ["thisFile"] = new ValueFlatString(filename),
        });

        CachedThunk stdThunk = CachedThunk.ReadyThunk(Objects.MakeValueExtendedObject(baseStd, fileSpecific));

        return new Env(
            new BindingFrame
            {
                ["std"] = stdThunk,
                ["$std"] = stdThunk, // Unavailable to the user. To be used with desugaring.
            },
            SelfBinding.MakeUnbound());
    }

    private static TraceElement ManifestationTrace() => new(LocationRange.MakeMessage("During manifestation"), null);

    private static Value EvaluateAux(Interpreter i, Node node, Dictionary<string, VmExt> tla)
    {
        TraceElement evalTrace = new(LocationRange.MakeMessage("During evaluation"), null);
        Env env = MakeInitialEnv(node.LocRange.FileName, i.BaseStd);
        i.Stack.SetCurrentTrace(evalTrace);
        Value result = i.EvalInCleanEnv(env, node, false);
        i.Stack.ClearCurrentTrace();
        // If it's not a function, ignore TLA
        if (result is ValueFunction f)
        {
            Dictionary<string, CachedThunk> toplevelArgMap = PrepareExtVars(i, tla, "top-level-arg");
            List<NamedCallArgument> named = new(toplevelArgMap.Count);
            foreach (KeyValuePair<string, CachedThunk> kv in toplevelArgMap)
            {
                named.Add(new NamedCallArgument(kv.Key, kv.Value));
            }
            CallArguments args = new(System.Array.Empty<CachedThunk>(), named.ToArray(), false);
            TraceElement funcTrace = new(LocationRange.MakeMessage("Top-level function call"), null);
            i.Stack.SetCurrentTrace(funcTrace);
            result = f.Call(i, args);
            i.Stack.ClearCurrentTrace();
        }
        return result;
    }

    internal static string EvaluateTop(Interpreter i, Node node, Dictionary<string, VmExt> tla, bool stringOutputMode, bool outputNewline)
    {
        Value result = EvaluateAux(i, node, tla);

        StringBuilder buf = new();
        i.Stack.SetCurrentTrace(ManifestationTrace());
        if (stringOutputMode)
        {
            i.ManifestString(buf, result);
        }
        else
        {
            i.ManifestAndSerializeJSON(buf, result, true, "");
        }
        i.Stack.ClearCurrentTrace();
        if (outputNewline)
        {
            buf.Append('\n');
        }
        return Util.ToStringChecked(buf);
    }

    internal static Dictionary<string, string> EvaluateMultiTop(Interpreter i, Node node, Dictionary<string, VmExt> tla, bool stringOutputMode, bool outputNewline)
    {
        Value result = EvaluateAux(i, node, tla);

        i.Stack.SetCurrentTrace(ManifestationTrace());
        Dictionary<string, string> manifested = i.ManifestAndSerializeMulti(result, stringOutputMode, outputNewline);
        i.Stack.ClearCurrentTrace();
        return manifested;
    }

    internal static List<string> EvaluateStreamTop(Interpreter i, Node node, Dictionary<string, VmExt> tla)
    {
        Value result = EvaluateAux(i, node, tla);

        i.Stack.SetCurrentTrace(ManifestationTrace());
        List<string> manifested = i.ManifestAndSerializeYAMLStream(result);
        i.Stack.ClearCurrentTrace();
        return manifested;
    }
}
