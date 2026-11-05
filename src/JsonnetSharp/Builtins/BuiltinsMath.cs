// Ports the numeric builtins of go-jsonnet builtins.go: liftNumeric, liftNumeric2, liftNumericToBoolean
// and the functions built with them (sqrt, hypot, ceil, floor, sin, ..., isDecimal), plus pow.
// Go's math functions come from GoMath (pure-Go ports) wherever .NET's Math could round differently.

namespace JsonnetSharp;

internal static partial class Builtins
{
    private static Value LiftNumeric(Interpreter i, Value x, Func<double, double> f) =>
        MakeDoubleCheck(i, f(i.GetNumber(x).Value));

    private static Value LiftNumeric2(Interpreter i, Value x, Value y, Func<double, double, double> f)
    {
        ValueNumber nx = i.GetNumber(x);
        ValueNumber ny = i.GetNumber(y);
        return MakeDoubleCheck(i, f(nx.Value, ny.Value));
    }

    private static Value LiftNumericToBoolean(Interpreter i, Value x, Func<double, bool> f) =>
        MakeBool(f(i.GetNumber(x).Value));

    // Sqrt is exact in both Go and .NET.
    internal static Value BuiltinSqrt(Interpreter i, Value x) => LiftNumeric(i, x, Math.Sqrt);

    internal static Value BuiltinHypot(Interpreter i, Value x, Value y) => LiftNumeric2(i, x, y, GoMath.Hypot);

    // A zero result keeps the sign of x (ceil(-0.5) is -0), which .NET Framework's Math.Ceiling loses.
    internal static Value BuiltinCeil(Interpreter i, Value x) => LiftNumeric(i, x, static f => SignedZero(Math.Ceiling(f), f));

    internal static Value BuiltinFloor(Interpreter i, Value x) => LiftNumeric(i, x, static f => SignedZero(Math.Floor(f), f));

    private static double SignedZero(double r, double x) => r == 0 ? GoMath.Copysign(0, x) : r;

    internal static Value BuiltinSin(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Sin);

    internal static Value BuiltinCos(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Cos);

    internal static Value BuiltinTan(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Tan);

    internal static Value BuiltinAsin(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Asin);

    internal static Value BuiltinAcos(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Acos);

    internal static Value BuiltinAtan(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Atan);

    internal static Value BuiltinAtan2(Interpreter i, Value y, Value x) => LiftNumeric2(i, y, x, GoMath.Atan2);

    internal static Value BuiltinLog(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.ArchLog);

    internal static Value BuiltinExp(Interpreter i, Value x) => LiftNumeric(i, x, static f =>
    {
        double res = GoMath.ArchExp(f);
        if (res == 0 && f > 0)
        {
            return double.PositiveInfinity;
        }
        return res;
    });

    internal static Value BuiltinMantissa(Interpreter i, Value x) => LiftNumeric(i, x, static f => GoMath.Frexp(f, out _));

    internal static Value BuiltinExponent(Interpreter i, Value x) => LiftNumeric(i, x, static f =>
    {
        GoMath.Frexp(f, out int exponent);
        return exponent;
    });

    internal static Value BuiltinRound(Interpreter i, Value x) => LiftNumeric(i, x, GoMath.Round);

    // math.Modf's integral part is Math.Truncate; C#'s % is math.Mod.
    internal static Value BuiltinIsEven(Interpreter i, Value x) => LiftNumericToBoolean(i, x, static f => Math.Truncate(f) % 2 == 0);

    internal static Value BuiltinIsOdd(Interpreter i, Value x) => LiftNumericToBoolean(i, x, static f => Math.Truncate(f) % 2 != 0);

    internal static Value BuiltinIsInteger(Interpreter i, Value x) => LiftNumericToBoolean(i, x, static f =>
    {
        GoMath.Modf(f, out double frac);
        return frac == 0;
    });

    internal static Value BuiltinIsDecimal(Interpreter i, Value x) => LiftNumericToBoolean(i, x, static f =>
    {
        GoMath.Modf(f, out double frac);
        return frac != 0;
    });

    internal static Value BuiltinPow(Interpreter i, Value basev, Value expv)
    {
        ValueNumber b = i.GetNumber(basev);
        ValueNumber e = i.GetNumber(expv);
        return MakeDoubleCheck(i, GoMath.Pow(b.Value, e.Value));
    }
}
