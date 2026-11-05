// Port of sigs.k8s.io/yaml v1.4.0 goyaml.v2/resolve.go, with the bits of Go's strconv.ParseInt/ParseUint/ParseFloat
// and time.Parse that it relies on.

using System.Globalization;

namespace JsonnetSharp.Yaml;

internal static class YamlResolve
{
    private const string LongTagPrefix = "tag:yaml.org,2002:";

    private static readonly byte[] ResolveTable = BuildResolveTable();

    private static readonly Dictionary<string, (object? Value, string Tag)> ResolveMap = BuildResolveMap();

    private static byte[] BuildResolveTable()
    {
        var t = new byte[256];
        t['+'] = (byte)'S'; // Sign
        t['-'] = (byte)'S';
        foreach (char c in "0123456789")
        {
            t[c] = (byte)'D'; // Digit
        }
        foreach (char c in "yYnNtTfFoO~")
        {
            t[c] = (byte)'M'; // In map
        }
        t['.'] = (byte)'.'; // Float (potentially in map)
        return t;
    }

    private static Dictionary<string, (object?, string)> BuildResolveMap()
    {
        var m = new Dictionary<string, (object?, string)>(StringComparer.Ordinal);
        void Add(object? v, string tag, params string[] l)
        {
            foreach (string s in l)
            {
                m[s] = (v, tag);
            }
        }
        Add(true, YamlTags.Bool, "y", "Y", "yes", "Yes", "YES");
        Add(true, YamlTags.Bool, "true", "True", "TRUE");
        Add(true, YamlTags.Bool, "on", "On", "ON");
        Add(false, YamlTags.Bool, "n", "N", "no", "No", "NO");
        Add(false, YamlTags.Bool, "false", "False", "FALSE");
        Add(false, YamlTags.Bool, "off", "Off", "OFF");
        Add(null, YamlTags.Null, "", "~", "null", "Null", "NULL");
        Add(double.NaN, YamlTags.Float, ".nan", ".NaN", ".NAN");
        Add(double.PositiveInfinity, YamlTags.Float, ".inf", ".Inf", ".INF");
        Add(double.PositiveInfinity, YamlTags.Float, "+.inf", "+.Inf", "+.INF");
        Add(double.NegativeInfinity, YamlTags.Float, "-.inf", "-.Inf", "-.INF");
        Add("<<", YamlTags.Merge, "<<");
        return m;
    }

    public static string ShortTag(string tag) =>
        tag.StartsWith(LongTagPrefix, StringComparison.Ordinal) ? "!!" + tag.Substring(LongTagPrefix.Length) : tag;

    private static bool ResolvableTag(string tag) =>
        tag.Length == 0 || tag == YamlTags.Str || tag == YamlTags.Bool || tag == YamlTags.Int ||
        tag == YamlTags.Float || tag == YamlTags.Null || tag == YamlTags.Timestamp;

    public static (string Tag, object? Value) Resolve(string tag, string input)
    {
        if (!ResolvableTag(tag))
        {
            return (tag, input);
        }

        (string rtag, object? output) = ResolveUnchecked(tag, input);

        if (tag.Length == 0 || tag == rtag || tag == YamlTags.Str || tag == YamlTags.Binary)
        {
            return (rtag, output);
        }
        if (tag == YamlTags.Float && rtag == YamlTags.Int && output is long l)
        {
            return (YamlTags.Float, (double)l);
        }
        throw YamlDecoder.Failf("cannot decode " + ShortTag(rtag) + " `" + input + "` as a " + ShortTag(tag));
    }

    private static (string, object?) ResolveUnchecked(string tag, string input)
    {
        // Any data is accepted as a !!str or !!binary.
        // Otherwise, the prefix is enough of a hint about what it might be.
        byte hint = (byte)'N';
        if (input.Length != 0)
        {
            hint = input[0] < 0x80 ? ResolveTable[input[0]] : (byte)0;
        }
        if (hint != 0 && tag != YamlTags.Str && tag != YamlTags.Binary)
        {
            // Handle things we can lookup in a map.
            if (ResolveMap.TryGetValue(input, out (object? Value, string Tag) item))
            {
                return (item.Tag, item.Value);
            }

            // Base 60 floats are a bad idea, were dropped in YAML 1.2, and
            // are purposefully unsupported here. They're still quoted on
            // the way out for compatibility with other parser, though.

            switch (hint)
            {
                case (byte)'M':
                    // We've already checked the map above.
                    break;

                case (byte)'.':
                    // Not in the map, so maybe a normal float.
                    if (ParseFloat(input, out double floatv))
                    {
                        return (YamlTags.Float, floatv);
                    }
                    break;

                case (byte)'D':
                case (byte)'S':
                    // Int, float, or timestamp.
                    // Only try values as a timestamp if the value is unquoted or there's an explicit
                    // !!timestamp tag.
                    if (tag.Length == 0 || tag == YamlTags.Timestamp)
                    {
                        if (ParseTimestamp(input))
                        {
                            return (YamlTags.Timestamp, input);
                        }
                    }

                    string plain = input.Replace("_", "");
                    if (ParseInt(plain, 0, out long intv))
                    {
                        return (YamlTags.Int, intv);
                    }
                    if (ParseUint(plain, 0, out ulong uintv))
                    {
                        return (YamlTags.Int, uintv);
                    }
                    if (IsYamlStyleFloat(plain) && ParseFloat(plain, out floatv))
                    {
                        return (YamlTags.Float, floatv);
                    }
                    if (plain.StartsWith("0b", StringComparison.Ordinal))
                    {
                        if (ParseInt(plain.Substring(2), 2, out intv))
                        {
                            return (YamlTags.Int, intv);
                        }
                        if (ParseUint(plain.Substring(2), 2, out uintv))
                        {
                            return (YamlTags.Int, uintv);
                        }
                    }
                    else if (plain.StartsWith("-0b", StringComparison.Ordinal))
                    {
                        if (ParseInt("-" + plain.Substring(3), 2, out intv))
                        {
                            return (YamlTags.Int, intv);
                        }
                    }
                    break;

                default:
                    throw new InvalidOperationException("resolveTable item not yet handled: " + (char)hint + " (with " + input + ")");
            }
        }
        return (YamlTags.Str, input);
    }

    // ^[-+]?(\.[0-9]+|[0-9]+(\.[0-9]*)?)([eE][-+]?[0-9]+)?$
    private static bool IsYamlStyleFloat(string s)
    {
        int i = 0;
        int n = s.Length;
        if (i < n && (s[i] == '-' || s[i] == '+'))
        {
            i++;
        }
        if (i < n && s[i] == '.')
        {
            i++;
            int start = i;
            while (i < n && IsDigit(s[i]))
            {
                i++;
            }
            if (i == start)
            {
                return false;
            }
        }
        else
        {
            int start = i;
            while (i < n && IsDigit(s[i]))
            {
                i++;
            }
            if (i == start)
            {
                return false;
            }
            if (i < n && s[i] == '.')
            {
                i++;
                while (i < n && IsDigit(s[i]))
                {
                    i++;
                }
            }
        }
        if (i < n && (s[i] == 'e' || s[i] == 'E'))
        {
            i++;
            if (i < n && (s[i] == '-' || s[i] == '+'))
            {
                i++;
            }
            int start = i;
            while (i < n && IsDigit(s[i]))
            {
                i++;
            }
            if (i == start)
            {
                return false;
            }
        }
        return i == n;
    }

    private static bool IsDigit(char c) => c >= '0' && c <= '9';

    private static char Lower(char c) => (char)(c | 0x20);

    // strconv.ParseInt(s, base, 64), reporting only success.
    internal static bool ParseInt(string s, int numBase, out long value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }
        bool neg = false;
        if (s[0] == '+')
        {
            s = s.Substring(1);
        }
        else if (s[0] == '-')
        {
            neg = true;
            s = s.Substring(1);
        }
        if (!ParseUint(s, numBase, out ulong un))
        {
            return false;
        }
        const ulong cutoff = 1UL << 63;
        if (!neg && un >= cutoff || neg && un > cutoff)
        {
            return false;
        }
        value = neg ? (long)(0 - un) : (long)un;
        return true;
    }

    // strconv.ParseUint(s, base, 64), reporting only success. Underscores were already removed by the caller.
    internal static bool ParseUint(string s, int numBase, out ulong value)
    {
        value = 0;
        if (s.Length == 0)
        {
            return false;
        }
        if (numBase == 0)
        {
            numBase = 10;
            if (s[0] == '0')
            {
                if (s.Length >= 3 && Lower(s[1]) == 'b')
                {
                    numBase = 2;
                    s = s.Substring(2);
                }
                else if (s.Length >= 3 && Lower(s[1]) == 'o')
                {
                    numBase = 8;
                    s = s.Substring(2);
                }
                else if (s.Length >= 3 && Lower(s[1]) == 'x')
                {
                    numBase = 16;
                    s = s.Substring(2);
                }
                else
                {
                    numBase = 8;
                    s = s.Substring(1);
                }
            }
        }

        ulong n = 0;
        foreach (char c in s)
        {
            int d;
            if (c >= '0' && c <= '9')
            {
                d = c - '0';
            }
            else if (Lower(c) >= 'a' && Lower(c) <= 'z')
            {
                d = Lower(c) - 'a' + 10;
            }
            else
            {
                return false;
            }
            if (d >= numBase)
            {
                return false;
            }
            ulong n1;
            try
            {
                n1 = checked(n * (ulong)numBase + (ulong)d);
            }
            catch (OverflowException)
            {
                return false;
            }
            n = n1;
        }
        value = n;
        return true;
    }

    // strconv.ParseFloat(s, 64) for decimal input, reporting only success (syntax and range errors both fail).
    // Hexadecimal, inf and nan forms cannot reach it: resolve only calls it for input starting with '.' or matching
    // yamlStyleFloat.
    internal static bool ParseFloat(string s, out double value)
    {
        value = 0;
        int i = 0;
        int n = s.Length;
        bool underscores = false;
        if (i < n && (s[i] == '+' || s[i] == '-'))
        {
            i++;
        }
        bool sawDot = false;
        bool sawDigits = false;
        for (; i < n; i++)
        {
            char c = s[i];
            if (c == '_')
            {
                underscores = true;
            }
            else if (c == '.')
            {
                if (sawDot)
                {
                    break;
                }
                sawDot = true;
            }
            else if (IsDigit(c))
            {
                sawDigits = true;
            }
            else
            {
                break;
            }
        }
        if (!sawDigits)
        {
            return false;
        }
        if (i < n && Lower(s[i]) == 'e')
        {
            i++;
            if (i < n && (s[i] == '+' || s[i] == '-'))
            {
                i++;
            }
            if (i >= n || !IsDigit(s[i]))
            {
                return false;
            }
            for (; i < n && (IsDigit(s[i]) || s[i] == '_'); i++)
            {
                if (s[i] == '_')
                {
                    underscores = true;
                }
            }
        }
        if (i != n || underscores && !UnderscoreOk(s))
        {
            return false;
        }
        if (!GoStrconv.TryParseFloat(s.Replace("_", ""), out value) ||
            double.IsInfinity(value))
        {
            // Overflow is a range error in Go.
            return false;
        }
        if (value == 0 && s[0] == '-')
        {
            // .NET Framework drops the sign of a negative zero.
            value = -0.0;
        }
        return true;
    }

    // strconv.underscoreOK for a decimal number.
    private static bool UnderscoreOk(string s)
    {
        // saw tracks the last character (class) we saw:
        // ^ for beginning of number,
        // 0 for a digit or base prefix,
        // _ for an underscore,
        // ! for none of the above.
        char saw = '^';
        int i = 0;
        if (s.Length >= 1 && (s[0] == '-' || s[0] == '+'))
        {
            s = s.Substring(1);
        }
        for (; i < s.Length; i++)
        {
            if (IsDigit(s[i]))
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

    private enum TimestampLayout
    {
        UpperT,
        LowerT,
        Space,
        DateOnly,
    }

    // parseTimestamp: reports whether s parses as one of the allowed timestamp layouts.
    // Only the validity matters: values resolved as timestamps are kept as their original text.
    internal static bool ParseTimestamp(string s)
    {
        // Quick check: all date formats start with YYYY-.
        int i = 0;
        for (; i < s.Length; i++)
        {
            if (!IsDigit(s[i]))
            {
                break;
            }
        }
        if (i != 4 || i == s.Length || s[i] != '-')
        {
            return false;
        }
        return TimeParse(s, TimestampLayout.UpperT) || TimeParse(s, TimestampLayout.LowerT) ||
            TimeParse(s, TimestampLayout.Space) || TimeParse(s, TimestampLayout.DateOnly);
    }

    // time.Parse with the layouts "2006-1-2T15:4:5.999999999Z07:00", "2006-1-2t15:4:5.999999999Z07:00",
    // "2006-1-2 15:4:5.999999999" and "2006-1-2", reporting only success.
    private static bool TimeParse(string s, TimestampLayout layout)
    {
        int p = 0;
        if (s.Length < 4 || !IsDigit(s[0]))
        {
            return false;
        }
        int year = int.Parse(s.Substring(0, 4), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        p = 4;
        if (!Literal(s, ref p, '-') || !GetNum(s, ref p, false, out int month) || month <= 0 || month > 12 ||
            !Literal(s, ref p, '-') || !GetNum(s, ref p, false, out int day) || day > 31)
        {
            return false;
        }
        if (layout != TimestampLayout.DateOnly)
        {
            if (layout == TimestampLayout.Space)
            {
                // A space in the layout matches one or more spaces in the value.
                if (p >= s.Length || s[p] != ' ')
                {
                    return false;
                }
                while (p < s.Length && s[p] == ' ')
                {
                    p++;
                }
            }
            else if (!Literal(s, ref p, layout == TimestampLayout.UpperT ? 'T' : 't'))
            {
                return false;
            }
            if (!GetNum(s, ref p, false, out int hour) || hour >= 24 ||
                !Literal(s, ref p, ':') || !GetNum(s, ref p, false, out int minute) || minute >= 60 ||
                !Literal(s, ref p, ':') || !GetNum(s, ref p, false, out int second) || second >= 60)
            {
                return false;
            }

            // Fractional seconds: any number of digits after '.' or ','.
            if (s.Length - p >= 2 && (s[p] == '.' || s[p] == ',') && IsDigit(s[p + 1]))
            {
                p++;
                while (p < s.Length && IsDigit(s[p]))
                {
                    p++;
                }
            }

            if (layout != TimestampLayout.Space)
            {
                if (p < s.Length && s[p] == 'Z')
                {
                    p++;
                }
                else
                {
                    if (s.Length - p < 6 || s[p + 3] != ':')
                    {
                        return false;
                    }
                    int q = p + 1;
                    int r = p + 4;
                    if (!GetNum(s, ref q, true, out int hr) || !GetNum(s, ref r, true, out int mm) || hr > 24 || mm > 60 ||
                        (s[p] != '+' && s[p] != '-'))
                    {
                        return false;
                    }
                    p += 6;
                }
            }
        }
        return p == s.Length && day >= 1 && day <= DaysIn(month, year);
    }

    private static bool Literal(string s, ref int p, char c)
    {
        if (p < s.Length && s[p] == c)
        {
            p++;
            return true;
        }
        return false;
    }

    // time.getnum: one or two digits, exactly two when fixed.
    private static bool GetNum(string s, ref int p, bool fixedWidth, out int value)
    {
        value = 0;
        if (p >= s.Length || !IsDigit(s[p]))
        {
            return false;
        }
        if (p + 1 >= s.Length || !IsDigit(s[p + 1]))
        {
            if (fixedWidth)
            {
                return false;
            }
            value = s[p] - '0';
            p++;
            return true;
        }
        value = (s[p] - '0') * 10 + (s[p + 1] - '0');
        p += 2;
        return true;
    }

    private static int DaysIn(int month, int year)
    {
        if (month == 2)
        {
            bool leap = year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
            return leap ? 29 : 28;
        }
        return month == 4 || month == 6 || month == 9 || month == 11 ? 30 : 31;
    }
}
