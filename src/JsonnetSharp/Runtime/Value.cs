// Ports go-jsonnet value.go.
//
// Go returns (value, error); here errors are thrown (RuntimeError / StaticError), so every
// `value, err := ...` becomes a plain call.

using System.Runtime.CompilerServices;
using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

// Go's valueType. Instances are compared by reference like Go's pointers.
internal sealed class ValType
{
    public readonly string Name;

    private ValType(string name) => Name = name;

    public static readonly ValType String = new("string");
    public static readonly ValType Number = new("number");
    public static readonly ValType Function = new("function");
    public static readonly ValType Object = new("object");
    public static readonly ValType Boolean = new("boolean");
    public static readonly ValType Null = new("null");
    public static readonly ValType Array = new("array");
}

// value represents a concrete jsonnet value of a specific type.
// Various operations on values are allowed, depending on their type.
// All values are of course immutable.
internal abstract class Value
{
    public abstract ValType Type { get; }
}

// A set of variables with associated thunks.
internal sealed class BindingFrame : Dictionary<string, CachedThunk>
{
    public BindingFrame()
    {
    }

    public BindingFrame(int capacity)
        : base(capacity)
    {
    }
}

// Primitive values
// -------------------------------------

// valueString. Jsonnet strings are sequences of code points (Go runes).
internal abstract class ValueString : Value
{
    public sealed override ValType Type => ValType.String;

    // Number of code points.
    public abstract int Length();

    // The code points. Callers must not modify the returned array.
    public abstract int[] GetRunes();

    // The string as UTF-16 (Go's getGoString).
    public abstract string GetGoString();

    public abstract Value Index(Interpreter i, long index);
}

// valueFlatString represents a string value. It keeps the UTF-16 string and/or the code points
// (Go keeps only the []rune) and converts lazily between them.
internal sealed class ValueFlatString : ValueString
{
    private string? _str;
    private int[]? _runes;

    public ValueFlatString(string s) => _str = Util.WellFormed(s);

    public ValueFlatString(int[] runes) => _runes = runes;

    public static readonly ValueFlatString Empty = new("");

    public override Value Index(Interpreter i, long index)
    {
        int[] runes = GetRunes();
        if (0 <= index && index < runes.Length)
        {
            return new ValueFlatString(new[] { runes[index] });
        }
        throw i.Error("Index " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " out of bounds, not within [0, " + runes.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    public override int[] GetRunes() => _runes ??= StringToRunes(_str!);

    public override string GetGoString() => _str ??= RunesToString(_runes!);

    public override int Length() => GetRunes().Length;

    // []rune(s): a lone surrogate becomes U+FFFD like an invalid UTF-8 byte in Go.
    internal static int[] StringToRunes(string s)
    {
        int n = 0;
        for (int k = 0; k < s.Length; k++)
        {
            if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]))
            {
                k++;
            }
            n++;
        }
        int[] r = new int[n];
        if (n == s.Length)
        {
            for (int k = 0; k < n; k++)
            {
                char c = s[k];
                r[k] = char.IsSurrogate(c) ? 0xFFFD : c;
            }
            return r;
        }
        int j = 0;
        for (int k = 0; k < s.Length; k++)
        {
            char c = s[k];
            if (char.IsHighSurrogate(c) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]))
            {
                r[j++] = char.ConvertToUtf32(c, s[k + 1]);
                k++;
            }
            else
            {
                r[j++] = char.IsSurrogate(c) ? 0xFFFD : c;
            }
        }
        return r;
    }

    // string(runes): invalid runes become U+FFFD.
    internal static string RunesToString(int[] runes) => RunesToString(runes, 0, runes.Length);

    internal static string RunesToString(int[] runes, int start, int count)
    {
        if (count > StringTooLongException.MaxLength / 2)
        {
            long units = count;
            for (int k = start; k < start + count; k++)
            {
                if (runes[k] > 0xFFFF && runes[k] <= 0x10FFFF)
                {
                    units++;
                }
            }
            if (units > StringTooLongException.MaxLength)
            {
                throw new StringTooLongException();
            }
        }
        StringBuilder sb = new(count);
        for (int k = start; k < start + count; k++)
        {
            GoUtf8.AppendRune(sb, runes[k]);
        }
        return sb.ToString();
    }
}

// Indirect representation of long strings.
// It consists of two parts, left and right, concatenation of which is the whole string.
internal sealed class ValueStringTree : ValueString
{
    private ValueString _left;
    private ValueString? _right;
    private readonly int _len;

    // TODO(sbarzowski) this was chosen on a hunch, be more systematic about it
    internal const int ExtendedStringMinLength = 30;

    public ValueStringTree(ValueString left, ValueString right, int len)
    {
        _left = left;
        _right = right;
        _len = len;
    }

    // buildFullString, iterative: the tree can be as deep as the number of concatenations.
    private static void BuildFullString(ValueString s, List<int> buf)
    {
        Stack<ValueString> todo = new();
        todo.Push(s);
        while (todo.Count > 0)
        {
            ValueString cur = todo.Pop();
            if (cur is ValueStringTree t)
            {
                if (t._right != null)
                {
                    todo.Push(t._right);
                }
                todo.Push(t._left);
            }
            else
            {
                buf.AddRange(cur.GetRunes());
            }
        }
    }

    private void FlattenToLeft()
    {
        if (_right != null)
        {
            List<int> result = new(_len);
            BuildFullString(this, result);
            _left = new ValueFlatString(result.ToArray());
            _right = null;
        }
    }

    public override Value Index(Interpreter i, long index)
    {
        if (0 <= index && index < _len)
        {
            FlattenToLeft();
            return _left.Index(i, index);
        }
        throw i.Error("Index " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " out of bounds, not within [0, " + Length().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    public override int[] GetRunes()
    {
        FlattenToLeft();
        return _left.GetRunes();
    }

    public override string GetGoString()
    {
        if (_len > StringTooLongException.MaxLength)
        {
            // Checked before flattening: the code points alone would take 4 bytes each.
            throw new StringTooLongException();
        }
        FlattenToLeft();
        return _left.GetGoString();
    }

    public override int Length() => _len;
}

internal sealed class ValueBoolean : Value
{
    public readonly bool Value;

    private ValueBoolean(bool v) => Value = v;

    public static readonly ValueBoolean True = new(true);
    public static readonly ValueBoolean False = new(false);

    public override ValType Type => ValType.Boolean;
}

internal sealed class ValueNumber : Value
{
    public readonly double Value;

    public ValueNumber(double v) => Value = v;

    public override ValType Type => ValType.Number;
}

internal sealed class ValueNull : Value
{
    public static readonly ValueNull Instance = new();

    private ValueNull()
    {
    }

    public override ValType Type => ValType.Null;
}

// ast.Array
// -------------------------------------

internal sealed class ValueArray : Value
{
    public readonly CachedThunk[] Elements;

    public ValueArray(CachedThunk[] elements) => Elements = elements;

    public static readonly ValueArray Empty = new(System.Array.Empty<CachedThunk>());

    [MethodImpl(Interpreter.HotPath)]
    public Value Index(Interpreter i, long index)
    {
        if (0 <= index && index < Elements.Length)
        {
            return i.EvaluatePV(Elements[index]);
        }
        throw i.Error("Index " + index.ToString(System.Globalization.CultureInfo.InvariantCulture) +
            " out of bounds, not within [0, " + Length().ToString(System.Globalization.CultureInfo.InvariantCulture) + ")");
    }

    public int Length() => Elements.Length;

    public override ValType Type => ValType.Array;
}

// ast.Function
// -------------------------------------

internal sealed class ValueFunction : Value
{
    public readonly IEvalCallable Ec;

    public ValueFunction(IEvalCallable ec) => Ec = ec;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public Value Call(Interpreter i, CallArguments args)
    {
        CheckArguments(i, args, Parameters());
        return Ec.EvalCall(args, i);
    }

    public NamedParameter[] Parameters() => Ec.Parameters();

    internal static void CheckArguments(Interpreter i, CallArguments args, NamedParameter[] parameters)
    {
        int numPositional = args.Positional.Length;
        int numNamed = args.Named.Length;
        int maxExpected = parameters.Length;

        if (numPositional > maxExpected)
        {
            throw i.Error("function expected " + maxExpected.ToString(System.Globalization.CultureInfo.InvariantCulture) +
                " positional argument(s), but got " + numPositional.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        // Fast path: positional only and every parameter covered.
        if (numNamed == 0 && numPositional == maxExpected)
        {
            return;
        }

        // Parameter names the function will accept.
        HashSet<string> accepted = new();
        foreach (NamedParameter param in parameters)
        {
            accepted.Add(param.Name);
        }

        // Parameter names the call will bind.
        HashSet<string> received = new();
        for (int k = 0; k < numPositional; k++)
        {
            received.Add(parameters[k].Name);
        }
        foreach (NamedCallArgument arg in args.Named)
        {
            if (received.Contains(arg.Name))
            {
                throw i.Error("Argument " + arg.Name + " already provided");
            }
            if (!accepted.Contains(arg.Name))
            {
                throw i.Error("function has no parameter " + arg.Name);
            }
            received.Add(arg.Name);
        }

        foreach (NamedParameter param in parameters)
        {
            if (!received.Contains(param.Name) && param.DefaultArg == null)
            {
                throw i.Error("Missing argument: " + param.Name);
            }
        }
    }

    public override ValType Type => ValType.Function;
}

// TODO(sbarzowski) better name?
internal interface IEvalCallable
{
    Value EvalCall(CallArguments args, Interpreter i);

    NamedParameter[] Parameters();
}

internal readonly struct NamedParameter
{
    public readonly Node? DefaultArg;
    public readonly string Name;

    public NamedParameter(string name, Node? defaultArg)
    {
        Name = name;
        DefaultArg = defaultArg;
    }
}

internal sealed class CallArguments
{
    public CachedThunk[] Positional;
    public NamedCallArgument[] Named;
    public bool TailStrict;

    public CallArguments(CachedThunk[] positional, NamedCallArgument[] named, bool tailStrict)
    {
        Positional = positional;
        Named = named;
        TailStrict = tailStrict;
    }

    // Go's args(xs...)
    public static CallArguments Args(params CachedThunk[] xs) => new(xs, System.Array.Empty<NamedCallArgument>(), false);
}

internal readonly struct NamedCallArgument
{
    public readonly CachedThunk Pv;
    public readonly string Name;

    public NamedCallArgument(string name, CachedThunk pv)
    {
        Name = name;
        Pv = pv;
    }
}

// Objects
// -------------------------------------

// Object is a value that allows indexing (taking a value of a field)
// and combining through mixin inheritence (operator +).
internal sealed class ValueObject : Value
{
    // null - not checked yet; NoErrorInObjectInvariants - checked (or checking in progress) without
    // error; otherwise the error that was raised.
    private object? _assertionError;
    internal readonly Dictionary<ObjectCacheKey, Value> Cache = new();
    internal readonly UncachedObject Uncached;

    // Hack - we need to distinguish not-checked-yet and no error situations
    // so we have a special value for no error and null means that we don't know yet.
    private static readonly object NoErrorInObjectInvariants = new();

    public ValueObject(UncachedObject uncached) => Uncached = uncached;

    public override ValType Type => ValType.Object;

    [MethodImpl(Interpreter.HotPath)]
    public Value Index(Interpreter i, string field) => Objects.ObjectIndex(i, SelfBinding.ObjectBinding(this), field);

    internal bool AssertionsChecked() => _assertionError != null;

    internal void SetAssertionsCheckResult(Exception? err) => _assertionError = (object?)err ?? NoErrorInObjectInvariants;

    internal void SetAssertionsPassed() => _assertionError = NoErrorInObjectInvariants;

    internal Exception? GetAssertionsCheckResult()
    {
        if (_assertionError == null)
        {
            throw new InvalidOperationException("Assertions not checked yet");
        }
        return _assertionError == NoErrorInObjectInvariants ? null : (Exception)_assertionError;
    }
}

internal readonly struct ObjectCacheKey : IEquatable<ObjectCacheKey>
{
    public readonly string Field;
    public readonly int Depth;

    public ObjectCacheKey(string field, int depth)
    {
        Field = field;
        Depth = depth;
    }

    public bool Equals(ObjectCacheKey other) => Depth == other.Depth && string.Equals(Field, other.Field, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is ObjectCacheKey k && Equals(k);

    public override int GetHashCode() => Field.GetHashCode() * 31 + Depth;
}

internal readonly struct SelfBinding
{
    // self is the lexically nearest object we are in, or null.  Note
    // that this is not the same as context, because we could be inside a function,
    // inside an object and then context would be the function, but self would still point
    // to the object.
    public readonly ValueObject? Self;

    // superDepth is the "super" level of self.  Sometimes, we look upwards in the
    // inheritance tree, e.g. via an explicit use of super, or because a given field
    // has been inherited.  When evaluating a field from one of these super objects,
    // we need to bind self to the concrete object (so self must point
    // there) but uses of super should be resolved relative to the object whose
    // field we are evaluating.  Thus, we keep a second field for that.  This is
    // usually 0, unless we are evaluating a super object's field.
    public readonly int SuperDepth;

    public SelfBinding(ValueObject? self, int superDepth)
    {
        Self = self;
        SuperDepth = superDepth;
    }

    public static SelfBinding MakeUnbound() => new(null, 123456789); // poison value

    public static SelfBinding ObjectBinding(ValueObject obj) => new(obj, 0);

    public SelfBinding Super() => new(Self, SuperDepth + 1);
}

// hidden represents wether to include hidden fields in a lookup.
internal enum Hidden
{
    WithHidden,
    WithoutHidden,
}

internal abstract class UncachedObject
{
    public abstract int InheritanceSize();
}

internal readonly struct ObjectLocal
{
    // Locals may depend on self and super so they are unbound fields and not simply thunks
    public readonly Node Node;
    public readonly string Name;

    public ObjectLocal(string name, Node node)
    {
        Name = name;
        Node = node;
    }
}

// simpleObject represents a flat object (no inheritance).
// Note that it can be used as part of extended objects
// in inheritance using operator +.
//
// Fields are late bound (to object), so they are not values or potentialValues.
// This is important for inheritance, for example:
// Let a = {x: 42} and b = {y: self.x}. Evaluating b.y is an error,
// but (a+b).y evaluates to 42.
internal sealed class SimpleObject : UncachedObject
{
    public readonly BindingFrame UpValues;
    public readonly Dictionary<string, SimpleObjectField> Fields;
    public readonly List<IUnboundField> Asserts;
    public readonly List<ObjectLocal> Locals;

    public SimpleObject(BindingFrame upValues, Dictionary<string, SimpleObjectField> fields, List<IUnboundField> asserts, List<ObjectLocal> locals)
    {
        UpValues = upValues;
        Fields = fields;
        Asserts = asserts;
        Locals = locals;
    }

    public override int InheritanceSize() => 1;
}

internal readonly struct SimpleObjectField
{
    public readonly IUnboundField Field;
    public readonly ObjectFieldHide Hide;

    public SimpleObjectField(IUnboundField field, ObjectFieldHide hide)
    {
        Field = field;
        Hide = hide;
    }
}

// unboundField is a field that doesn't know yet in which object it is.
internal interface IUnboundField
{
    Value Evaluate(Interpreter i, SelfBinding sb, BindingFrame origBinding, string fieldName);

    LocationRange Loc();
}

// extendedObject represents an object created through inheritance (left + right).
// We represent it as the pair of objects. This results in a tree-like structure.
// Example:
// (A + B) + C
//
//	    +
//	   / \
//	  +   C
//	 / \
//	A   B
//
// It is possible to create an arbitrary binary tree.
// Note however, that because + is associative the only thing that matters
// is the order of leafs.
//
// This represenation allows us to implement "+" in O(1),
// but requires going through the tree and trying subsequent leafs for field access.
internal sealed class ExtendedObject : UncachedObject
{
    public readonly UncachedObject Left;
    public readonly UncachedObject Right;
    private readonly int _totalInheritanceSize;

    public ExtendedObject(UncachedObject left, UncachedObject right)
    {
        Left = left;
        Right = right;
        _totalInheritanceSize = left.InheritanceSize() + right.InheritanceSize();
    }

    public override int InheritanceSize() => _totalInheritanceSize;
}

// restrictedObject represents an object created by std.objectRemoveKey.
// It passes through field accesses, restricted to the fields it already knows about.
internal sealed class RestrictedObject : UncachedObject
{
    public readonly UncachedObject Obj;
    public readonly Dictionary<string, ObjectFieldHide> RetainedFields;
    private readonly int _inheritanceSize;

    public RestrictedObject(UncachedObject obj, Dictionary<string, ObjectFieldHide> retainedFields)
    {
        Obj = obj;
        RetainedFields = retainedFields;
        _inheritanceSize = 1 + obj.InheritanceSize();
    }

    public override int InheritanceSize() => _inheritanceSize;
}

// The free functions of value.go that operate on objects.
internal static class Objects
{
    internal static ValueObject MakeValueSimpleObject(BindingFrame b, Dictionary<string, SimpleObjectField> fields, List<IUnboundField>? asserts, List<ObjectLocal>? locals) =>
        new(new SimpleObject(b, fields, asserts ?? new List<IUnboundField>(), locals ?? new List<ObjectLocal>()));

    internal static ValueObject MakeValueExtendedObject(ValueObject left, ValueObject right) =>
        new(new ExtendedObject(left.Uncached, right.Uncached));

    internal static ValueObject MakeValueRestrictedObject(ValueObject obj) =>
        new(new RestrictedObject(obj.Uncached, ObjectFieldsVisibility(obj)));

    private static void CheckAssertionsHelper(Interpreter i, ValueObject obj, UncachedObject? curr, int superDepth)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (curr)
        {
            case ExtendedObject e:
                CheckAssertionsHelper(i, obj, e.Right, superDepth);
                CheckAssertionsHelper(i, obj, e.Left, superDepth + e.Right.InheritanceSize());
                return;
            case RestrictedObject r:
                CheckAssertionsHelper(i, obj, r.Obj, superDepth + 1);
                return;
            case SimpleObject s:
                foreach (IUnboundField assert in s.Asserts)
                {
                    SelfBinding sb = new(obj, superDepth);
                    BindingFrame fieldUpValues = PrepareFieldUpvalues(sb, s.UpValues, s.Locals);
                    assert.Evaluate(i, sb, fieldUpValues, "");
                }
                return;
            case null:
                return;
            default:
                throw new InvalidOperationException("Unknown object type " + curr.GetType());
        }
    }

    internal static void CheckAssertions(Interpreter i, ValueObject obj)
    {
        if (!obj.AssertionsChecked())
        {
            // Assertions may refer to the object that will normally
            // trigger checking of assertions, resulting in an endless recursion.
            // To avoid that, while we check them, we treat them as already passed.
            obj.SetAssertionsPassed();
            try
            {
                CheckAssertionsHelper(i, obj, obj.Uncached, 0);
                obj.SetAssertionsCheckResult(null);
            }
            catch (Exception e) when (e is RuntimeError || e is StaticError)
            {
                obj.SetAssertionsCheckResult(e);
            }
        }
        Exception? err = obj.GetAssertionsCheckResult();
        if (err != null)
        {
            throw err;
        }
    }

    // findField returns a field in object curr, with superDepth at least minSuperDepth
    // It also returns an associated bindingFrame and actual superDepth that the field
    // was found at.
    internal static bool FindField(UncachedObject curr, int minSuperDepth, string f, out SimpleObjectField field, out BindingFrame? frame, out List<ObjectLocal>? locals, out int counter)
    {
        int offset = 0;
        // The right spine is walked in a loop; Go recurses into both children.
        while (true)
        {
            RuntimeHelpers.EnsureSufficientExecutionStack();
            switch (curr)
            {
                case ExtendedObject e:
                {
                    int rightSize = e.Right.InheritanceSize();
                    if (rightSize > minSuperDepth)
                    {
                        if (FindField(e.Right, minSuperDepth, f, out field, out frame, out locals, out counter))
                        {
                            counter += offset;
                            return true;
                        }
                    }
                    curr = e.Left;
                    minSuperDepth -= rightSize;
                    offset += rightSize;
                    continue;
                }
                case RestrictedObject r:
                    if (minSuperDepth == 0)
                    {
                        if (!r.RetainedFields.ContainsKey(f))
                        {
                            field = default;
                            frame = null;
                            locals = null;
                            // Go returns counter 0 here, then the callers add their offsets.
                            counter = offset;
                            return false;
                        }
                    }
                    curr = r.Obj;
                    minSuperDepth -= 1;
                    offset += 1;
                    continue;
                case SimpleObject s:
                    if (minSuperDepth <= 0)
                    {
                        if (s.Fields.TryGetValue(f, out field))
                        {
                            frame = s.UpValues;
                            locals = s.Locals;
                            counter = offset;
                            return true;
                        }
                    }
                    field = default;
                    frame = null;
                    locals = null;
                    counter = offset;
                    return false;
                default:
                    throw new InvalidOperationException("Unknown object type " + curr.GetType());
            }
        }
    }

    internal static BindingFrame PrepareFieldUpvalues(SelfBinding sb, BindingFrame upValues, List<ObjectLocal> locals)
    {
        BindingFrame newUpValues = new(upValues.Count + locals.Count);
        foreach (KeyValuePair<string, CachedThunk> kv in upValues)
        {
            newUpValues[kv.Key] = kv.Value;
        }
        if (locals.Count == 0)
        {
            return newUpValues;
        }
        // The thunks share one environment whose upValues is filled in below.
        Env env = new(null, sb);
        foreach (ObjectLocal l in locals)
        {
            newUpValues[l.Name] = new CachedThunk(env, l.Node);
        }
        env.UpValues = newUpValues;
        return newUpValues;
    }

    [MethodImpl(Interpreter.HotPath)]
    internal static Value ObjectIndex(Interpreter i, SelfBinding sb, string fieldName)
    {
        ValueObject self = sb.Self!;
        CheckAssertions(i, self);
        if (sb.SuperDepth >= self.Uncached.InheritanceSize())
        {
            throw i.Error("Attempt to use super when there is no super class.");
        }

        if (!FindField(self.Uncached, sb.SuperDepth, fieldName, out SimpleObjectField field, out BindingFrame? upValues, out List<ObjectLocal>? locals, out int foundAt))
        {
            throw i.Error("Field does not exist: " + fieldName);
        }

        ObjectCacheKey key = new(fieldName, foundAt);
        if (self.Cache.TryGetValue(key, out Value? cached))
        {
            return cached;
        }

        SelfBinding fieldSelfBinding = new(self, foundAt);
        BindingFrame fieldUpValues = PrepareFieldUpvalues(fieldSelfBinding, upValues!, locals!);

        Value val = field.Field.Evaluate(i, fieldSelfBinding, fieldUpValues, fieldName);
        self.Cache[key] = val;
        return val;
    }

    internal static bool ObjectHasField(SelfBinding sb, string fieldName) =>
        FindField(sb.Self!.Uncached, sb.SuperDepth, fieldName, out _, out _, out _, out _);

    internal static Dictionary<string, ObjectFieldHide> UncachedObjectFieldsVisibility(UncachedObject obj)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (obj)
        {
            case ExtendedObject e:
            {
                Dictionary<string, ObjectFieldHide> r = UncachedObjectFieldsVisibility(e.Left);
                Dictionary<string, ObjectFieldHide> rightMap = UncachedObjectFieldsVisibility(e.Right);
                foreach (KeyValuePair<string, ObjectFieldHide> kv in rightMap)
                {
                    if (kv.Value == ObjectFieldHide.ObjectFieldInherit)
                    {
                        if (!r.ContainsKey(kv.Key))
                        {
                            r[kv.Key] = kv.Value;
                        }
                    }
                    else
                    {
                        r[kv.Key] = kv.Value;
                    }
                }
                return r;
            }
            case RestrictedObject ro:
                return new Dictionary<string, ObjectFieldHide>(ro.RetainedFields);
            case SimpleObject s:
            {
                Dictionary<string, ObjectFieldHide> r = new(s.Fields.Count);
                foreach (KeyValuePair<string, SimpleObjectField> kv in s.Fields)
                {
                    r[kv.Key] = kv.Value.Hide;
                }
                return r;
            }
        }
        return new Dictionary<string, ObjectFieldHide>();
    }

    internal static Dictionary<string, ObjectFieldHide> ObjectFieldsVisibility(ValueObject obj) => UncachedObjectFieldsVisibility(obj.Uncached);

    // Returns field names of an object. Gotcha: the order of fields is unspecified.
    internal static List<string> ObjectFields(ValueObject obj, Hidden h)
    {
        List<string> r = new();
        foreach (KeyValuePair<string, ObjectFieldHide> kv in ObjectFieldsVisibility(obj))
        {
            if (h == Hidden.WithHidden || kv.Value != ObjectFieldHide.ObjectFieldHidden)
            {
                r.Add(kv.Key);
            }
        }
        return r;
    }

    internal static string DuplicateFieldNameErrMsg(string fieldName) => "Duplicate field name: " + Interpreter.UnparseString(fieldName);
}
