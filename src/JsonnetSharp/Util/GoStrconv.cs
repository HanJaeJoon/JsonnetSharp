// The parts of Go's strconv (and fmt's %f) the builtins need: FormatFloat(f, 'f', prec, 64),
// FormatFloat(f, 'f', -1, 64), ParseInt(s, 10, 64), Atoi and the syntax and range checks of ParseFloat.

using System.Globalization;
using System.Numerics;
using System.Text;

namespace JsonnetSharp;

internal static class GoStrconv
{
    // strconv.FormatFloat(v, 'f', prec, 64), rounded half to even on the exact value.
    internal static string FormatFloatF(double v, int prec)
    {
        if (NonFinite(v) is string special)
        {
            return special;
        }
        long bits = BitConverter.DoubleToInt64Bits(v);
        bool neg = bits < 0;
        int exp = (int)((bits >> 52) & 0x7FF);
        long mant = bits & 0xFFFFFFFFFFFFFL;
        if (exp == 0)
        {
            exp = 1;
        }
        else
        {
            mant |= 1L << 52;
        }
        exp -= 1075;

        BigInteger q;
        BigInteger scaled = new BigInteger(mant) * BigInteger.Pow(10, prec);
        if (exp >= 0)
        {
            q = scaled << exp;
        }
        else
        {
            BigInteger den = BigInteger.One << -exp;
            q = BigInteger.DivRem(scaled, den, out BigInteger r);
            int c = (r * 2).CompareTo(den);
            if (c > 0 || (c == 0 && !q.IsEven))
            {
                q += 1;
            }
        }

        string digits = q.ToString(CultureInfo.InvariantCulture).PadLeft(prec + 1, '0');
        StringBuilder sb = new();
        if (neg)
        {
            sb.Append('-');
        }
        sb.Append(digits, 0, digits.Length - prec);
        if (prec > 0)
        {
            sb.Append('.').Append(digits, digits.Length - prec, prec);
        }
        return sb.ToString();
    }

    // strconv.FormatFloat(v, 'f', -1, 64) for a finite v: the shortest round-trip digits in %f layout.
    internal static string FormatFloatShortestF(double v)
    {
        string g = Yaml.GoFormat.FormatFloatG(v, 64);
        bool neg = g[0] == '-';
        if (neg)
        {
            g = g.Substring(1);
        }
        int e = g.IndexOf('e');
        int exp10 = 0;
        string mant = g;
        if (e >= 0)
        {
            exp10 = int.Parse(g.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
            mant = g.Substring(0, e);
        }
        int dot = mant.IndexOf('.');
        string digits = dot < 0 ? mant : mant.Remove(dot, 1);
        int point = (dot < 0 ? mant.Length : dot) + exp10;
        int lead = 0;
        while (lead < digits.Length - 1 && digits[lead] == '0')
        {
            lead++;
        }
        digits = digits.Substring(lead);
        point -= lead;

        StringBuilder sb = new();
        if (neg)
        {
            sb.Append('-');
        }
        if (digits == "0")
        {
            return sb.Append('0').ToString();
        }
        if (point <= 0)
        {
            return sb.Append("0.").Append('0', -point).Append(digits).ToString();
        }
        if (point >= digits.Length)
        {
            return sb.Append(digits).Append('0', point - digits.Length).ToString();
        }
        return sb.Append(digits, 0, point).Append('.').Append(digits, point, digits.Length - point).ToString();
    }

    // strconv.ParseInt(s, 10, 64).
    internal static bool ParseInt64(string s, out long result)
    {
        result = 0;
        int k = 0;
        bool neg = false;
        if (s.Length > 0 && (s[0] == '+' || s[0] == '-'))
        {
            neg = s[0] == '-';
            k = 1;
        }
        if (k == s.Length)
        {
            return false;
        }
        ulong n = 0;
        const ulong Cutoff = 1UL << 63;
        for (; k < s.Length; k++)
        {
            char c = s[k];
            if (c < '0' || c > '9')
            {
                return false;
            }
            if (n > (Cutoff - (ulong)(c - '0')) / 10)
            {
                return false;
            }
            n = n * 10 + (ulong)(c - '0');
        }
        if (!neg && n >= Cutoff)
        {
            return false;
        }
        result = neg ? unchecked(-(long)n) : (long)n;
        return true;
    }

    // Whether strconv.ParseFloat(s, 64) returns no error: valid Go float syntax (decimal or hex,
    // underscores between digits, inf/infinity/nan) and no overflow.
    internal static bool ParseFloatOk(string s)
    {
        if (Special(s))
        {
            return true;
        }
        int i = 0;
        if (i < s.Length && (s[i] == '+' || s[i] == '-'))
        {
            i++;
        }
        bool hex = i + 2 < s.Length && s[i] == '0' && (s[i + 1] == 'x' || s[i + 1] == 'X');
        if (hex)
        {
            i += 2;
        }
        bool underscores = false;
        bool sawdot = false;
        bool sawdigits = false;
        StringBuilder mantissa = new();
        int fracDigits = 0;
        for (; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '_')
            {
                underscores = true;
                continue;
            }
            if (c == '.')
            {
                if (sawdot)
                {
                    break;
                }
                sawdot = true;
                continue;
            }
            if ((c >= '0' && c <= '9') || (hex && IsHexLetter(c)))
            {
                sawdigits = true;
                mantissa.Append(c);
                if (sawdot)
                {
                    fracDigits++;
                }
                continue;
            }
            break;
        }
        if (!sawdigits)
        {
            return false;
        }
        long exp = 0;
        char expChar = hex ? 'p' : 'e';
        if (i < s.Length && char.ToLowerInvariant(s[i]) == expChar)
        {
            i++;
            if (i >= s.Length)
            {
                return false;
            }
            int esign = 1;
            if (s[i] == '+')
            {
                i++;
            }
            else if (s[i] == '-')
            {
                i++;
                esign = -1;
            }
            if (i >= s.Length || s[i] < '0' || s[i] > '9')
            {
                return false;
            }
            for (; i < s.Length && ((s[i] >= '0' && s[i] <= '9') || s[i] == '_'); i++)
            {
                if (s[i] == '_')
                {
                    underscores = true;
                    continue;
                }
                if (exp < 10000)
                {
                    exp = exp * 10 + (s[i] - '0');
                }
            }
            exp *= esign;
        }
        else if (hex)
        {
            // Must have exponent.
            return false;
        }
        if (i != s.Length)
        {
            return false;
        }
        if (underscores && !UnderscoreOk(s))
        {
            return false;
        }

        // Range check: only overflow is an error, underflow rounds to zero.
        if (hex)
        {
            // shortcut: approximate magnitude; values within an ulp of MaxFloat64 may be misjudged.
            double m = 0;
            foreach (char c in mantissa.ToString())
            {
                m = m * 16 + Convert.ToInt32(c.ToString(), 16);
            }
            double scaledHex = m * Math.Pow(2, exp - 4 * fracDigits);
            return !double.IsInfinity(scaledHex);
        }
        string clean = s.Replace("_", "");
        return TryParseFloat(clean, out double d) && !double.IsInfinity(d);
    }

    // Go prints non-finite floats as +Inf, -Inf and NaN in every verb (%f, %g, %v, strconv 'f' and 'g').
    internal static string? NonFinite(double v) =>
        double.IsNaN(v) ? "NaN" : double.IsPositiveInfinity(v) ? "+Inf" : double.IsNegativeInfinity(v) ? "-Inf" : null;

    // double.TryParse(s, NumberStyles.Float, InvariantCulture), correctly rounded on every runtime like
    // strconv.ParseFloat. Overflow gives +/-Inf.
    internal static bool TryParseFloat(string s, out double d)
    {
#if NET
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
#else
        // .NET Framework's parser is not correctly rounded (fixed in .NET Core 3.0).
        if (!SplitDecimal(s, out bool neg, out string digits, out long exp10))
        {
            return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d);
        }
        d = BitConverter.Int64BitsToDouble(DecimalToBinary(digits, exp10, 52, 1023));
        if (neg)
        {
            d = -d;
        }
        return true;
#endif
    }

#if !NET
    // [+-]digits[.digits][(e|E)[+-]digits] as significant digits (no leading zeros) times 10^exp10.
    private static bool SplitDecimal(string s, out bool neg, out string digits, out long exp10)
    {
        neg = false;
        digits = "";
        exp10 = 0;
        int i = 0;
        int n = s.Length;
        if (i < n && (s[i] == '+' || s[i] == '-'))
        {
            neg = s[i] == '-';
            i++;
        }
        StringBuilder sb = new();
        bool sawDigit = false;
        bool sawDot = false;
        for (; i < n; i++)
        {
            char c = s[i];
            if (c >= '0' && c <= '9')
            {
                sawDigit = true;
                if (sb.Length > 0 || c != '0')
                {
                    sb.Append(c);
                }
                if (sawDot)
                {
                    exp10--;
                }
            }
            else if (c == '.' && !sawDot)
            {
                sawDot = true;
            }
            else
            {
                break;
            }
        }
        if (!sawDigit)
        {
            return false;
        }
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            bool eneg = false;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                eneg = s[i] == '-';
                i++;
            }
            if (i == n)
            {
                return false;
            }
            long e = 0;
            for (; i < n; i++)
            {
                char c = s[i];
                if (c < '0' || c > '9')
                {
                    return false;
                }
                if (e < 100000000)
                {
                    e = e * 10 + (c - '0');
                }
            }
            exp10 += eneg ? -e : e;
        }
        if (i != n)
        {
            return false;
        }
        digits = sb.ToString();
        return true;
    }

    private static readonly double[] ExactPowersOfTen =
    {
        1e0, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10, 1e11,
        1e12, 1e13, 1e14, 1e15, 1e16, 1e17, 1e18, 1e19, 1e20, 1e21, 1e22,
    };

    // The raw bits of digits * 10^exp10 (digits without leading zeros) rounded half to even into a binary
    // format with mbits fraction bits and the given exponent bias (52/1023 for double, 23/127 for float).
    internal static long DecimalToBinary(string digits, long exp10, int mbits, int bias)
    {
        int end = digits.Length;
        while (end > 0 && digits[end - 1] == '0')
        {
            end--;
        }
        if (end == 0)
        {
            return 0;
        }
        exp10 += digits.Length - end;
        digits = digits.Substring(0, end);
        long magnitude = digits.Length + exp10; // value < 10^magnitude
        long infinity = (long)(2 * bias + 1) << mbits;
        if (magnitude > 310)
        {
            return infinity;
        }
        if (magnitude < -330)
        {
            return 0;
        }
        if (mbits == 52 && digits.Length <= 15 && exp10 >= -22 && exp10 <= 22)
        {
            // Both operands are exact, so the one IEEE operation rounds correctly.
            double m = long.Parse(digits, CultureInfo.InvariantCulture);
            return BitConverter.DoubleToInt64Bits(exp10 >= 0 ? m * ExactPowersOfTen[exp10] : m / ExactPowersOfTen[-exp10]);
        }

        BigInteger num = BigInteger.Parse(digits, CultureInfo.InvariantCulture);
        BigInteger den = BigInteger.One;
        if (exp10 >= 0)
        {
            num *= BigInteger.Pow(10, (int)exp10);
        }
        else
        {
            den = BigInteger.Pow(10, (int)-exp10);
        }
        // e2 = floor(log2(num / den)), estimated and then corrected.
        int e2 = (int)Math.Floor(BigInteger.Log(num, 2) - BigInteger.Log(den, 2));
        while (CompareScaled(num, den, e2) < 0)
        {
            e2--;
        }
        while (CompareScaled(num, den, e2 + 1) >= 0)
        {
            e2++;
        }
        if (e2 < 1 - bias)
        {
            // Subnormal: fixed scale.
            e2 = 1 - bias;
        }
        int shift = mbits - e2;
        BigInteger d = shift >= 0 ? den : den << -shift;
        BigInteger q = BigInteger.DivRem(shift >= 0 ? num << shift : num, d, out BigInteger r);
        int c = (r << 1).CompareTo(d);
        if (c > 0 || (c == 0 && !q.IsEven))
        {
            q += 1;
        }
        if (q == BigInteger.One << (mbits + 1))
        {
            q >>= 1;
            e2++;
        }
        if (e2 > bias)
        {
            return infinity;
        }
        if (q < BigInteger.One << mbits)
        {
            // Subnormal or zero; q == 2^mbits after rounding up already encodes the smallest normal.
            return (long)q;
        }
        return ((long)(e2 + bias) << mbits) | ((long)q & ((1L << mbits) - 1));
    }

    // Compares num / den with 2^k.
    private static int CompareScaled(BigInteger num, BigInteger den, int k) =>
        k >= 0 ? num.CompareTo(den << k) : (num << -k).CompareTo(den);

    // The shortest digits that parse back to x (x > 0, exact in the format given as in DecimalToBinary),
    // the closest to x when two qualify, as strconv's shortest formatting picks: x = 0.digits * 10^point.
    internal static void ShortestDigits(double x, int mbits, int bias, out string digits, out int point)
    {
        Util.ExactDigits(x, out string d, out int dp);
        digits = d;
        point = dp;
        if (x < (double)(1L << (mbits + 1)) && x == Math.Floor(x))
        {
            // Below 2^(mbits+1) the ulp is at most 1, so an integer needs all its digits.
            return;
        }
        long target = DecimalToBinary(d, dp - d.Length, mbits, bias);
        // Round-tripping is monotone in the digit count, so binary search the shortest.
        int lo = 1;
        int hi = d.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (TryRound(d, dp, mid, mbits, bias, target, out _, out _))
            {
                hi = mid;
            }
            else
            {
                lo = mid + 1;
            }
        }
        if (lo < d.Length)
        {
            TryRound(d, dp, lo, mbits, bias, target, out digits, out point);
        }
    }

    // Tries d cut to p digits rounded down and up, nearer first, keeping the first that parses to target.
    private static bool TryRound(string d, int dp, int p, int mbits, int bias, long target, out string digits, out int point)
    {
        string down = d.Substring(0, p);
        char[] upChars = down.ToCharArray();
        int upDp = dp;
        int k = p - 1;
        while (k >= 0 && upChars[k] == '9')
        {
            upChars[k] = '0';
            k--;
        }
        string up;
        if (k < 0)
        {
            up = "1";
            upDp++;
        }
        else
        {
            upChars[k]++;
            up = new string(upChars);
        }
        // d has no trailing zeros, so the cut-off part is above half unless it is exactly "5".
        bool upFirst = d[p] > '5' || (d[p] == '5' && (d.Length > p + 1 || ((d[p - 1] - '0') & 1) == 1));
        string first = upFirst ? up : down;
        int firstDp = upFirst ? upDp : dp;
        string second = upFirst ? down : up;
        int secondDp = upFirst ? dp : upDp;
        if (DecimalToBinary(first, firstDp - first.Length, mbits, bias) == target)
        {
            digits = first.TrimEnd('0');
            point = firstDp;
            return true;
        }
        if (DecimalToBinary(second, secondDp - second.Length, mbits, bias) == target)
        {
            digits = second.TrimEnd('0');
            point = secondDp;
            return true;
        }
        digits = "";
        point = 0;
        return false;
    }
#endif

    private static bool IsHexLetter(char c) => (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    // strconv's special: [+-]inf, [+-]infinity and nan, case-insensitively.
    private static bool Special(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }
        string t = s;
        bool signed = s[0] == '+' || s[0] == '-';
        if (signed)
        {
            t = s.Substring(1);
        }
        if (string.Equals(t, "inf", StringComparison.OrdinalIgnoreCase) || string.Equals(t, "infinity", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        return !signed && string.Equals(t, "nan", StringComparison.OrdinalIgnoreCase);
    }

    // underscoreOK: underscores may only appear between digits or between a base prefix and a digit.
    private static bool UnderscoreOk(string s)
    {
        char saw = '^';
        int i = 0;
        if (s.Length >= 1 && (s[0] == '-' || s[0] == '+'))
        {
            s = s.Substring(1);
        }
        bool hex = false;
        if (s.Length >= 2 && s[0] == '0' && "bBoOxX".IndexOf(s[1]) >= 0)
        {
            i = 2;
            saw = '0';
            hex = s[1] == 'x' || s[1] == 'X';
        }
        for (; i < s.Length; i++)
        {
            if ((s[i] >= '0' && s[i] <= '9') || (hex && IsHexLetter(s[i])))
            {
                saw = '0';
                continue;
            }
            if (s[i] == '_')
            {
                if (saw != '0')
                {
                    return false;
                }
                saw = '_';
                continue;
            }
            if (saw == '_')
            {
                return false;
            }
            saw = '!';
        }
        return saw != '_';
    }
}
