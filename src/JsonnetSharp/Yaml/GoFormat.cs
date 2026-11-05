// Go formatting helpers the YAML error messages and key conversions need: strconv.FormatFloat(f, 'g', -1, 32|64),
// strconv.Quote and fmt's %#v for the values goyaml decodes into an interface{}.

using System.Globalization;
using System.Text;

namespace JsonnetSharp.Yaml;

internal static class GoFormat
{
    private const string HexDigits = "0123456789abcdef";

    // strconv.FormatFloat(f, 'g', -1, bitSize) for bitSize 32 or 64.
    public static string FormatFloatG(double f, int bitSize)
    {
        if (bitSize == 32)
        {
            // Narrow first: a finite double beyond float32's range becomes +/-Inf, as in Go.
            f = (float)f;
        }
        if (GoStrconv.NonFinite(f) is string special)
        {
            return special;
        }
        var sb = new StringBuilder();
        if (f < 0 || (f == 0 && 1 / f < 0))
        {
            sb.Append('-');
        }
        double x = Math.Abs(f);
        if (x == 0)
        {
            return sb.Append('0').ToString();
        }
        ShortestDigits(x, bitSize, out string digits, out int point);
        int exp = point - 1;
        if (exp < -4 || exp >= 6)
        {
            // %e with the shortest digits and at least two exponent digits.
            sb.Append(digits[0]);
            if (digits.Length > 1)
            {
                sb.Append('.').Append(digits, 1, digits.Length - 1);
            }
            sb.Append('e').Append(exp < 0 ? '-' : '+');
            int e = Math.Abs(exp);
            if (e < 10)
            {
                sb.Append('0');
            }
            return sb.Append(e.ToString(CultureInfo.InvariantCulture)).ToString();
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

    // The shortest decimal digits that round trip to x (x > 0) at the given precision; x = 0.digits * 10^point.
    private static void ShortestDigits(double x, int bitSize, out string digits, out int point)
    {
#if !NET
        // .NET Framework has no shortest "R" and its "E" formatting and parsing are not correctly rounded.
        GoStrconv.ShortestDigits(x, bitSize == 32 ? 23 : 52, bitSize == 32 ? 127 : 1023, out digits, out point);
#else
        // "R" is the shortest round trip string since .NET Core 3.0.
        string s = bitSize == 32 ? ((float)x).ToString("R", CultureInfo.InvariantCulture) : x.ToString("R", CultureInfo.InvariantCulture);
        int e = s.IndexOfAny(new[] { 'E', 'e' });
        string mantissa = e < 0 ? s : s.Substring(0, e);
        int exponent = e < 0 ? 0 : int.Parse(s.Substring(e + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
        int dot = mantissa.IndexOf('.');
        point = (dot < 0 ? mantissa.Length : dot) + exponent;
        string all = dot < 0 ? mantissa : mantissa.Remove(dot, 1);
        int lead = 0;
        while (lead < all.Length - 1 && all[lead] == '0')
        {
            lead++;
        }
        point -= lead;
        digits = all.Substring(lead).TrimEnd('0');
        if (digits.Length == 0)
        {
            digits = "0";
        }
#endif
    }

    // fmt's %#v (and %+#v) of a decoded YAML value passed directly as an argument.
    public static string GoSyntax(object? v) => v == null ? "<nil>" : GoSyntaxInner(v);

    private static string GoSyntaxInner(object? v)
    {
        switch (v)
        {
            case null:
                return "interface {}(nil)";
            case bool b:
                return b ? "true" : "false";
            case long l:
                return l.ToString(CultureInfo.InvariantCulture);
            case ulong u:
                return "0x" + u.ToString("x", CultureInfo.InvariantCulture);
            case double d:
                return FormatFloatG(d, 64);
            case string s:
                return Quote(s, rawBytes: true);
            case List<object?> list:
                return "[]interface {}{" + string.Join(", ", list.Select(GoSyntaxInner)) + "}";
            case YamlMap m:
            {
                // Pairs, not lookups: a NaN key never finds itself. The stable sort keeps NaN keys in document order.
                IEnumerable<KeyValuePair<object, object?>> entries = m.Entries.OrderBy(kv => kv.Key, Comparer<object>.Create(CompareKeys));
                return "map[interface {}]interface {}{" +
                    string.Join(", ", entries.Select(kv => GoSyntaxInner(kv.Key == YamlMap.NullKey ? null : kv.Key) + ":" + GoSyntaxInner(kv.Value))) +
                    "}";
            }
            default:
                return v.ToString() ?? "";
        }
    }

    // internal/fmtsort order for interface keys: nil first, then by dynamic type, then by value.
    // shortcut: Go orders different dynamic types by type descriptor address, which depends on the build; TypeRank
    // pins the order the go-jsonnet 0.22.0 release binary (windows/amd64) prints. Recheck it on a go-jsonnet upgrade.
    private static int CompareKeys(object a, object b)
    {
        int ra = TypeRank(a);
        int rb = TypeRank(b);
        if (ra != rb)
        {
            return ra.CompareTo(rb);
        }
        switch (a)
        {
            case bool ba:
                return ba.CompareTo((bool)b);
            case long la:
                return la.CompareTo((long)b);
            case ulong ua:
                return ua.CompareTo((ulong)b);
            case double da:
            {
                double db = (double)b;
                if (double.IsNaN(da) || double.IsNaN(db))
                {
                    return double.IsNaN(da) ? (double.IsNaN(db) ? 0 : -1) : 1;
                }
                return da.CompareTo(db);
            }
            case string sa:
                return GoString.Compare(sa, (string)b);
            default:
                return 0;
        }
    }

    private static int TypeRank(object k) => k switch
    {
        _ when k == YamlMap.NullKey => 0,
        string => 1,
        ulong => 2,
        long => 3,
        double => 4,
        _ => 5, // bool
    };

    // strconv.Quote. With rawBytes, escaped invalid bytes (see GoString) print as \xNN like Go's.
    public static string Quote(string s, bool rawBytes = false)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        for (int i = 0; i < s.Length; i++)
        {
            int r = s[i];
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]))
            {
                r = char.ConvertToUtf32(s[i], s[i + 1]);
            }
            int width = r >= 0x10000 ? 2 : 1;
            int raw = rawBytes ? GoString.EscapedByte(s, i) : -1;
            if (raw >= 0)
            {
                sb.Append("\\x").Append(HexDigits[raw >> 4]).Append(HexDigits[raw & 0xF]);
            }
            else if (r == '"' || r == '\\')
            {
                sb.Append('\\').Append((char)r);
            }
            else if (IsPrint(r))
            {
                sb.Append(s, i, width);
            }
            else
            {
                switch (r)
                {
                    case '\a':
                        sb.Append("\\a");
                        break;
                    case '\b':
                        sb.Append("\\b");
                        break;
                    case '\f':
                        sb.Append("\\f");
                        break;
                    case '\n':
                        sb.Append("\\n");
                        break;
                    case '\r':
                        sb.Append("\\r");
                        break;
                    case '\t':
                        sb.Append("\\t");
                        break;
                    case '\v':
                        sb.Append("\\v");
                        break;
                    default:
                        if (r < ' ' || r == 0x7F)
                        {
                            sb.Append("\\x").Append(HexDigits[r >> 4]).Append(HexDigits[r & 0xF]);
                        }
                        else if (r < 0x10000)
                        {
                            if (r >= 0xD800 && r <= 0xDFFF)
                            {
                                r = 0xFFFD;
                            }
                            sb.Append("\\u");
                            for (int shift = 12; shift >= 0; shift -= 4)
                            {
                                sb.Append(HexDigits[(r >> shift) & 0xF]);
                            }
                        }
                        else
                        {
                            sb.Append("\\U");
                            for (int shift = 28; shift >= 0; shift -= 4)
                            {
                                sb.Append(HexDigits[(r >> shift) & 0xF]);
                            }
                        }
                        break;
                }
            }
            i += width - 1;
        }
        return sb.Append('"').ToString();
    }

    // unicode.IsPrint on fixed Unicode 15.0 tables, like Go's strconv.
    private static bool IsPrint(int r) => (r < 0xD800 || r > 0xDFFF) && UnicodeTables.IsPrint(r);
}
