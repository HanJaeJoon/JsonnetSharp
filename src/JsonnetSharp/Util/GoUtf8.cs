// Ports the parts of Go's unicode/utf8, unicode/utf16 and strconv that the frontend needs.

using System.Globalization;
using System.Text;

namespace JsonnetSharp
{
    internal static class GoUtf8
    {
        internal const int RuneError = 0xFFFD;
        internal const int MaxRune = 0x10FFFF;

        internal static readonly UTF8Encoding Utf8 = new(false, false);

        // []byte(s). Go has no size limit; a UTF-8 form over int.MaxValue bytes does not fit a .NET array.
        internal static byte[] GetBytes(string s)
        {
            try
            {
                return Utf8.GetBytes(s);
            }
            catch (ArgumentException) when (s.Length > int.MaxValue / 3)
            {
                throw new StringTooLongException("string is too long for .NET (its UTF-8 form is more than 2147483647 bytes)");
            }
        }

        // Mirrors utf8.DecodeRune on s[i:end]: invalid or truncated input yields (RuneError, 1).
        internal static int DecodeRune(byte[] s, int i, int end, out int width)
        {
            if (i >= end)
            {
                width = 0;
                return RuneError;
            }
            int b0 = s[i];
            if (b0 < 0x80)
            {
                width = 1;
                return b0;
            }
            int need;
            int min;
            int r;
            if (b0 >= 0xC2 && b0 <= 0xDF)
            {
                need = 1;
                min = 0x80;
                r = b0 & 0x1F;
            }
            else if (b0 >= 0xE0 && b0 <= 0xEF)
            {
                need = 2;
                min = 0x800;
                r = b0 & 0x0F;
            }
            else if (b0 >= 0xF0 && b0 <= 0xF4)
            {
                need = 3;
                min = 0x10000;
                r = b0 & 0x07;
            }
            else
            {
                width = 1;
                return RuneError;
            }
            if (i + need >= end)
            {
                width = 1;
                return RuneError;
            }
            for (int k = 1; k <= need; k++)
            {
                int b = s[i + k];
                if ((b & 0xC0) != 0x80)
                {
                    width = 1;
                    return RuneError;
                }
                r = (r << 6) | (b & 0x3F);
            }
            if (r < min || r > MaxRune || (r >= 0xD800 && r <= 0xDFFF))
            {
                width = 1;
                return RuneError;
            }
            width = need + 1;
            return r;
        }

        internal static bool ValidRune(int r) => (r >= 0 && r < 0xD800) || (r > 0xDFFF && r <= MaxRune);

        // Appends r like Go's WriteRune: invalid runes become U+FFFD.
        internal static void AppendRune(StringBuilder sb, int r)
        {
            if (!ValidRune(r))
            {
                r = RuneError;
            }
            if (r < 0x10000)
            {
                sb.Append((char)r);
            }
            else
            {
                r -= 0x10000;
                sb.Append((char)(0xD800 + (r >> 10)));
                sb.Append((char)(0xDC00 + (r & 0x3FF)));
            }
        }

        internal static string RuneToString(int r)
        {
            StringBuilder sb = new(2);
            AppendRune(sb, r);
            return sb.ToString();
        }

        // utf16.DecodeRune
        internal static int DecodeSurrogatePair(int r1, int r2)
        {
            if (r1 >= 0xD800 && r1 < 0xDC00 && r2 >= 0xDC00 && r2 < 0xE000)
            {
                return (((r1 - 0xD800) << 10) | (r2 - 0xDC00)) + 0x10000;
            }
            return RuneError;
        }

        // strconv.QuoteRuneToASCII
        internal static string QuoteRuneToASCII(int r)
        {
            if (!ValidRune(r))
            {
                r = RuneError;
            }
            StringBuilder sb = new();
            sb.Append('\'');
            if (r == '\'' || r == '\\')
            {
                sb.Append('\\').Append((char)r);
            }
            else if (r < 0x80 && r >= 0x20 && r != 0x7F)
            {
                sb.Append((char)r);
            }
            else
            {
                switch (r)
                {
                    case '\a': sb.Append("\\a"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    case '\v': sb.Append("\\v"); break;
                    default:
                        if (r < ' ' || r == 0x7F)
                        {
                            sb.Append("\\x").Append(r.ToString("x2", CultureInfo.InvariantCulture));
                        }
                        else if (r < 0x10000)
                        {
                            sb.Append("\\u").Append(r.ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            sb.Append("\\U").Append(r.ToString("x8", CultureInfo.InvariantCulture));
                        }
                        break;
                }
            }
            sb.Append('\'');
            return sb.ToString();
        }
    }
}
