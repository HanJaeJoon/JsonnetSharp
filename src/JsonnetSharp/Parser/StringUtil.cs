// Ports go-jsonnet internal/parser/string_util.go.

using System.Globalization;
using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp
{
    internal sealed partial class Parser
    {
        // StringUnescape compiles out the escape codes in the string.
        // Works on UTF-8 bytes like Go because length checks and error text are byte based.
        internal static string StringUnescape(in LocationRange loc, string str)
        {
            if (str.IndexOf('\\') < 0)
            {
                return str;
            }
            byte[] s = GoUtf8.Utf8.GetBytes(str);
            StringBuilder buf = new(str.Length);
            // read one rune at a time
            for (int i = 0; i < s.Length;)
            {
                int r = GoUtf8.DecodeRune(s, i, s.Length, out int w);
                i += w;
                if (r != '\\')
                {
                    GoUtf8.AppendRune(buf, r);
                    continue;
                }
                if (i >= s.Length)
                {
                    throw new StaticError("Truncated escape sequence in string literal.", loc);
                }
                int r2 = GoUtf8.DecodeRune(s, i, s.Length, out int w2);
                i += w2;
                switch (r2)
                {
                    case '"':
                        buf.Append('"');
                        break;
                    case '\'':
                        buf.Append('\'');
                        break;
                    case '\\':
                        buf.Append('\\');
                        break;
                    case '/':
                        buf.Append('/'); // See json.org, \/ is a valid escape.
                        break;
                    case 'b':
                        buf.Append('\b');
                        break;
                    case 'f':
                        buf.Append('\f');
                        break;
                    case 'n':
                        buf.Append('\n');
                        break;
                    case 'r':
                        buf.Append('\r');
                        break;
                    case 't':
                        buf.Append('\t');
                        break;
                    case 'u':
                        {
                            if (i + 4 > s.Length)
                            {
                                throw new StaticError("Truncated unicode escape sequence in string literal.", loc);
                            }
                            if (!TryDecodeHex4(s, i, out int code))
                            {
                                throw new StaticError("Unicode escape sequence was malformed: " + BytesToString(s, 0, 4), loc);
                            }
                            i += 4;
                            if (code >= 0xD800 && code < 0xE000)
                            {
                                int highSurrogate = code;
                                if (i + 6 > s.Length)
                                {
                                    throw new StaticError("Truncated unicode surrogate pair escape sequence in string literal.", loc);
                                }
                                if (s[i] != '\\' || s[i + 1] != 'u')
                                {
                                    throw new StaticError("Unicode surrogate pair escape sequence missing low surrogate in string literal.", loc);
                                }
                                i += 2;
                                if (!TryDecodeHex4(s, i, out int lowSurrogate))
                                {
                                    throw new StaticError("Unicode low surrogate escape sequence was malformed: " + BytesToString(s, 0, 4), loc);
                                }
                                i += 4;
                                code = GoUtf8.DecodeSurrogatePair(highSurrogate, lowSurrogate);
                            }
                            GoUtf8.AppendRune(buf, code);
                            break;
                        }
                    default:
                        throw new StaticError("Unknown escape sequence in string literal: \\" + GoUtf8.RuneToString(r2), loc);
                }
            }
            return buf.ToString();
        }

        private static string BytesToString(byte[] s, int from, int count) => GoUtf8.Utf8.GetString(s, from, count);

        // Mirrors hex.DecodeString on exactly four bytes.
        private static bool TryDecodeHex4(byte[] s, int i, out int code)
        {
            code = 0;
            for (int k = 0; k < 4; k++)
            {
                int c = s[i + k];
                int v;
                if (c >= '0' && c <= '9')
                {
                    v = c - '0';
                }
                else if (c >= 'a' && c <= 'f')
                {
                    v = c - 'a' + 10;
                }
                else if (c >= 'A' && c <= 'F')
                {
                    v = c - 'A' + 10;
                }
                else
                {
                    return false;
                }
                code = (code << 4) | v;
            }
            return true;
        }

        // StringEscape does the opposite of StringUnescape
        internal static string StringEscape(string s, bool single)
        {
            StringBuilder buf = new(s.Length + 2);
            // read one rune at a time
            for (int i = 0; i < s.Length; i++)
            {
                int r = s[i];
                if (char.IsHighSurrogate(s, i) && i + 1 < s.Length && char.IsLowSurrogate(s, i + 1))
                {
                    r = char.ConvertToUtf32(s[i], s[i + 1]);
                    i++;
                }
                else if (r >= 0xD800 && r < 0xE000)
                {
                    r = GoUtf8.RuneError;
                }
                switch (r)
                {
                    case '"':
                        if (!single)
                        {
                            buf.Append('\\');
                        }
                        buf.Append('"');
                        break;
                    case '\'':
                        if (single)
                        {
                            buf.Append('\\');
                        }
                        buf.Append('\'');
                        break;
                    case '\\':
                        buf.Append("\\\\");
                        break;
                    case '\b':
                        buf.Append("\\b");
                        break;
                    case '\f':
                        buf.Append("\\f");
                        break;
                    case '\n':
                        buf.Append("\\n");
                        break;
                    case '\r':
                        buf.Append("\\r");
                        break;
                    case '\t':
                        buf.Append("\\t");
                        break;
                    case '\u0000':
                        buf.Append("\\u0000");
                        break;
                    default:
                        if (r < 0x20 || (r >= 0x7f && r <= 0x9f))
                        {
                            buf.Append("\\u").Append(r.ToString("x4", CultureInfo.InvariantCulture));
                        }
                        else
                        {
                            GoUtf8.AppendRune(buf, r);
                        }
                        break;
                }
            }
            return buf.ToString();
        }
    }
}
