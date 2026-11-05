// Ported from the Go math package (https://github.com/golang/go/tree/master/src/math), via the GoMath.cs of
// the sister project JqSharp: frexp.go, ldexp.go, modf.go, hypot.go, pow.go, fma.go, sin.go,
// tan.go, trig_reduce.go, atan.go, asin.go, atan2.go, floor.go (Round) and the amd64
// assembly of exp and log.
// Copyright 2009 The Go Authors. All rights reserved.
// Use of this source code is governed by a BSD-style license
// that can be found in the Go LICENSE file.
//
// The pure-Go implementations are used so results match go-jsonnet bit for bit. Constant
// expressions that Go folds with exact arithmetic (e.g. 2/Pi) are pre-rounded.

namespace JsonnetSharp;

internal static class GoMath
{
    private const ulong UvNaN = 0x7FF8000000000001;
    private const ulong UvInf = 0x7FF0000000000000;
    private const int Mask = 0x7FF;
    private const int Shift = 64 - 11 - 1;
    private const int Bias = 1023;
    private const ulong SignMask = 1UL << 63;
    private const ulong FracMask = (1UL << Shift) - 1;

    private const double Pi = 3.141592653589793;
    private const double Ln2 = 0.6931471805599453;
    private const double Sqrt2 = 1.4142135623730951;
    private const double InvSqrtPi = 0.5641895835477563;
    private const double TwoOverPi = 0.6366197723675814;
    private const double FourOverPi = 1.2732395447351628;
    private const double InvLn2 = 1.4426950408889634;
    private const double MaxFloat64 = double.MaxValue;

    private static readonly double NaN = Float64frombits(UvNaN);

    internal static ulong Float64bits(double f)
    {
        return unchecked((ulong)BitConverter.DoubleToInt64Bits(f));
    }

    internal static double Float64frombits(ulong b)
    {
        return BitConverter.Int64BitsToDouble(unchecked((long)b));
    }

    private static bool Signbit(double x)
    {
        return (Float64bits(x) & SignMask) != 0;
    }

    private static double Normalize(double x, out int exp)
    {
        const double SmallestNormal = 2.2250738585072014e-308;
        if (Math.Abs(x) < SmallestNormal)
        {
            exp = -52;
            return x * (1L << 52);
        }
        exp = 0;
        return x;
    }

    // --- Go-style shifts and 128-bit helpers (Go yields 0 for shift counts >= 64) ---

    private static ulong ShlG(ulong v, uint n)
    {
        return n >= 64 ? 0 : v << (int)n;
    }

    private static ulong ShrG(ulong v, uint n)
    {
        return n >= 64 ? 0 : v >> (int)n;
    }

    private static int LeadingZeros64(ulong x)
    {
        if (x == 0)
        {
            return 64;
        }
        int n = 0;
        if ((x & 0xFFFFFFFF00000000) == 0) { n += 32; x <<= 32; }
        if ((x & 0xFFFF000000000000) == 0) { n += 16; x <<= 16; }
        if ((x & 0xFF00000000000000) == 0) { n += 8; x <<= 8; }
        if ((x & 0xF000000000000000) == 0) { n += 4; x <<= 4; }
        if ((x & 0xC000000000000000) == 0) { n += 2; x <<= 2; }
        if ((x & 0x8000000000000000) == 0) { n += 1; }
        return n;
    }

    private static ulong Mul64(ulong x, ulong y, out ulong lo)
    {
        unchecked
        {
            ulong x0 = x & 0xFFFFFFFF, x1 = x >> 32;
            ulong y0 = y & 0xFFFFFFFF, y1 = y >> 32;
            ulong w0 = x0 * y0;
            ulong t = x1 * y0 + (w0 >> 32);
            ulong w1 = t & 0xFFFFFFFF;
            ulong w2 = t >> 32;
            w1 += x0 * y1;
            lo = x * y;
            return x1 * y1 + w2 + (w1 >> 32);
        }
    }

    private static ulong Add64(ulong x, ulong y, ulong carry, out ulong carryOut)
    {
        unchecked
        {
            ulong sum = x + y + carry;
            carryOut = ((x & y) | ((x | y) & ~sum)) >> 63;
            return sum;
        }
    }

    // --- Basic functions ---

    internal static double Copysign(double f, double sign)
    {
        return Float64frombits((Float64bits(f) & ~SignMask) | (Float64bits(sign) & SignMask));
    }

    internal static double Frexp(double f, out int exp)
    {
        exp = 0;
        if (f == 0 || double.IsInfinity(f) || double.IsNaN(f))
        {
            return f;
        }
        f = Normalize(f, out exp);
        ulong x = Float64bits(f);
        exp += (int)((x >> Shift) & Mask) - Bias + 1;
        x &= ~((ulong)Mask << Shift);
        x |= (ulong)(-1 + Bias) << Shift;
        return Float64frombits(x);
    }

    internal static double Ldexp(double frac, int exp)
    {
        if (frac == 0 || double.IsInfinity(frac) || double.IsNaN(frac))
        {
            return frac;
        }
        frac = Normalize(frac, out int e);
        exp += e;
        ulong x = Float64bits(frac);
        exp += ((int)(x >> Shift) & Mask) - Bias;
        if (exp < -1075)
        {
            return Copysign(0, frac);
        }
        if (exp > 1023)
        {
            return frac < 0 ? double.NegativeInfinity : double.PositiveInfinity;
        }
        double m = 1;
        if (exp < -1022)
        {
            exp += 53;
            m = 1.0 / (1L << 53);
        }
        x &= ~((ulong)Mask << Shift);
        x |= (ulong)(exp + Bias) << Shift;
        return m * Float64frombits(x);
    }

    internal static double Modf(double x, out double frac)
    {
        double integer = Math.Truncate(x);
        frac = Copysign(x - integer, x);
        return integer;
    }

    internal static double Hypot(double p, double q)
    {
        p = Math.Abs(p);
        q = Math.Abs(q);
        if (double.IsPositiveInfinity(p) || double.IsPositiveInfinity(q))
        {
            return double.PositiveInfinity;
        }
        if (double.IsNaN(p) || double.IsNaN(q))
        {
            return NaN;
        }
        if (p < q)
        {
            double t = p;
            p = q;
            q = t;
        }
        if (p == 0)
        {
            return 0;
        }
        q /= p;
        return p * Math.Sqrt(1 + q * q);
    }

    internal static double Pow(double x, double y)
    {
        if (y == 0 || x == 1)
        {
            return 1;
        }
        if (y == 1)
        {
            return x;
        }
        if (double.IsNaN(x) || double.IsNaN(y))
        {
            return NaN;
        }
        if (x == 0)
        {
            if (y < 0)
            {
                if (Signbit(x) && IsOddInt(y))
                {
                    return double.NegativeInfinity;
                }
                return double.PositiveInfinity;
            }
            if (Signbit(x) && IsOddInt(y))
            {
                return x;
            }
            return 0;
        }
        if (double.IsInfinity(y))
        {
            if (x == -1)
            {
                return 1;
            }
            if ((Math.Abs(x) < 1) == double.IsPositiveInfinity(y))
            {
                return 0;
            }
            return double.PositiveInfinity;
        }
        if (double.IsInfinity(x))
        {
            if (double.IsNegativeInfinity(x))
            {
                return Pow(1 / x, -y);
            }
            return y < 0 ? 0 : double.PositiveInfinity;
        }
        if (y == 0.5)
        {
            return Math.Sqrt(x);
        }
        if (y == -0.5)
        {
            return 1 / Math.Sqrt(x);
        }

        double yi = Modf(Math.Abs(y), out double yf);
        if (yf != 0 && x < 0)
        {
            return NaN;
        }
        if (yi >= 9223372036854775808.0)
        {
            if (x == -1)
            {
                return 1;
            }
            if ((Math.Abs(x) < 1) == (y > 0))
            {
                return 0;
            }
            return double.PositiveInfinity;
        }

        double a1 = 1.0;
        int ae = 0;
        if (yf != 0)
        {
            if (yf > 0.5)
            {
                yf--;
                yi++;
            }
            a1 = ArchExp(yf * ArchLog(x));
        }

        double x1 = Frexp(x, out int xe);
        for (long i = (long)yi; i != 0; i >>= 1)
        {
            if (xe < -(1 << 12) || (1 << 12) < xe)
            {
                ae += xe;
                break;
            }
            if ((i & 1) == 1)
            {
                a1 *= x1;
                ae += xe;
            }
            x1 *= x1;
            xe <<= 1;
            if (x1 < .5)
            {
                x1 += x1;
                xe--;
            }
        }

        if (y < 0)
        {
            a1 = 1 / a1;
            ae = -ae;
        }
        return Ldexp(a1, ae);
    }

    private static bool IsOddInt(double x)
    {
        if (Math.Abs(x) >= (1L << 53))
        {
            return false;
        }
        double xi = Modf(x, out double xf);
        return xf == 0 && ((long)xi & 1) == 1;
    }

    // --- Trigonometric helpers (Go's own Sin/Cos so Gamma/Bessel match) ---

    private const double PI4A = 7.85398125648498535156e-1;
    private const double PI4B = 3.77489470793079817668e-8;
    private const double PI4C = 2.69515142907905952645e-15;
    private const double ReduceThreshold = 1 << 29;

    private static readonly double[] SinCoef =
    {
        1.58962301576546568060e-10,
        -2.50507477628578072866e-8,
        2.75573136213857245213e-6,
        -1.98412698295895385996e-4,
        8.33333333332211858878e-3,
        -1.66666666666666307295e-1,
    };

    private static readonly double[] CosCoef =
    {
        -1.13585365213876817300e-11,
        2.08757008419747316778e-9,
        -2.75573141792967388112e-7,
        2.48015872888517045348e-5,
        -1.38888888888730564116e-3,
        4.16666666666665929218e-2,
    };

    private static readonly ulong[] MPi4 =
    {
        0x0000000000000001,
        0x45f306dc9c882a53,
        0xf84eafa3ea69bb81,
        0xb6c52b3278872083,
        0xfca2c757bd778ac3,
        0x6e48dc74849ba5c0,
        0x0c925dd413a32439,
        0xfc3bd63962534e7d,
        0xd1046bea5d768909,
        0xd338e04d68befc82,
        0x7323ac7306a673e9,
        0x3908bf177bf25076,
        0x3ff12fffbc0b301f,
        0xde5e2316b414da3e,
        0xda6cfd9e4f96136e,
        0x9e8c7ecd3cbfd45a,
        0xea4f758fd7cbe2f6,
        0x7a0e73ef14a525d4,
        0xd7f6bf623f1aba10,
        0xac06608df8f6d757,
    };

    private static double TrigReduce(double x, out ulong j)
    {
        const double Pi4 = Pi / 4;
        if (x < Pi4)
        {
            j = 0;
            return x;
        }
        unchecked
        {
            ulong ix = Float64bits(x);
            int exp = (int)((ix >> Shift) & Mask) - Bias - Shift;
            ix &= ~((ulong)Mask << Shift);
            ix |= 1UL << Shift;
            uint digit = (uint)(exp + 61) / 64;
            uint bitshift = (uint)(exp + 61) % 64;
            ulong z0 = ShlG(MPi4[digit], bitshift) | ShrG(MPi4[digit + 1], 64 - bitshift);
            ulong z1 = ShlG(MPi4[digit + 1], bitshift) | ShrG(MPi4[digit + 2], 64 - bitshift);
            ulong z2 = ShlG(MPi4[digit + 2], bitshift) | ShrG(MPi4[digit + 3], 64 - bitshift);
            ulong z2hi = Mul64(z2, ix, out _);
            ulong z1hi = Mul64(z1, ix, out ulong z1lo);
            ulong z0lo = z0 * ix;
            ulong lo = Add64(z1lo, z2hi, 0, out ulong c);
            ulong hi = Add64(z0lo, z1hi, c, out _);
            j = hi >> 61;
            hi = (hi << 3) | (lo >> 61);
            uint lz = (uint)LeadingZeros64(hi);
            ulong e = (ulong)(Bias - (lz + 1));
            hi = ShlG(hi, lz + 1) | ShrG(lo, 64 - (lz + 1));
            hi >>= 64 - Shift;
            hi |= e << Shift;
            double z = Float64frombits(hi);
            if ((j & 1) == 1)
            {
                j++;
                j &= 7;
                z--;
            }
            return z * Pi4;
        }
    }

    private static double ReduceOctant(double x, out ulong j)
    {
        if (x >= ReduceThreshold)
        {
            return TrigReduce(x, out j);
        }
        j = (ulong)(x * FourOverPi);
        double y = j;
        if ((j & 1) == 1)
        {
            j++;
            y++;
        }
        j &= 7;
        return ((x - y * PI4A) - y * PI4B) - y * PI4C;
    }

    private static double SinPoly(double z, double zz)
    {
        return z + z * zz * ((((((SinCoef[0] * zz) + SinCoef[1]) * zz + SinCoef[2]) * zz + SinCoef[3]) * zz + SinCoef[4]) * zz + SinCoef[5]);
    }

    private static double CosPoly(double zz)
    {
        return 1.0 - 0.5 * zz + zz * zz * ((((((CosCoef[0] * zz) + CosCoef[1]) * zz + CosCoef[2]) * zz + CosCoef[3]) * zz + CosCoef[4]) * zz + CosCoef[5]);
    }

    internal static double Cos(double x)
    {
        if (double.IsNaN(x) || double.IsInfinity(x))
        {
            return NaN;
        }
        bool sign = false;
        x = Math.Abs(x);
        double z = ReduceOctant(x, out ulong j);
        if (j > 3)
        {
            j -= 4;
            sign = !sign;
        }
        if (j > 1)
        {
            sign = !sign;
        }
        double zz = z * z;
        double y = j == 1 || j == 2 ? SinPoly(z, zz) : CosPoly(zz);
        return sign ? -y : y;
    }

    internal static double Sin(double x)
    {
        if (x == 0 || double.IsNaN(x))
        {
            return x;
        }
        if (double.IsInfinity(x))
        {
            return NaN;
        }
        bool sign = false;
        if (x < 0)
        {
            x = -x;
            sign = true;
        }
        double z = ReduceOctant(x, out ulong j);
        if (j > 3)
        {
            sign = !sign;
            j -= 4;
        }
        double zz = z * z;
        double y = j == 1 || j == 2 ? CosPoly(zz) : SinPoly(z, zz);
        return sign ? -y : y;
    }


    private static readonly double[] TanP =
    {
        -1.30936939181383777646e4,
        1.15351664838587416140e6,
        -1.79565251976484877988e7,
    };

    private static readonly double[] TanQ =
    {
        1.0,
        1.36812963470692954678e4,
        -1.32089234440210967447e6,
        2.50083801823357915839e7,
        -5.38695755929454629881e7,
    };

    internal static double Tan(double x)
    {
        if (x == 0 || double.IsNaN(x))
        {
            return x;
        }
        if (double.IsInfinity(x))
        {
            return NaN;
        }
        bool sign = false;
        if (x < 0)
        {
            x = -x;
            sign = true;
        }
        double z = ReduceOctant(x, out ulong j);
        double zz = z * z;
        double y;
        if (zz > 1e-14)
        {
            y = z + z * (zz * (((TanP[0] * zz) + TanP[1]) * zz + TanP[2]) / ((((zz + TanQ[1]) * zz + TanQ[2]) * zz + TanQ[3]) * zz + TanQ[4]));
        }
        else
        {
            y = z;
        }
        if ((j & 2) == 2)
        {
            y = -1 / y;
        }
        return sign ? -y : y;
    }

    // xatan evaluates a series valid in the range [0, 0.66].
    private static double Xatan(double x)
    {
        const double P0 = -8.750608600031904122785e-01;
        const double P1 = -1.615753718733365076637e+01;
        const double P2 = -7.500855792314704667340e+01;
        const double P3 = -1.228866684490136173410e+02;
        const double P4 = -6.485021904942025371773e+01;
        const double Q0 = +2.485846490142306297962e+01;
        const double Q1 = +1.650270098316988542046e+02;
        const double Q2 = +4.328810604912902668951e+02;
        const double Q3 = +4.853903996359136964868e+02;
        const double Q4 = +1.945506571482613964425e+02;
        double z = x * x;
        z = z * ((((P0 * z + P1) * z + P2) * z + P3) * z + P4) / (((((z + Q0) * z + Q1) * z + Q2) * z + Q3) * z + Q4);
        z = x * z + x;
        return z;
    }

    // satan reduces its argument (known to be positive) to the range [0, 0.66] and calls xatan.
    private static double Satan(double x)
    {
        const double Morebits = 6.123233995736765886130e-17; // pi/2 = PIO2 + Morebits
        const double Tan3pio8 = 2.41421356237309504880; // tan(3*pi/8)
        if (x <= 0.66)
        {
            return Xatan(x);
        }
        if (x > Tan3pio8)
        {
            return PiOver2 - Xatan(1 / x) + Morebits;
        }
        return PiOver4 + Xatan((x - 1) / (x + 1)) + 0.5 * Morebits;
    }

    private const double PiOver2 = 1.5707963267948966;
    private const double PiOver4 = 0.7853981633974483;

    internal static double Atan(double x)
    {
        if (x == 0)
        {
            return x;
        }
        return x > 0 ? Satan(x) : -Satan(-x);
    }

    internal static double Asin(double x)
    {
        if (x == 0)
        {
            return x;
        }
        bool sign = false;
        if (x < 0)
        {
            x = -x;
            sign = true;
        }
        if (x > 1)
        {
            return NaN;
        }
        double temp = Math.Sqrt(1 - x * x);
        temp = x > 0.7 ? PiOver2 - Satan(temp / x) : Satan(x / temp);
        return sign ? -temp : temp;
    }

    internal static double Acos(double x) => PiOver2 - Asin(x);

    internal static double Atan2(double y, double x)
    {
        if (double.IsNaN(y) || double.IsNaN(x))
        {
            return NaN;
        }
        if (y == 0)
        {
            if (x >= 0 && !Signbit(x))
            {
                return Copysign(0, y);
            }
            return Copysign(Pi, y);
        }
        if (x == 0)
        {
            return Copysign(PiOver2, y);
        }
        if (double.IsInfinity(x))
        {
            if (double.IsPositiveInfinity(x))
            {
                return double.IsInfinity(y) ? Copysign(PiOver4, y) : Copysign(0, y);
            }
            return double.IsInfinity(y) ? Copysign(2.356194490192345, y) : Copysign(Pi, y);
        }
        if (double.IsInfinity(y))
        {
            return Copysign(PiOver2, y);
        }
        double q = Atan(y / x);
        if (x < 0)
        {
            return q <= 0 ? q + Pi : q - Pi;
        }
        return q;
    }

    internal static double Round(double x)
    {
        const ulong UvOne = 0x3FF0000000000000;
        ulong bits = Float64bits(x);
        uint e = (uint)(bits >> Shift) & Mask;
        if (e < Bias)
        {
            // Round abs(x) < 1 including denormals.
            bits &= SignMask; // +-0
            if (e == Bias - 1)
            {
                bits |= UvOne; // +-1
            }
        }
        else if (e < Bias + Shift)
        {
            // Round any abs(x) >= 1 containing a fractional component [0,1).
            const ulong Half = 1UL << (Shift - 1);
            e -= Bias;
            bits += Half >> (int)e;
            bits &= ~(FracMask >> (int)e);
        }
        return Float64frombits(bits);
    }

    // --- amd64 assembly versions (exp_amd64.s, log_amd64.s), which go-jsonnet's amd64 builds use ---

    // archExp from exp_amd64.s: a Taylor series on x/16 squared four times (after Shibata's SLEEF).
    // shortcut: ports only the avxfma path (useFMA, CPUs with AVX2 and FMA); go-jsonnet on an older
    // CPU takes the unfused path and can differ in the last bit.
    internal static double ArchExp(double x)
    {
        const double Log2e = 1.4426950408889634073599246810018920;
        const double Ln2U = 0.69314718055966295651160180568695068359375;
        const double Ln2L = 0.28235290563031577122588448175013436025525412068e-12;
        const double Overflow = 7.09782712893384e+02;
        if (double.IsNaN(x) || double.IsPositiveInfinity(x))
        {
            return x;
        }
        if (double.IsNegativeInfinity(x))
        {
            return 0;
        }
        if (x > Overflow)
        {
            return double.PositiveInfinity;
        }
        // CVTSD2SL rounds to nearest even.
        int k = (int)Math.Round(Log2e * x, MidpointRounding.ToEven);
        double kf = k;
        x = FMA(-kf, Ln2U, x);
        x = FMA(-kf, Ln2L, x);
        // reduce argument
        x *= 0.0625;
        // Taylor series evaluation
        double y = 2.4801587301587301587e-5;
        y = FMA(y, x, 1.9841269841269841270e-4);
        y = FMA(y, x, 1.3888888888888888889e-3);
        y = FMA(y, x, 8.3333333333333333333e-3);
        y = FMA(y, x, 4.1666666666666666667e-2);
        y = FMA(y, x, 1.6666666666666666667e-1);
        y = FMA(y, x, 0.5);
        y = FMA(y, x, 1.0);
        y *= x;
        y *= 2.0 + y;
        y *= 2.0 + y;
        y *= 2.0 + y;
        y = FMA(2.0 + y, y, 1.0);
        int e = k + 0x3FF;
        if (e <= 0)
        {
            if (e < -52)
            {
                return 0;
            }
            y *= Float64frombits((ulong)(e + 0x3FE) << 52);
            e = 1;
        }
        else if (e >= 0x7FF)
        {
            return double.PositiveInfinity;
        }
        return y * Float64frombits((ulong)e << 52);
    }

    // archLog from log_amd64.s: the same algorithm as Log, but its inline frexp does not normalize
    // subnormal inputs.
    internal static double ArchLog(double x)
    {
        const double Ln2Hi = 6.93147180369123816490e-01;
        const double Ln2Lo = 1.90821492927058770002e-10;
        const double L1 = 6.666666666666735130e-01;
        const double L2 = 3.999999999940941908e-01;
        const double L3 = 2.857142874366239149e-01;
        const double L4 = 2.222219843214978396e-01;
        const double L5 = 1.818357216161805012e-01;
        const double L6 = 1.531383769920937332e-01;
        const double L7 = 1.479819860511658591e-01;
        if (double.IsNaN(x) || double.IsPositiveInfinity(x))
        {
            return x;
        }
        if (x == 0)
        {
            return double.NegativeInfinity;
        }
        if (x < 0)
        {
            return NaN;
        }
        ulong bits = Float64bits(x);
        double f1 = Float64frombits((bits & FracMask) | 0x3FE0000000000000);
        double k = (int)((bits >> Shift) & Mask) - 0x3FE;
        if (f1 < Sqrt2 / 2)
        {
            f1 *= 2;
            k--;
        }
        double f = f1 - 1;
        double s = f / (2 + f);
        double s2 = s * s;
        double s4 = s2 * s2;
        double t1 = s2 * (L1 + s4 * (L3 + s4 * (L5 + s4 * L7)));
        double t2 = s4 * (L2 + s4 * (L4 + s4 * L6));
        double r = t1 + t2;
        double hfsq = 0.5 * f * f;
        return k * Ln2Hi - ((hfsq - (s * (hfsq + r) + k * Ln2Lo)) - f);
    }

    // --- Fused multiply-add (software, exact) ---

    private static ulong Zero(ulong x)
    {
        return x == 0 ? 1UL : 0UL;
    }

    private static ulong Nonzero(ulong x)
    {
        return x != 0 ? 1UL : 0UL;
    }

    private static void Shl128(ulong u1, ulong u2, uint n, out ulong r1, out ulong r2)
    {
        unchecked
        {
            r1 = ShlG(u1, n) | ShrG(u2, 64 - n) | ShlG(u2, n - 64);
            r2 = ShlG(u2, n);
        }
    }

    private static void Shr128(ulong u1, ulong u2, uint n, out ulong r1, out ulong r2)
    {
        unchecked
        {
            r2 = ShrG(u2, n) | ShlG(u1, 64 - n) | ShrG(u1, n - 64);
            r1 = ShrG(u1, n);
        }
    }

    private static void ShrCompress(ulong u1, ulong u2, uint n, out ulong r1, out ulong r2)
    {
        unchecked
        {
            if (n == 0)
            {
                r1 = u1;
                r2 = u2;
            }
            else if (n == 64)
            {
                r1 = 0;
                r2 = u1 | Nonzero(u2);
            }
            else if (n >= 128)
            {
                r1 = 0;
                r2 = Nonzero(u1 | u2);
            }
            else if (n < 64)
            {
                Shr128(u1, u2, n, out r1, out r2);
                r2 |= Nonzero(u2 & ((1UL << (int)n) - 1));
            }
            else
            {
                Shr128(u1, u2, n, out r1, out r2);
                r2 |= Nonzero((u1 & ((1UL << (int)(n - 64)) - 1)) | u2);
            }
        }
    }

    private static int Lz(ulong u1, ulong u2)
    {
        int l = LeadingZeros64(u1);
        if (l == 64)
        {
            l += LeadingZeros64(u2);
        }
        return l;
    }

    private static void Split(ulong b, out uint sign, out int exp, out ulong mantissa)
    {
        sign = (uint)(b >> 63);
        exp = (int)(b >> 52) & Mask;
        mantissa = b & FracMask;
        if (exp == 0)
        {
            int shift = LeadingZeros64(mantissa) - 11;
            mantissa <<= shift;
            exp = 1 - shift;
        }
        else
        {
            mantissa |= 1UL << 52;
        }
    }

    internal static double FMA(double x, double y, double z)
    {
        ulong bx = Float64bits(x), by = Float64bits(y), bz = Float64bits(z);

        if (x == 0.0 || y == 0.0 || (bx & UvInf) == UvInf || (by & UvInf) == UvInf)
        {
            return x * y + z;
        }
        if (z == 0.0)
        {
            return x * y;
        }
        if ((bz & UvInf) == UvInf)
        {
            return z;
        }

        unchecked
        {
            Split(bx, out uint xs, out int xe, out ulong xm);
            Split(by, out uint ys, out int ye, out ulong ym);
            Split(bz, out uint zs, out int ze, out ulong zm);

            int pe = xe + ye - Bias + 1;
            ulong pm1 = Mul64(xm << 10, ym << 11, out ulong pm2);
            ulong zm1 = zm << 10, zm2 = 0;
            uint ps = xs ^ ys;

            uint is62zero = (uint)((~pm1 >> 62) & 1);
            Shl128(pm1, pm2, is62zero, out pm1, out pm2);
            pe -= (int)is62zero;

            if (pe < ze || pe == ze && pm1 < zm1)
            {
                uint ts = ps; ps = zs; zs = ts;
                int te = pe; pe = ze; ze = te;
                ulong t1 = pm1; pm1 = zm1; zm1 = t1;
                ulong t2 = pm2; pm2 = zm2; zm2 = t2;
            }

            if (ps != zs && pe == ze && pm1 == zm1 && pm2 == zm2)
            {
                return 0;
            }

            ShrCompress(zm1, zm2, (uint)(pe - ze), out zm1, out zm2);

            ulong m;
            if (ps == zs)
            {
                pm2 = Add64(pm2, zm2, 0, out ulong c);
                pm1 = Add64(pm1, zm1, c, out _);
                pe -= (int)(~pm1 >> 63);
                ShrCompress(pm1, pm2, (uint)(64 + (pm1 >> 63)), out pm1, out m);
            }
            else
            {
                pm2 = Sub64(pm2, zm2, 0, out ulong c);
                pm1 = Sub64(pm1, zm1, c, out _);
                int nz = Lz(pm1, pm2);
                pe -= nz;
                Shl128(pm1, pm2, (uint)(nz - 1), out m, out pm2);
                m |= Nonzero(pm2);
            }

            if (pe > 1022 + Bias || pe == 1022 + Bias && ((m + (1UL << 9)) >> 63) == 1)
            {
                return Float64frombits(((ulong)ps << 63) | UvInf);
            }
            if (pe < 0)
            {
                uint n = (uint)(-pe);
                m = ShrG(m, n) | Nonzero(m & (ShlG(1, n) - 1));
                pe = 0;
            }
            m = ((m + (1UL << 9)) >> 10) & ~Zero((m & ((1UL << 10) - 1)) ^ (1UL << 9));
            pe &= -(int)Nonzero(m);
            return Float64frombits(((ulong)ps << 63) + ((ulong)pe << 52) + m);
        }
    }

    private static ulong Sub64(ulong x, ulong y, ulong borrow, out ulong borrowOut)
    {
        unchecked
        {
            ulong diff = x - y - borrow;
            borrowOut = ((~x & y) | (~(x ^ y) & diff)) >> 63;
            return diff;
        }
    }
}
