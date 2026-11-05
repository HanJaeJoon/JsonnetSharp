// Ports go-jsonnet util.go, plus small Go-compat helpers used by the runtime.

using System.Numerics;
using System.Text;

namespace JsonnetSharp;

internal static class Util
{
    internal static int MinInt(int a, int b) => a < b ? a : b;

    internal static int RuneCmp(int a, int b) => a < b ? -1 : a > b ? 1 : 0;

    internal static int IntCmp(int a, int b) => a < b ? -1 : a > b ? 1 : 0;

    internal static int Float64Cmp(double a, double b) => a < b ? -1 : a > b ? 1 : 0;

    // Go compares strings bytewise (UTF-8), which is code point order. UTF-16 ordinal order
    // differs when a surrogate pair is compared with a char in U+E000..U+FFFF.
    internal static int CompareGoStrings(string a, string b)
    {
        int n = a.Length < b.Length ? a.Length : b.Length;
        for (int i = 0; i < n; i++)
        {
            char ca = a[i];
            char cb = b[i];
            if (ca != cb)
            {
                return FixUp(ca) < FixUp(cb) ? -1 : 1;
            }
        }
        return IntCmp(a.Length, b.Length);
    }

    private static int FixUp(char c)
    {
        if (c < 0xD800)
        {
            return c;
        }
        return c <= 0xDFFF ? c + 0x2000 : c - 0x800;
    }

    internal static readonly IComparer<string> GoStringComparer = Comparer<string>.Create(CompareGoStrings);

    // .NET's string length limit as a Jsonnet runtime error (Go has none) instead of an OutOfMemoryException
    // from ToString or an ArgumentOutOfRangeException from a StringBuilder past int.MaxValue chars.
    // shortcut: covers manifestation, escaping, std.toString and the output; other builders (TOML, format)
    // still fail with .NET's exception, add the check there if one shows up.
    internal static void CheckLength(StringBuilder sb)
    {
        if (sb.Length > StringTooLongException.MaxLength)
        {
            throw new StringTooLongException();
        }
    }

    internal static string ToStringChecked(StringBuilder sb)
    {
        CheckLength(sb);
        return sb.ToString();
    }

    // A Go string decodes to runes, so a lone surrogate from the host (the analogue of an invalid UTF-8 byte)
    // becomes U+FFFD on every path, the returned text included.
    internal static string WellFormed(string s)
    {
        for (int k = 0; k < s.Length; k++)
        {
            if (char.IsSurrogate(s[k]))
            {
                if (char.IsHighSurrogate(s[k]) && k + 1 < s.Length && char.IsLowSurrogate(s[k + 1]))
                {
                    k++;
                    continue;
                }
                char[] c = s.ToCharArray();
                for (int j = k; j < c.Length; j++)
                {
                    if (char.IsHighSurrogate(c[j]) && j + 1 < c.Length && char.IsLowSurrogate(c[j + 1]))
                    {
                        j++;
                    }
                    else if (char.IsSurrogate(c[j]))
                    {
                        c[j] = (char)0xFFFD;
                    }
                }
                return new string(c);
            }
        }
        return s;
    }

    // Go's int(f) / int64(f) on amd64: out-of-range values and NaN become math.MinInt64.
    internal static long GoFloatToInt64(double f)
    {
        if (double.IsNaN(f) || f >= 9223372036854775808.0 || f < -9223372036854775808.0)
        {
            return long.MinValue;
        }
        return (long)f;
    }

    // fmt.Sprintf("%.0f", v) for an integral, finite v.
    internal static string FormatIntegral(double v)
    {
        bool neg = v < 0 || (v == 0 && BitConverter.DoubleToInt64Bits(v) < 0);
        double abs = Math.Abs(v);
        string digits;
        if (abs < 9007199254740992.0)
        {
            digits = ((long)abs).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        else
        {
            ExactDigits(abs, out digits, out int dp);
            digits = digits.PadRight(dp, '0');
        }
        return neg ? "-" + digits : digits;
    }

    // fmt.Sprintf("%.17g", v) for a finite v.
    internal static string FormatG17(double v)
    {
        const int prec = 17;
        bool neg = v < 0 || (v == 0 && BitConverter.DoubleToInt64Bits(v) < 0);
        double abs = Math.Abs(v);
        string d;
        int dp;
        if (abs == 0)
        {
            d = "";
            dp = 0;
        }
        else
        {
            ExactDigits(abs, out d, out dp);
            RoundDigits(ref d, ref dp, prec);
        }
        int nd = d.Length;

        StringBuilder sb = new();
        if (neg)
        {
            sb.Append('-');
        }
        int eprec = prec;
        if (eprec > nd && nd >= dp)
        {
            eprec = nd;
        }
        int exp = dp - 1;
        if (exp < -4 || exp >= eprec)
        {
            // %e
            int p = prec > nd ? nd : prec;
            p--;
            sb.Append(nd == 0 ? '0' : d[0]);
            if (p > 0)
            {
                sb.Append('.');
                for (int i = 1; i <= p; i++)
                {
                    sb.Append(i < nd ? d[i] : '0');
                }
            }
            sb.Append('e');
            if (exp < 0)
            {
                sb.Append('-');
                exp = -exp;
            }
            else
            {
                sb.Append('+');
            }
            if (exp < 10)
            {
                sb.Append('0');
            }
            sb.Append(exp.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        // %f
        int fprec = prec;
        if (fprec > dp)
        {
            fprec = nd;
        }
        fprec = Math.Max(fprec - dp, 0);
        if (dp > 0)
        {
            int m = Math.Min(nd, dp);
            sb.Append(d, 0, m);
            for (; m < dp; m++)
            {
                sb.Append('0');
            }
        }
        else
        {
            sb.Append('0');
        }
        if (fprec > 0)
        {
            sb.Append('.');
            for (int i = 0; i < fprec; i++)
            {
                int j = dp + i;
                sb.Append(j >= 0 && j < nd ? d[j] : '0');
            }
        }
        return sb.ToString();
    }

    // Exact decimal expansion of a positive finite double: value = 0.digits * 10^dp,
    // digits without trailing zeros.
    internal static void ExactDigits(double v, out string digits, out int dp)
    {
        long bits = BitConverter.DoubleToInt64Bits(v);
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
        while ((mant & 1) == 0)
        {
            mant >>= 1;
            exp++;
        }
        BigInteger n;
        int scale;
        if (exp >= 0)
        {
            n = new BigInteger(mant) << exp;
            scale = 0;
        }
        else
        {
            n = new BigInteger(mant) * BigInteger.Pow(5, -exp);
            scale = -exp;
        }
        string s = n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        dp = s.Length - scale;
        digits = s.TrimEnd('0');
    }

    // Rounds to nd digits, half to even, as strconv does for a fixed precision.
    private static void RoundDigits(ref string d, ref int dp, int nd)
    {
        if (d.Length <= nd)
        {
            return;
        }
        bool up;
        if (d[nd] > '5')
        {
            up = true;
        }
        else if (d[nd] < '5')
        {
            up = false;
        }
        else
        {
            up = d.Length > nd + 1 || ((d[nd - 1] - '0') & 1) == 1;
        }
        char[] c = d.Substring(0, nd).ToCharArray();
        if (up)
        {
            int i = nd - 1;
            while (i >= 0 && c[i] == '9')
            {
                c[i] = '0';
                i--;
            }
            if (i < 0)
            {
                c = new char[] { '1' };
                dp++;
            }
            else
            {
                c[i]++;
            }
        }
        d = new string(c).TrimEnd('0');
    }
}
