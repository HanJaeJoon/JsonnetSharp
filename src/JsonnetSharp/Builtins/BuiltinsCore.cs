// Ports the builtins of go-jsonnet builtins.go that the interpreter itself relies on: the operators,
// equality and comparison, toString, the object/array primitives std.jsonnet and the desugarer
// use ($flatMapArray, $objectFlatMerge, objectFieldsEx, objectHasEx, makeArray, ...), extVar and native.

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

internal static partial class Builtins
{
    internal static Value BuiltinPlus(Interpreter i, Value x, Value y)
    {
        // TODO(sbarzowski) perhaps a more elegant way to dispatch
        if (y is ValueString rightStr)
        {
            ValueString left = (ValueString)BuiltinToString(i, x);
            return ConcatStrings(left, rightStr);
        }
        switch (x)
        {
            case ValueNumber left:
            {
                ValueNumber right = i.GetNumber(y);
                return MakeDoubleCheck(i, left.Value + right.Value);
            }
            case ValueString left:
            {
                ValueString right = (ValueString)BuiltinToString(i, y);
                return ConcatStrings(left, right);
            }
            case ValueObject left:
                if (y is ValueObject rightObj)
                {
                    return Objects.MakeValueExtendedObject(left, rightObj);
                }
                throw i.TypeErrorSpecific(y, ValType.Object);
            case ValueArray left:
            {
                ValueArray right = i.GetArray(y);
                return ConcatArrays(left, right);
            }
            default:
                throw i.TypeErrorGeneral(x);
        }
    }

    // concatStrings (value.go)
    internal static ValueString ConcatStrings(ValueString a, ValueString b)
    {
        int aLen = a.Length();
        int bLen = b.Length();
        if ((long)aLen + bLen > StringTooLongException.MaxRunes)
        {
            // Go's lengths are 64-bit; an int would wrap.
            throw new StringTooLongException();
        }
        if (aLen == 0)
        {
            return b;
        }
        else if (bLen == 0)
        {
            return a;
        }
        else if (aLen + bLen < ValueStringTree.ExtendedStringMinLength)
        {
            int[] runesA = a.GetRunes();
            int[] runesB = b.GetRunes();
            int[] result = new int[aLen + bLen];
            System.Array.Copy(runesA, result, aLen);
            System.Array.Copy(runesB, 0, result, aLen, bLen);
            return new ValueFlatString(result);
        }
        return new ValueStringTree(a, b, aLen + bLen);
    }

    // concatArrays (value.go)
    internal static ValueArray ConcatArrays(ValueArray a, ValueArray b)
    {
        CachedThunk[] result = new CachedThunk[a.Elements.Length + b.Elements.Length];
        System.Array.Copy(a.Elements, result, a.Elements.Length);
        System.Array.Copy(b.Elements, 0, result, a.Elements.Length, b.Elements.Length);
        return new ValueArray(result);
    }

    // stringCmp (value.go)
    internal static int StringCmp(ValueString a, ValueString b)
    {
        int[] runesA = a.GetRunes();
        int[] runesB = b.GetRunes();
        int length = runesA.Length < runesB.Length ? runesA.Length : runesB.Length;
        for (int k = 0; k < length; k++)
        {
            if (runesA[k] != runesB[k])
            {
                return Util.RuneCmp(runesA[k], runesB[k]);
            }
        }
        return Util.IntCmp(runesA.Length, runesB.Length);
    }

    // stringEqual (value.go)
    internal static bool StringEqual(ValueString a, ValueString b)
    {
        int[] runesA = a.GetRunes();
        int[] runesB = b.GetRunes();
        if (runesA.Length != runesB.Length)
        {
            return false;
        }
        for (int k = 0; k < runesA.Length; k++)
        {
            if (runesA[k] != runesB[k])
            {
                return false;
            }
        }
        return true;
    }

    internal static Value BuiltinMinus(Interpreter i, Value xv, Value yv)
    {
        ValueNumber x = i.GetNumber(xv);
        ValueNumber y = i.GetNumber(yv);
        return MakeDoubleCheck(i, x.Value - y.Value);
    }

    internal static Value BuiltinMult(Interpreter i, Value xv, Value yv)
    {
        ValueNumber x = i.GetNumber(xv);
        ValueNumber y = i.GetNumber(yv);
        return MakeDoubleCheck(i, x.Value * y.Value);
    }

    internal static Value BuiltinDiv(Interpreter i, Value xv, Value yv)
    {
        ValueNumber x = i.GetNumber(xv);
        ValueNumber y = i.GetNumber(yv);
        if (y.Value == 0)
        {
            throw i.Error("Division by zero.");
        }
        return MakeDoubleCheck(i, x.Value / y.Value);
    }

    internal static Value BuiltinModulo(Interpreter i, Value xv, Value yv)
    {
        ValueNumber x = i.GetNumber(xv);
        ValueNumber y = i.GetNumber(yv);
        if (y.Value == 0)
        {
            throw i.Error("Division by zero.");
        }
        // C#'s % on doubles is IEEE fmod with the sign of the dividend, like Go's math.Mod.
        return MakeDoubleCheck(i, x.Value % y.Value);
    }

    // go-jsonnet compares values by plain recursion and, on a cyclic value (local o = {a: self}; o == o), dies
    // when its 1 GB stack overflows. Filling the 256 MB evaluation thread instead takes tens of seconds, nearly
    // all of it in garbage collections that scan the deep stack and pause the whole process, so the depth is
    // capped well below that. The cap stays far above any acyclic value a program builds in practice.
    internal const int MaxCompareDepth = 100000;

    // Also caps the conversion of a native function's result (a cyclic .NET collection never ends).
    internal static void EnterCompare(Interpreter i)
    {
        if (++i.CompareDepth > MaxCompareDepth)
        {
            // Reported like native stack exhaustion: "max stack frames exceeded."
            throw new InsufficientExecutionStackException();
        }
    }

    internal static int ValueCmp(Interpreter i, Value x, Value y)
    {
        EnterCompare(i);
        int result = ValueCmpAux(i, x, y);
        i.CompareDepth--;
        return result;
    }

    private static int ValueCmpAux(Interpreter i, Value x, Value y)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        switch (x)
        {
            case ValueNumber left:
                return Util.Float64Cmp(left.Value, i.GetNumber(y).Value);
            case ValueString left:
                return StringCmp(left, i.GetString(y));
            case ValueArray left:
                return ArrayCmp(i, left, i.GetArray(y));
            default:
                throw i.TypeErrorGeneral(x);
        }
    }

    internal static int ArrayCmp(Interpreter i, ValueArray x, ValueArray y)
    {
        for (int index = 0; index < Util.MinInt(x.Length(), y.Length()); index++)
        {
            Value left = x.Index(i, index);
            Value right = y.Index(i, index);
            int cmp = ValueCmp(i, left, right);
            if (cmp != 0)
            {
                return cmp;
            }
        }
        return Util.IntCmp(x.Length(), y.Length());
    }

    internal static Value BuiltinLess(Interpreter i, Value x, Value y) => MakeBool(ValueCmp(i, x, y) == -1);

    internal static Value BuiltinGreater(Interpreter i, Value x, Value y) => MakeBool(ValueCmp(i, x, y) == 1);

    internal static Value BuiltinGreaterEq(Interpreter i, Value x, Value y) => MakeBool(ValueCmp(i, x, y) >= 0);

    internal static Value BuiltinLessEq(Interpreter i, Value x, Value y) => MakeBool(ValueCmp(i, x, y) <= 0);

    // makeValueBoolean
    internal static ValueBoolean MakeBool(bool v) => v ? ValueBoolean.True : ValueBoolean.False;

    internal static Value BuiltinLength(Interpreter i, Value x)
    {
        int num;
        switch (x)
        {
            case ValueObject obj:
                num = Objects.ObjectFields(obj, Hidden.WithoutHidden).Count;
                break;
            case ValueArray arr:
                num = arr.Elements.Length;
                break;
            case ValueString s:
                num = s.Length();
                break;
            case ValueFunction f:
                num = 0;
                foreach (NamedParameter param in f.Parameters())
                {
                    if (param.DefaultArg == null)
                    {
                        num++;
                    }
                }
                break;
            default:
                throw i.TypeErrorGeneral(x);
        }
        return new ValueNumber(num);
    }

    internal static string ValueToString(Interpreter i, Value x)
    {
        if (x is ValueString s)
        {
            return s.GetGoString();
        }
        StringBuilder buf = new();
        i.ManifestAndSerializeJSON(buf, x, false, "");
        return Util.ToStringChecked(buf);
    }

    internal static Value BuiltinToString(Interpreter i, Value x)
    {
        if (x is ValueString)
        {
            // valueToString would round-trip it through a Go string; the value is the same.
            return x;
        }
        return new ValueFlatString(ValueToString(i, x));
    }

    internal static Value BuiltinTrace(Interpreter i, Value x, Value y)
    {
        ValueString xStr = i.GetString(x);
        TraceElement trace = i.Stack.CurrentTrace;
        string filename = trace.Loc.File!.DiagnosticFileName;
        int line = trace.Loc.Begin.Line;
        try
        {
            i.TraceOut.Write("TRACE: " + filename + ":" + line.ToString(CultureInfo.InvariantCulture) + " " + xStr.GetGoString() + "\n");
        }
        catch (Exception e) when (!(e is OutOfMemoryException || e is ThreadInterruptedException || e is ThreadAbortException))
        {
            // Go ignores the error fmt.Fprintf returns; a TextWriter reports one by throwing any type.
        }
        return y;
    }

    internal static Value BuiltinMakeArray(Interpreter i, Value szv, Value funcv)
    {
        int sz = i.GetInt(szv);
        ValueFunction fun = i.GetFunction(funcv);
        CachedThunk[] elems = new CachedThunk[sz > 0 ? sz : 0];
        for (int k = 0; k < elems.Length; k++)
        {
            i.PollCancel(k);
            elems[k] = new CachedThunk(Env.Empty, new AstMakeArrayElement(fun, k));
        }
        return new ValueArray(elems);
    }

    internal static Value BuiltinFlatMap(Interpreter i, Value funcv, Value arrv)
    {
        ValueFunction fun = i.GetFunction(funcv);
        switch (arrv)
        {
            case ValueArray arr:
            {
                int num = arr.Length();
                // Start with capacity of the original array.
                // This may spare us a few reallocations.
                // TODO(sbarzowski) verify that it actually helps
                List<CachedThunk> elems = new(num);
                for (int counter = 0; counter < num; counter++)
                {
                    Value returnedValue = fun.Call(i, CallArguments.Args(arr.Elements[counter]));
                    ValueArray returned = i.GetArray(returnedValue);
                    elems.AddRange(returned.Elements);
                }
                return new ValueArray(elems.ToArray());
            }
            case ValueString str:
            {
                StringBuilder sb = new();
                foreach (int elem in str.GetRunes())
                {
                    Value returnedValue = fun.Call(i, CallArguments.Args(CachedThunk.ReadyThunk(new ValueFlatString(new[] { elem }))));
                    ValueString returned = i.GetString(returnedValue);
                    sb.Append(returned.GetGoString());
                }
                return new ValueFlatString(sb.ToString());
            }
            default:
                throw i.Error("std.flatMap second param must be array / string, got " + arrv.Type.Name);
        }
    }

    // builtinFlatMapArray is like builtinFlatMap, but only accepts array as the
    // arrv value. Desugared comprehensions contain a call to this function, rather
    // than builtinFlatMap, so that a better error message is printed when the
    // comprehension would iterate over a non-array.
    internal static Value BuiltinFlatMapArray(Interpreter i, Value funcv, Value arrv)
    {
        if (arrv is ValueArray)
        {
            return BuiltinFlatMap(i, funcv, arrv);
        }
        throw i.TypeErrorSpecific(arrv, ValType.Array);
    }

    internal static Value BuiltinNegation(Interpreter i, Value x) => MakeBool(!i.GetBoolean(x).Value);

    internal static Value BuiltinXnor(Interpreter i, Value xv, Value yv)
    {
        ValueBoolean p = i.GetBoolean(xv);
        ValueBoolean q = i.GetBoolean(yv);
        return MakeBool(p.Value == q.Value);
    }

    internal static Value BuiltinXor(Interpreter i, Value xv, Value yv)
    {
        ValueBoolean p = i.GetBoolean(xv);
        ValueBoolean q = i.GetBoolean(yv);
        return MakeBool(p.Value != q.Value);
    }

    internal static Value BuiltinBitNeg(Interpreter i, Value x)
    {
        ValueNumber n = i.GetNumber(x);
        long intValue = Util.GoFloatToInt64(n.Value);
        return new ValueNumber(~intValue);
    }

    internal static Value BuiltinIdentity(Interpreter i, Value x) => x;

    internal static Value BuiltinUnaryPlus(Interpreter i, Value x) => new ValueNumber(i.GetNumber(x).Value);

    internal static Value BuiltinUnaryMinus(Interpreter i, Value x) => new ValueNumber(-i.GetNumber(x).Value);

    // TODO(sbarzowski) since we have a builtin implementation of equals it's no longer really
    // needed and we should deprecate it eventually
    internal static Value PrimitiveEquals(Interpreter i, Value x, Value y)
    {
        if (x.Type != y.Type)
        {
            return ValueBoolean.False;
        }
        switch (x)
        {
            case ValueBoolean left:
                return MakeBool(left.Value == i.GetBoolean(y).Value);
            case ValueNumber left:
                return MakeBool(left.Value == i.GetNumber(y).Value);
            case ValueString left:
                return MakeBool(StringEqual(left, i.GetString(y)));
            case ValueNull:
                return ValueBoolean.True;
            case ValueFunction:
                throw i.Error("Cannot test equality of functions");
            default:
                throw i.Error("primitiveEquals operates on primitive types, got " + x.Type.Name);
        }
    }

    internal static bool RawEquals(Interpreter i, Value x, Value y)
    {
        EnterCompare(i);
        bool result = RawEqualsAux(i, x, y);
        i.CompareDepth--;
        return result;
    }

    private static bool RawEqualsAux(Interpreter i, Value x, Value y)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (x.Type != y.Type)
        {
            return false;
        }
        switch (x)
        {
            case ValueBoolean left:
                return left.Value == i.GetBoolean(y).Value;
            case ValueNumber left:
                return left.Value == i.GetNumber(y).Value;
            case ValueString left:
                return StringEqual(left, i.GetString(y));
            case ValueNull:
                return true;
            case ValueArray left:
            {
                ValueArray right = i.GetArray(y);
                if (left.Length() != right.Length())
                {
                    return false;
                }
                for (int j = 0; j < left.Elements.Length; j++)
                {
                    Value leftElem = i.EvaluatePV(left.Elements[j]);
                    Value rightElem = i.EvaluatePV(right.Elements[j]);
                    if (!RawEquals(i, leftElem, rightElem))
                    {
                        return false;
                    }
                }
                return true;
            }
            case ValueObject left:
            {
                ValueObject right = i.GetObject(y);
                List<string> leftFields = Objects.ObjectFields(left, Hidden.WithoutHidden);
                List<string> rightFields = Objects.ObjectFields(right, Hidden.WithoutHidden);
                leftFields.Sort(Util.GoStringComparer);
                rightFields.Sort(Util.GoStringComparer);
                if (leftFields.Count != rightFields.Count)
                {
                    return false;
                }
                for (int k = 0; k < leftFields.Count; k++)
                {
                    if (leftFields[k] != rightFields[k])
                    {
                        return false;
                    }
                }
                foreach (string fieldName in leftFields)
                {
                    Value leftField = left.Index(i, fieldName);
                    Value rightField = right.Index(i, fieldName);
                    if (!RawEquals(i, leftField, rightField))
                    {
                        return false;
                    }
                }
                return true;
            }
            case ValueFunction:
                throw i.Error("Cannot test equality of functions");
        }
        throw new InvalidOperationException("Unhandled case in equals " + x + " " + y);
    }

    internal static Value BuiltinEquals(Interpreter i, Value x, Value y) => MakeBool(RawEquals(i, x, y));

    internal static Value BuiltinNotEquals(Interpreter i, Value x, Value y) => MakeBool(!RawEquals(i, x, y));

    internal static Value BuiltinType(Interpreter i, Value x) => new ValueFlatString(x.Type.Name);

    internal static Value MakeDoubleCheck(Interpreter i, double x)
    {
        if (double.IsNaN(x))
        {
            throw i.Error("Not a number");
        }
        if (double.IsInfinity(x))
        {
            throw i.Error("Overflow");
        }
        return new ValueNumber(x);
    }

    // IEEE-754 double precision floats can safely store integers in the range [-2**53+1, 2**53-1].
    // We restrict bitwise operations to arguments in this range, since operating on larger values is
    // likely to be a mistake.
    // https://jsonnet.org/ref/language.html#number
    // See also Javascript Number.{MIN_SAFE_INTEGER,MAX_SAFE_INTEGER}
    private const double MaxSafeIntValue = (1L << 53) - 1;
    private const double MinSafeIntValue = -MaxSafeIntValue;

    private static Value LiftBitwise(Interpreter i, Value xv, Value yv, Func<long, long, long> f, bool positiveRightArg)
    {
        ValueNumber x = i.GetNumber(xv);
        ValueNumber y = i.GetNumber(yv);
        if (x.Value < MinSafeIntValue || x.Value > MaxSafeIntValue)
        {
            throw new RuntimeError(BitwiseRangeMessage(x.Value), i.GetCurrentStackTrace());
        }
        if (y.Value < MinSafeIntValue || y.Value > MaxSafeIntValue)
        {
            throw new RuntimeError(BitwiseRangeMessage(y.Value), i.GetCurrentStackTrace());
        }
        if (positiveRightArg && y.Value < 0)
        {
            throw new RuntimeError("Shift by negative exponent.", i.GetCurrentStackTrace());
        }
        return MakeDoubleCheck(i, f((long)x.Value, (long)y.Value));
    }

    private static string BitwiseRangeMessage(double v) =>
        "Bitwise operator argument " + Interpreter.FormatFloatV(v) + " outside of range [" +
        ((long)MinSafeIntValue).ToString(CultureInfo.InvariantCulture) + ", " +
        ((long)MaxSafeIntValue).ToString(CultureInfo.InvariantCulture) + "]";

    internal static Value BuiltinShiftL(Interpreter i, Value x, Value y) => LiftBitwise(i, x, y, (a, b) => a << (int)(b % 64), true);

    internal static Value BuiltinShiftR(Interpreter i, Value x, Value y) => LiftBitwise(i, x, y, (a, b) => a >> (int)(b % 64), true);

    internal static Value BuiltinBitwiseAnd(Interpreter i, Value x, Value y) => LiftBitwise(i, x, y, (a, b) => a & b, false);

    internal static Value BuiltinBitwiseOr(Interpreter i, Value x, Value y) => LiftBitwise(i, x, y, (a, b) => a | b, false);

    internal static Value BuiltinBitwiseXor(Interpreter i, Value x, Value y) => LiftBitwise(i, x, y, (a, b) => a ^ b, false);

    internal static Value BuiltinObjectFieldsEx(Interpreter i, Value objv, Value includeHiddenV)
    {
        ValueObject obj = i.GetObject(objv);
        ValueBoolean includeHidden = i.GetBoolean(includeHiddenV);
        List<string> fields = Objects.ObjectFields(obj, includeHidden.Value ? Hidden.WithHidden : Hidden.WithoutHidden);
        fields.Sort(Util.GoStringComparer);
        CachedThunk[] elems = new CachedThunk[fields.Count];
        for (int k = 0; k < elems.Length; k++)
        {
            elems[k] = CachedThunk.ReadyThunk(new ValueFlatString(fields[k]));
        }
        return new ValueArray(elems);
    }

    internal static Value BuiltinObjectHasEx(Interpreter i, Value objv, Value fnamev, Value includeHiddenV)
    {
        ValueObject obj = i.GetObject(objv);
        ValueString fname = i.GetString(fnamev);
        ValueBoolean includeHidden = i.GetBoolean(includeHiddenV);
        Hidden h = includeHidden.Value ? Hidden.WithHidden : Hidden.WithoutHidden;

        bool hasField = Objects.ObjectFieldsVisibility(obj).TryGetValue(fname.GetGoString(), out ObjectFieldHide hide);
        hasField = hasField && (h == Hidden.WithHidden || hide != ObjectFieldHide.ObjectFieldHidden);

        return MakeBool(hasField);
    }

    internal static Value BuiltinUglyObjectFlatMerge(Interpreter i, Value x)
    {
        // TODO(sbarzowski) consider keeping comprehensions in AST
        // It will probably be way less hacky, with better error messages and better performance

        ValueArray objarr = i.GetArray(x);
        Dictionary<string, SimpleObjectField> newFields = new();
        foreach (CachedThunk elem in objarr.Elements)
        {
            ValueObject obj = i.EvaluateObject(elem);

            // starts getting ugly - we mess with object internals
            SimpleObject simpleObj = (SimpleObject)obj.Uncached;

            if (simpleObj.Locals.Count > 0)
            {
                throw new InvalidOperationException("Locals should have been desugared in object comprehension.");
            }

            // there is only one field, really
            foreach (KeyValuePair<string, SimpleObjectField> kv in simpleObj.Fields)
            {
                if (newFields.ContainsKey(kv.Key))
                {
                    throw i.Error(Objects.DuplicateFieldNameErrMsg(kv.Key));
                }

                newFields[kv.Key] = new SimpleObjectField(new BindingsUnboundField(kv.Value.Field, simpleObj.UpValues), kv.Value.Hide);
            }
        }

        return Objects.MakeValueSimpleObject(
            new BindingFrame(),
            newFields,
            new List<IUnboundField>(), // No asserts allowed
            null);
    }

    internal static Value BuiltinExtVar(Interpreter i, Value name)
    {
        ValueString str = i.GetString(name);
        string index = str.GetGoString();
        if (i.ExtVars.TryGetValue(index, out CachedThunk? pv))
        {
            return i.EvaluatePV(pv);
        }
        throw i.Error("Undefined external variable: " + index);
    }

    internal static Value BuiltinNative(Interpreter i, Value name)
    {
        ValueString str = i.GetString(name);
        string index = str.GetGoString();
        if (i.NativeFuncs.TryGetValue(index, out JsonnetNativeFunction? f))
        {
            return new ValueFunction(new NativeCallable(f));
        }
        return ValueNull.Instance;
    }

    internal static Value BuiltinObjectRemoveKey(Interpreter i, Value objv, Value keyv)
    {
        ValueObject obj = i.GetObject(objv);
        ValueString key = i.GetString(keyv);

        ValueObject restrictedObj = Objects.MakeValueRestrictedObject(obj);
        ((RestrictedObject)restrictedObj.Uncached).RetainedFields.Remove(key.GetGoString());
        return restrictedObj;
    }

    internal static Value BuiltinIsNull(Interpreter i, Value v) => MakeBool(v is ValueNull);
}
