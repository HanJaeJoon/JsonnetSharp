// Ports the builtin plumbing of go-jsonnet builtins.go: builtin types, flattenArgs, the operator
// tables and the funcBuiltins table. The builtin functions themselves live in the other
// Builtins*.cs partial files.

using JsonnetSharp.Ast;

namespace JsonnetSharp;

internal delegate Value UnaryBuiltinFunc(Interpreter i, Value x);

internal delegate Value BinaryBuiltinFunc(Interpreter i, Value x, Value y);

internal delegate Value TernaryBuiltinFunc(Interpreter i, Value x, Value y, Value z);

internal delegate Value GeneralBuiltinFunc(Interpreter i, Value?[] args);

internal interface IBuiltin : IEvalCallable
{
    string Name { get; }
}

internal sealed class UnaryBuiltin : IBuiltin
{
    public string Name { get; }
    public readonly UnaryBuiltinFunc Function;
    private readonly NamedParameter[] _params;

    public UnaryBuiltin(string name, UnaryBuiltinFunc function, params string[] parameters)
    {
        Name = name;
        Function = function;
        _params = Builtins.MakeParameters(parameters);
    }

    public Value EvalCall(CallArguments args, Interpreter i)
    {
        CachedThunk?[] flatArgs = Builtins.FlattenArgs(args, _params, System.Array.Empty<Value?>());
        Value? x = flatArgs[0]?.GetValue(i);
        return Function(i, x!);
    }

    public NamedParameter[] Parameters() => _params;
}

internal sealed class BinaryBuiltin : IBuiltin
{
    public string Name { get; }
    public readonly BinaryBuiltinFunc Function;
    private readonly NamedParameter[] _params;

    public BinaryBuiltin(string name, BinaryBuiltinFunc function, params string[] parameters)
    {
        Name = name;
        Function = function;
        _params = Builtins.MakeParameters(parameters);
    }

    public Value EvalCall(CallArguments args, Interpreter i)
    {
        CachedThunk?[] flatArgs = Builtins.FlattenArgs(args, _params, System.Array.Empty<Value?>());
        Value? x = flatArgs[0]?.GetValue(i);
        Value? y = flatArgs[1]?.GetValue(i);
        return Function(i, x!, y!);
    }

    public NamedParameter[] Parameters() => _params;
}

internal sealed class TernaryBuiltin : IBuiltin
{
    public string Name { get; }
    public readonly TernaryBuiltinFunc Function;
    private readonly NamedParameter[] _params;

    public TernaryBuiltin(string name, TernaryBuiltinFunc function, params string[] parameters)
    {
        Name = name;
        Function = function;
        _params = Builtins.MakeParameters(parameters);
    }

    public Value EvalCall(CallArguments args, Interpreter i)
    {
        CachedThunk?[] flatArgs = Builtins.FlattenArgs(args, _params, System.Array.Empty<Value?>());
        Value? x = flatArgs[0]?.GetValue(i);
        Value? y = flatArgs[1]?.GetValue(i);
        Value? z = flatArgs[2]?.GetValue(i);
        return Function(i, x!, y!, z!);
    }

    public NamedParameter[] Parameters() => _params;
}

internal readonly struct GeneralBuiltinParameter
{
    // Note that the defaults are passed as values rather than AST nodes like in Parameters.
    // This spares us unnecessary evaluation.
    public readonly Value? DefaultValue;
    public readonly string Name;
    public readonly bool NonValueDefault;

    public GeneralBuiltinParameter(string name, Value? defaultValue = null, bool nonValueDefault = false)
    {
        Name = name;
        DefaultValue = defaultValue;
        NonValueDefault = nonValueDefault;
    }
}

// generalBuiltin covers cases that other builtin structures do not,
// in particular it can have any number of parameters. It can also
// have optional parameters.  The optional ones have non-null defaultValues
// at the same index.
internal sealed class GeneralBuiltin : IBuiltin
{
    // This is not actually used because the defaultValue is used instead.
    // The only reason we don't leave it null is because the checkArguments
    // function uses the non-null status to indicate that the parameter
    // is optional.
    private static readonly LiteralNull OptionalMarker = new();

    public string Name { get; }
    public readonly GeneralBuiltinFunc Function;
    private readonly NamedParameter[] _params;
    private readonly Value?[] _defaults;

    public GeneralBuiltin(string name, GeneralBuiltinFunc function, params GeneralBuiltinParameter[] parameters)
    {
        Name = name;
        Function = function;
        _params = new NamedParameter[parameters.Length];
        _defaults = new Value?[parameters.Length];
        for (int k = 0; k < parameters.Length; k++)
        {
            GeneralBuiltinParameter p = parameters[k];
            bool optional = p.DefaultValue != null || p.NonValueDefault;
            _params[k] = new NamedParameter(p.Name, optional ? OptionalMarker : null);
            _defaults[k] = p.DefaultValue;
        }
    }

    public NamedParameter[] Parameters() => _params;

    public Value EvalCall(CallArguments args, Interpreter i)
    {
        CachedThunk?[] flatArgs = Builtins.FlattenArgs(args, _params, _defaults);
        Value?[] values = new Value?[flatArgs.Length];
        for (int j = 0; j < values.Length; j++)
        {
            values[j] = flatArgs[j]?.GetValue(i);
        }
        return Function(i, values);
    }
}

internal static partial class Builtins
{
    internal static NamedParameter[] MakeParameters(string[] names)
    {
        NamedParameter[] ret = new NamedParameter[names.Length];
        for (int k = 0; k < names.Length; k++)
        {
            ret[k] = new NamedParameter(names[k], null);
        }
        return ret;
    }

    internal static CachedThunk?[] FlattenArgs(CallArguments args, NamedParameter[] parameters, Value?[] defaults)
    {
        CachedThunk?[] flatArgs = new CachedThunk?[parameters.Length];

        // Bind positional arguments
        System.Array.Copy(args.Positional, flatArgs, Math.Min(args.Positional.Length, flatArgs.Length));
        // Bind named arguments
        foreach (NamedCallArgument arg in args.Named)
        {
            for (int k = 0; k < parameters.Length; k++)
            {
                if (parameters[k].Name == arg.Name)
                {
                    flatArgs[k] = arg.Pv;
                    break;
                }
            }
        }
        // Bind defaults for unsatisfied named parameters
        for (int k = 0; k < parameters.Length; k++)
        {
            if (flatArgs[k] == null && k < defaults.Length && defaults[k] != null)
            {
                flatArgs[k] = CachedThunk.ReadyThunk(defaults[k]!);
            }
        }
        return flatArgs;
    }

    // End of builtin utils

    internal static readonly UnaryBuiltin BuiltinID = new("id", BuiltinIdentity, "x");
    internal static readonly ValueFunction FunctionID = new(BuiltinID);

    // Indexed by BinaryOp. Note that % and `in` are desugared instead of being handled here,
    // and && / || are evaluated by the interpreter.
    internal static readonly BinaryBuiltin?[] BopBuiltins = BuildBopBuiltins();

    // Indexed by UnaryOp.
    internal static readonly UnaryBuiltin[] UopBuiltins =
    {
        new("operator!", BuiltinNegation, "x"),
        new("operator~", BuiltinBitNeg, "x"),
        new("operator+ (unary)", BuiltinUnaryPlus, "x"),
        new("operator- (unary)", BuiltinUnaryMinus, "x"),
    };

    private static BinaryBuiltin?[] BuildBopBuiltins()
    {
        BinaryBuiltin?[] b = new BinaryBuiltin?[(int)BinaryOp.BopOr + 1];
        b[(int)BinaryOp.BopMult] = new("operator*", BuiltinMult, "x", "y");
        b[(int)BinaryOp.BopDiv] = new("operator/", BuiltinDiv, "x", "y");

        b[(int)BinaryOp.BopPlus] = new("operator+", BuiltinPlus, "x", "y");
        b[(int)BinaryOp.BopMinus] = new("operator-", BuiltinMinus, "x", "y");

        b[(int)BinaryOp.BopShiftL] = new("operator<<", BuiltinShiftL, "x", "y");
        b[(int)BinaryOp.BopShiftR] = new("operator>>", BuiltinShiftR, "x", "y");

        b[(int)BinaryOp.BopGreater] = new("operator>", BuiltinGreater, "x", "y");
        b[(int)BinaryOp.BopGreaterEq] = new("operator>=", BuiltinGreaterEq, "x", "y");
        b[(int)BinaryOp.BopLess] = new("operator<,", BuiltinLess, "x", "y");
        b[(int)BinaryOp.BopLessEq] = new("operator<=", BuiltinLessEq, "x", "y");

        b[(int)BinaryOp.BopManifestEqual] = new("operator==", BuiltinEquals, "x", "y");
        b[(int)BinaryOp.BopManifestUnequal] = new("operator!=", BuiltinNotEquals, "x", "y"); // Special case

        b[(int)BinaryOp.BopBitwiseAnd] = new("operator&", BuiltinBitwiseAnd, "x", "y");
        b[(int)BinaryOp.BopBitwiseXor] = new("operator^", BuiltinBitwiseXor, "x", "y");
        b[(int)BinaryOp.BopBitwiseOr] = new("operator|", BuiltinBitwiseOr, "x", "y");
        return b;
    }

    private static Dictionary<string, IEvalCallable> BuildBuiltinMap(IBuiltin[] builtins)
    {
        Dictionary<string, IEvalCallable> result = new(builtins.Length);
        foreach (IBuiltin b in builtins)
        {
            result[b.Name] = b;
        }
        return result;
    }

    // Built lazily: static field initializers in different partial files run in unspecified order.
    private static readonly Lazy<Dictionary<string, IEvalCallable>> LazyFuncBuiltins = new(() => BuildBuiltinMap(new IBuiltin[]
    {
        BuiltinID,
        new UnaryBuiltin("extVar", BuiltinExtVar, "x"),
        new UnaryBuiltin("length", BuiltinLength, "x"),
        new UnaryBuiltin("toString", BuiltinToString, "a"),
        new UnaryBuiltin("escapeStringJson", BuiltinEscapeStringJson, "str_"),
        new BinaryBuiltin("trace", BuiltinTrace, "str", "rest"),
        new BinaryBuiltin("makeArray", BuiltinMakeArray, "sz", "func"),
        new BinaryBuiltin("flatMap", BuiltinFlatMap, "func", "arr"),
        new BinaryBuiltin("join", BuiltinJoin, "sep", "arr"),
        new UnaryBuiltin("reverse", BuiltinReverse, "arr"),
        new BinaryBuiltin("filter", BuiltinFilter, "func", "arr"),
        new TernaryBuiltin("foldl", BuiltinFoldl, "func", "arr", "init"),
        new TernaryBuiltin("foldr", BuiltinFoldr, "func", "arr", "init"),
        new BinaryBuiltin("member", BuiltinMember, "arr", "x"),
        new BinaryBuiltin("remove", BuiltinRemove, "arr", "elem"),
        new BinaryBuiltin("removeAt", BuiltinRemoveAt, "arr", "i"),
        new BinaryBuiltin("range", BuiltinRange, "from", "to"),
        new BinaryBuiltin("primitiveEquals", PrimitiveEquals, "x", "y"),
        new BinaryBuiltin("equals", BuiltinEquals, "x", "y"),
        new BinaryBuiltin("objectFieldsEx", BuiltinObjectFieldsEx, "obj", "hidden"),
        new TernaryBuiltin("objectHasEx", BuiltinObjectHasEx, "obj", "fname", "hidden"),
        new BinaryBuiltin("objectRemoveKey", BuiltinObjectRemoveKey, "obj", "key"),
        new UnaryBuiltin("type", BuiltinType, "x"),
        new UnaryBuiltin("char", BuiltinChar, "n"),
        new UnaryBuiltin("codepoint", BuiltinCodepoint, "str"),
        new UnaryBuiltin("ceil", BuiltinCeil, "x"),
        new UnaryBuiltin("floor", BuiltinFloor, "x"),
        new UnaryBuiltin("sqrt", BuiltinSqrt, "x"),
        new BinaryBuiltin("hypot", BuiltinHypot, "x", "y"),
        new UnaryBuiltin("sin", BuiltinSin, "x"),
        new UnaryBuiltin("cos", BuiltinCos, "x"),
        new UnaryBuiltin("tan", BuiltinTan, "x"),
        new UnaryBuiltin("asin", BuiltinAsin, "x"),
        new UnaryBuiltin("acos", BuiltinAcos, "x"),
        new UnaryBuiltin("atan", BuiltinAtan, "x"),
        new BinaryBuiltin("atan2", BuiltinAtan2, "y", "x"),
        new UnaryBuiltin("log", BuiltinLog, "x"),
        new UnaryBuiltin("exp", BuiltinExp, "x"),
        new UnaryBuiltin("mantissa", BuiltinMantissa, "x"),
        new UnaryBuiltin("exponent", BuiltinExponent, "x"),
        new UnaryBuiltin("round", BuiltinRound, "x"),
        new UnaryBuiltin("isEven", BuiltinIsEven, "x"),
        new UnaryBuiltin("isOdd", BuiltinIsOdd, "x"),
        new UnaryBuiltin("isInteger", BuiltinIsInteger, "x"),
        new UnaryBuiltin("isDecimal", BuiltinIsDecimal, "x"),
        new BinaryBuiltin("pow", BuiltinPow, "x", "n"),
        new BinaryBuiltin("modulo", BuiltinModulo, "x", "y"),
        new UnaryBuiltin("md5", BuiltinMd5, "s"),
        new UnaryBuiltin("sha1", BuiltinSha1, "s"),
        new UnaryBuiltin("sha256", BuiltinSha256, "s"),
        new UnaryBuiltin("sha512", BuiltinSha512, "s"),
        new UnaryBuiltin("sha3", BuiltinSha3, "s"),
        new BinaryBuiltin("xnor", BuiltinXnor, "x", "y"),
        new BinaryBuiltin("xor", BuiltinXor, "x", "y"),
        new BinaryBuiltin("lstripChars", BuiltinLstripChars, "str", "chars"),
        new BinaryBuiltin("rstripChars", BuiltinRstripChars, "str", "chars"),
        new BinaryBuiltin("stripChars", BuiltinStripChars, "str", "chars"),
        new TernaryBuiltin("substr", BuiltinSubstr, "str", "from", "len"),
        new TernaryBuiltin("splitLimit", BuiltinSplitLimit, "str", "c", "maxsplits"),
        new TernaryBuiltin("splitLimitR", BuiltinSplitLimitR, "str", "c", "maxsplits"),
        new TernaryBuiltin("strReplace", BuiltinStrReplace, "str", "from", "to"),
        new UnaryBuiltin("isEmpty", BuiltinIsEmpty, "str"),
        new BinaryBuiltin("equalsIgnoreCase", BuiltinEqualsIgnoreCase, "str1", "str2"),
        new UnaryBuiltin("trim", BuiltinTrim, "str"),
        new UnaryBuiltin("base64Decode", BuiltinBase64Decode, "str"),
        new UnaryBuiltin("base64DecodeBytes", BuiltinBase64DecodeBytes, "str"),
        new UnaryBuiltin("parseInt", BuiltinParseInt, "str"),
        new UnaryBuiltin("parseJson", BuiltinParseJSON, "str"),
        new UnaryBuiltin("parseYaml", BuiltinParseYAML, "str"),
        new GeneralBuiltin("manifestJsonEx", BuiltinManifestJSONEx, new("value"), new("indent"),
            new("newline", new ValueFlatString("\n")),
            new("key_val_sep", new ValueFlatString(": "))),
        new GeneralBuiltin("manifestTomlEx", BuiltinManifestTomlEx, new("value"), new("indent")),
        new GeneralBuiltin("manifestYamlDoc", BuiltinManifestYamlDoc,
            new("value"),
            new("indent_array_in_object", ValueBoolean.False),
            new("quote_keys", ValueBoolean.True)),
        new UnaryBuiltin("base64", BuiltinBase64, "input"),
        new UnaryBuiltin("encodeUTF8", BuiltinEncodeUTF8, "str"),
        new UnaryBuiltin("decodeUTF8", BuiltinDecodeUTF8, "arr"),
        new GeneralBuiltin("sort", BuiltinSort, new("arr"), new("keyF", FunctionID)),
        new GeneralBuiltin("minArray", BuiltinMinArray, new("arr"), new("keyF", FunctionID), new("onEmpty", nonValueDefault: true)),
        new GeneralBuiltin("maxArray", BuiltinMaxArray, new("arr"), new("keyF", FunctionID), new("onEmpty", nonValueDefault: true)),
        new UnaryBuiltin("native", BuiltinNative, "x"),
        new UnaryBuiltin("sum", BuiltinSum, "arr"),
        new UnaryBuiltin("avg", BuiltinAvg, "arr"),
        new BinaryBuiltin("contains", BuiltinContains, "arr", "elem"),
        new UnaryBuiltin("isNull", BuiltinIsNull, "x"),

        // internal
        new UnaryBuiltin("$objectFlatMerge", BuiltinUglyObjectFlatMerge, "x"),
        new BinaryBuiltin("$flatMapArray", BuiltinFlatMapArray, "func", "arr"),
    }), LazyThreadSafetyMode.ExecutionAndPublication);

    internal static Dictionary<string, IEvalCallable> FuncBuiltins => LazyFuncBuiltins.Value;
}
