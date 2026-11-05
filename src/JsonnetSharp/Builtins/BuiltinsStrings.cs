// Ports the string, encoding and hashing builtins of go-jsonnet builtins.go: escapeStringJson,
// lstripChars, rstripChars, stripChars, substr, splitLimit, splitLimitR, strReplace, isEmpty,
// equalsIgnoreCase, trim, char, codepoint, encodeUTF8, decodeUTF8, base64, base64Decode,
// base64DecodeBytes, parseInt, md5, sha1, sha256, sha512 and sha3.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace JsonnetSharp;

internal static partial class Builtins
{
    private static readonly UTF8Encoding Utf8 = new(false, false);

    // []byte(s) of the Go string; lone surrogates become U+FFFD like invalid runes in Go.
    private static byte[] GoBytes(ValueString s) => GoUtf8.GetBytes(s.GetGoString());

    internal static Value BuiltinEscapeStringJson(Interpreter i, Value v) =>
        new ValueFlatString(Interpreter.UnparseString(ValueToString(i, v)));

    // Go strips recursively one character at a time; a loop gives the same result without deep recursion.
    internal static Value BuiltinLstripChars(Interpreter i, Value str, Value chars)
    {
        if (str is not ValueString s)
        {
            throw i.Error("Unexpected type " + str.Type.Name + ", expected string");
        }
        int[] runes = s.GetRunes();
        int start = 0;
        while (start < runes.Length && RawMember(i, chars, new ValueFlatString(new[] { runes[start] })))
        {
            start++;
        }
        return start == 0 ? str : new ValueFlatString(ValueFlatString.RunesToString(runes, start, runes.Length - start));
    }

    internal static Value BuiltinRstripChars(Interpreter i, Value str, Value chars)
    {
        if (str is not ValueString s)
        {
            throw i.Error("Unexpected type " + str.Type.Name + ", expected string");
        }
        int[] runes = s.GetRunes();
        int end = runes.Length;
        while (end > 0 && RawMember(i, chars, new ValueFlatString(new[] { runes[end - 1] })))
        {
            end--;
        }
        return end == runes.Length ? str : new ValueFlatString(ValueFlatString.RunesToString(runes, 0, end));
    }

    internal static Value BuiltinStripChars(Interpreter i, Value str, Value chars) =>
        BuiltinRstripChars(i, BuiltinLstripChars(i, str, chars), chars);

    internal static Value BuiltinSubstr(Interpreter i, Value inputStr, Value inputFrom, Value inputLen)
    {
        if (inputStr is not ValueString strV)
        {
            throw new RuntimeError("substr first parameter should be a string, got " + inputStr.Type.Name, i.GetCurrentStackTrace());
        }
        if (inputFrom is not ValueNumber fromV)
        {
            throw new RuntimeError("substr second parameter should be a number, got " + inputFrom.Type.Name, i.GetCurrentStackTrace());
        }
        if (fromV.Value % 1 != 0)
        {
            throw new RuntimeError("substr second parameter should be an integer, got " + GoStrconv.FormatFloatF(fromV.Value, 6), i.GetCurrentStackTrace());
        }
        if (fromV.Value < 0)
        {
            throw new RuntimeError("substr second parameter should be greater than zero, got " + GoStrconv.FormatFloatF(fromV.Value, 6), i.GetCurrentStackTrace());
        }
        if (inputLen is not ValueNumber lenV)
        {
            throw new RuntimeError("substr third parameter should be a number, got " + inputLen.Type.Name, i.GetCurrentStackTrace());
        }
        int lenInt;
        try
        {
            lenInt = i.GetInt(lenV);
        }
        catch (RuntimeError)
        {
            throw new RuntimeError("substr third parameter should be an integer, got " + GoStrconv.FormatFloatF(lenV.Value, 6), i.GetCurrentStackTrace());
        }
        if (lenInt < 0)
        {
            throw new RuntimeError("substr third parameter should be greater than zero, got " + lenInt.ToString(CultureInfo.InvariantCulture), i.GetCurrentStackTrace());
        }

        long fromInt = Util.GoFloatToInt64(fromV.Value);
        int[] strStr = strV.GetRunes();
        long endIndex = unchecked(fromInt + lenInt);
        if (endIndex > strStr.Length)
        {
            endIndex = strStr.Length;
        }
        if (fromInt > strStr.Length)
        {
            return ValueFlatString.Empty;
        }
        // Only reachable when int(from) overflowed to math.MinInt64.
        if (endIndex < 0)
        {
            throw GoPanic("runtime error: slice bounds out of range [:" + endIndex.ToString(CultureInfo.InvariantCulture) + "]");
        }
        if (fromInt < 0)
        {
            throw GoPanic("runtime error: slice bounds out of range [" + fromInt.ToString(CultureInfo.InvariantCulture) + ":" +
                endIndex.ToString(CultureInfo.InvariantCulture) + "]");
        }
        return new ValueFlatString(ValueFlatString.RunesToString(strStr, (int)fromInt, (int)(endIndex - fromInt)));
    }

    private static ValueArray MakeStringArray(List<string> strs)
    {
        CachedThunk[] res = new CachedThunk[strs.Count];
        for (int k = 0; k < res.Length; k++)
        {
            res[k] = CachedThunk.ReadyThunk(new ValueFlatString(strs[k]));
        }
        return new ValueArray(res);
    }

    internal static Value BuiltinSplitLimit(Interpreter i, Value strv, Value cv, Value maxSplitsV)
    {
        ValueString str = i.GetString(strv);
        ValueString c = i.GetString(cv);
        int maxSplits = i.GetInt(maxSplitsV);
        if (maxSplits < -1)
        {
            throw i.Error("std.splitLimit third parameter should be -1 or non-negative, got " + maxSplits.ToString(CultureInfo.InvariantCulture));
        }
        string sStr = str.GetGoString();
        string sC = c.GetGoString();
        if (sC.Length < 1)
        {
            throw i.Error("std.splitLimit second parameter should have length 1 or greater, got 0");
        }

        // strings.SplitN(sStr, sC, n) with n = -1 or maxSplits+1 (the convention is shifted by one).
        List<string> strs = new();
        long n = maxSplits == -1 ? long.MaxValue : (long)maxSplits + 1;
        int pos = 0;
        while (strs.Count < n - 1)
        {
            int m = sStr.IndexOf(sC, pos, StringComparison.Ordinal);
            if (m < 0)
            {
                break;
            }
            strs.Add(sStr.Substring(pos, m - pos));
            pos = m + sC.Length;
        }
        strs.Add(sStr.Substring(pos));
        return MakeStringArray(strs);
    }

    internal static Value BuiltinSplitLimitR(Interpreter i, Value strv, Value cv, Value maxSplitsV)
    {
        ValueString str = i.GetString(strv);
        ValueString c = i.GetString(cv);
        int maxSplits = i.GetInt(maxSplitsV);
        if (maxSplits < -1)
        {
            throw i.Error("std.splitLimitR third parameter should be -1 or non-negative, got " + maxSplits.ToString(CultureInfo.InvariantCulture));
        }
        string sStr = str.GetGoString();
        string sC = c.GetGoString();
        if (sC.Length < 1)
        {
            throw i.Error("std.splitLimitR second parameter should have length 1 or greater, got 0");
        }

        int count = CountOrdinal(sStr, sC);
        if (maxSplits > -1 && count > maxSplits)
        {
            count = maxSplits;
        }
        string[] strs = new string[count + 1];
        // Go slices sStr[:index] in O(1); shrinking an end index instead of the string keeps this linear.
        int end = sStr.Length;
        for (int k = count; k > 0; k--)
        {
            int index = sStr.LastIndexOf(sC, end - 1, end, StringComparison.Ordinal);
            strs[k] = sStr.Substring(index + sC.Length, end - index - sC.Length);
            end = index;
        }
        strs[0] = sStr.Substring(0, end);
        return MakeStringArray(new List<string>(strs));
    }

    // strings.Count for a non-empty sep: non-overlapping occurrences.
    private static int CountOrdinal(string s, string sep)
    {
        int n = 0;
        int pos = 0;
        while ((pos = s.IndexOf(sep, pos, StringComparison.Ordinal)) >= 0)
        {
            n++;
            pos += sep.Length;
        }
        return n;
    }

    internal static Value BuiltinStrReplace(Interpreter i, Value strv, Value fromv, Value tov)
    {
        ValueString str = i.GetString(strv);
        ValueString from = i.GetString(fromv);
        ValueString to = i.GetString(tov);
        string sFrom = from.GetGoString();
        if (sFrom.Length == 0)
        {
            throw i.Error("'from' string must not be zero length.");
        }
        // string.Replace is ordinal, like strings.Replace.
        string s = str.GetGoString();
        string sTo = to.GetGoString();
        if (sTo.Length > sFrom.Length && s.Length + (long)CountOrdinal(s, sFrom) * (sTo.Length - sFrom.Length) > StringTooLongException.MaxLength)
        {
            throw new StringTooLongException();
        }
        return new ValueFlatString(s.Replace(sFrom, sTo));
    }

    internal static Value BuiltinIsEmpty(Interpreter i, Value strv) => MakeBool(i.GetString(strv).GetGoString().Length == 0);

    internal static Value BuiltinEqualsIgnoreCase(Interpreter i, Value sv1, Value sv2)
    {
        ValueString s1 = i.GetString(sv1);
        ValueString s2 = i.GetString(sv2);
        return MakeBool(EqualFold(s1.GetRunes(), s2.GetRunes()));
    }

    // strings.EqualFold under simple Unicode case folding.
    // shortcut: approximates Go's SimpleFold orbits with Unicode 15.0 simple upper/lower casing (fixed tables,
    // so every runtime agrees) plus the special simple case foldings (CaseFolding.txt) that casing alone misses.
    // It matched go-jsonnet on all 2974 case pairs tried; port unicode.SimpleFold if a gap shows.
    private static readonly Dictionary<int, int> SpecialFolds = new()
    {
        [0x00B5] = 0x03BC, [0x017F] = 0x0073, [0x0345] = 0x03B9, [0x03C2] = 0x03C3, [0x03D0] = 0x03B2,
        [0x03D1] = 0x03B8, [0x03D5] = 0x03C6, [0x03D6] = 0x03C0, [0x03F0] = 0x03BA, [0x03F1] = 0x03C1,
        [0x03F4] = 0x03B8, [0x03F5] = 0x03B5, [0x1E9B] = 0x1E61, [0x1E9E] = 0x00DF, [0x1FBE] = 0x03B9,
        [0x2126] = 0x03C9, [0x212A] = 0x006B, [0x212B] = 0x00E5,
        [0x01C4] = 0x01C6, [0x01C5] = 0x01C6, [0x01C7] = 0x01C9, [0x01C8] = 0x01C9,
        [0x01CA] = 0x01CC, [0x01CB] = 0x01CC, [0x01F1] = 0x01F3, [0x01F2] = 0x01F3,
        [0x1C80] = 0x0432, [0x1C81] = 0x0434, [0x1C82] = 0x043E, [0x1C83] = 0x0441, [0x1C84] = 0x0442,
        [0x1C85] = 0x0442, [0x1C86] = 0x044A, [0x1C87] = 0x0463, [0x1C88] = 0xA64B,
    };

    private static bool EqualFold(int[] a, int[] b)
    {
        if (a.Length != b.Length)
        {
            return false;
        }
        for (int k = 0; k < a.Length; k++)
        {
            int r1 = SpecialFolds.TryGetValue(a[k], out int f1) ? f1 : a[k];
            int r2 = SpecialFolds.TryGetValue(b[k], out int f2) ? f2 : b[k];
            if (r1 == r2)
            {
                continue;
            }
            if (UnicodeTables.ToUpper(r1) != UnicodeTables.ToUpper(r2) && UnicodeTables.ToLower(r1) != UnicodeTables.ToLower(r2))
            {
                return false;
            }
        }
        return true;
    }

    // strings.TrimSpace; char.IsWhiteSpace is the same set as Go's unicode.IsSpace.
    internal static Value BuiltinTrim(Interpreter i, Value strv) => new ValueFlatString(i.GetString(strv).GetGoString().Trim());

    // Maximum allowed unicode codepoint
    // https://en.wikipedia.org/wiki/Unicode#Architecture_and_terminology
    private const int CodepointMax = 0x10FFFF;

    internal static Value BuiltinChar(Interpreter i, Value x)
    {
        ValueNumber n = i.GetNumber(x);
        if (n.Value > CodepointMax)
        {
            throw i.Error("Invalid unicode codepoint, got " + Interpreter.FormatFloatV(n.Value));
        }
        else if (n.Value < 0)
        {
            throw i.Error("Codepoints must be >= 0, got " + Interpreter.FormatFloatV(n.Value));
        }
        // string(rune(n)) turns surrogates into U+FFFD.
        int r = (int)n.Value;
        return new ValueFlatString(new[] { GoUtf8.ValidRune(r) ? r : GoUtf8.RuneError });
    }

    internal static Value BuiltinCodepoint(Interpreter i, Value x)
    {
        ValueString str = i.GetString(x);
        if (str.Length() != 1)
        {
            throw i.Error("codepoint takes a string of length 1, got length " + str.Length().ToString(CultureInfo.InvariantCulture));
        }
        return new ValueNumber(str.GetRunes()[0]);
    }

    internal static Value BuiltinEncodeUTF8(Interpreter i, Value x)
    {
        byte[] bytes = GoBytes(i.GetString(x));
        CachedThunk[] elems = new CachedThunk[bytes.Length];
        for (int k = 0; k < bytes.Length; k++)
        {
            elems[k] = CachedThunk.ReadyThunk(new ValueNumber(bytes[k]));
        }
        return new ValueArray(elems);
    }

    internal static Value BuiltinDecodeUTF8(Interpreter i, Value x)
    {
        ValueArray arr = i.GetArray(x);
        byte[] bs = new byte[arr.Elements.Length];
        for (int pos = 0; pos < bs.Length; pos++)
        {
            int v = i.EvaluateInt(arr.Elements[pos]);
            if (v < 0 || v > 255)
            {
                throw i.Error("Bytes must be integers in range [0, 255], got " + v.ToString(CultureInfo.InvariantCulture));
            }
            bs[pos] = (byte)v;
        }
        return new ValueFlatString(Yaml.GoString.FromBytes(bs));
    }

    private static string Base64InvalidCodepoint(int v) =>
        "base64 encountered invalid codepoint value in the array (must be 0 <= X <= 255), got " + v.ToString(CultureInfo.InvariantCulture);

    internal static Value BuiltinBase64(Interpreter i, Value input)
    {
        byte[] byteArr;
        switch (input)
        {
            case ValueString vStr:
                foreach (int r in vStr.GetRunes())
                {
                    if (r < 0 || 255 < r)
                    {
                        throw new RuntimeError(Base64InvalidCodepoint(r), i.GetCurrentStackTrace());
                    }
                }
                // Go encodes the UTF-8 bytes, so code points 128..255 take two bytes each.
                byteArr = GoBytes(vStr);
                break;
            case ValueArray vArr:
                byteArr = new byte[vArr.Elements.Length];
                for (int k = 0; k < byteArr.Length; k++)
                {
                    Value cTv = vArr.Elements[k].GetValue(i);
                    int vInt;
                    try
                    {
                        vInt = i.GetInt(cTv);
                    }
                    catch (RuntimeError)
                    {
                        throw new RuntimeError("base64 encountered a non-integer value in the array, got " + cTv.Type.Name, i.GetCurrentStackTrace());
                    }
                    if (vInt < 0 || 255 < vInt)
                    {
                        throw new RuntimeError(Base64InvalidCodepoint(vInt), i.GetCurrentStackTrace());
                    }
                    byteArr[k] = (byte)vInt;
                }
                break;
            default:
                throw new RuntimeError("base64 can only base64 encode strings / arrays of single bytes, got " + input.Type.Name, i.GetCurrentStackTrace());
        }
        return new ValueFlatString(Convert.ToBase64String(byteArr));
    }

    private static byte[] Base64DecodeGoBytes(Interpreter i, string str)
    {
        int strLen = Utf8.GetByteCount(str);
        if (strLen % 4 != 0)
        {
            throw new RuntimeError("input string appears not to be a base64 encoded string. Wrong length found (" +
                strLen.ToString(CultureInfo.InvariantCulture) + ")", i.GetCurrentStackTrace());
        }
        byte[]? decoded = GoBase64Decode(str, out int errOffset);
        if (decoded == null)
        {
            throw i.Error("failed to decode: illegal base64 data at input byte " + errOffset.ToString(CultureInfo.InvariantCulture));
        }
        return decoded;
    }

    // base64.StdEncoding.DecodeString: strict padding, '\r' and '\n' are ignored anywhere.
    // Returns null and the byte offset Go reports in its CorruptInputError on failure.
    private static byte[]? GoBase64Decode(string str, out int errOffset)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        byte[] src = Utf8.GetBytes(str);
        List<byte> dst = new(src.Length / 4 * 3);
        int si = 0;
        errOffset = 0;
        int[] dbuf = new int[4];
        while (true)
        {
            int dlen = 4;
            for (int j = 0; j < 4; j++)
            {
                if (si == src.Length)
                {
                    if (j == 0)
                    {
                        return dst.ToArray();
                    }
                    // One leftover character, or missing padding (StdEncoding requires it).
                    errOffset = si - j;
                    return null;
                }
                byte input = src[si];
                si++;
                int out1 = input < 0x80 ? Alphabet.IndexOf((char)input) : -1;
                if (out1 >= 0)
                {
                    dbuf[j] = out1;
                    continue;
                }
                if (input == '\n' || input == '\r')
                {
                    j--;
                    continue;
                }
                if (input != '=')
                {
                    errOffset = si - 1;
                    return null;
                }

                // We've reached the end and there's padding
                switch (j)
                {
                    case 0:
                    case 1:
                        // incorrect padding
                        errOffset = si - 1;
                        return null;
                    case 2:
                        // "==" is expected, the first "=" is already consumed.
                        // skip over newlines
                        while (si < src.Length && (src[si] == '\n' || src[si] == '\r'))
                        {
                            si++;
                        }
                        if (si == src.Length)
                        {
                            // not enough padding
                            errOffset = src.Length;
                            return null;
                        }
                        if (src[si] != '=')
                        {
                            // incorrect padding
                            errOffset = si - 1;
                            return null;
                        }
                        si++;
                        break;
                }
                // skip over newlines
                while (si < src.Length && (src[si] == '\n' || src[si] == '\r'))
                {
                    si++;
                }
                if (si < src.Length)
                {
                    // trailing garbage
                    errOffset = si;
                    return null;
                }
                dlen = j;
                break;
            }

            int val = (dbuf[0] << 18) | (dbuf[1] << 12) | (dbuf[2] << 6) | dbuf[3];
            byte b0 = (byte)(val >> 16);
            byte b1 = (byte)(val >> 8);
            byte b2 = (byte)val;
            switch (dlen)
            {
                case 4:
                    dst.Add(b0);
                    dst.Add(b1);
                    dst.Add(b2);
                    break;
                case 3:
                    dst.Add(b0);
                    dst.Add(b1);
                    break;
                case 2:
                    dst.Add(b0);
                    break;
            }
            if (dlen < 4)
            {
                return dst.ToArray();
            }
        }
    }

    internal static Value BuiltinBase64DecodeBytes(Interpreter i, Value input)
    {
        if (input is not ValueString vStr)
        {
            throw new RuntimeError("base64DecodeBytes requires a string, got " + input.Type.Name, i.GetCurrentStackTrace());
        }
        byte[] decodedBytes = Base64DecodeGoBytes(i, vStr.GetGoString());
        CachedThunk[] res = new CachedThunk[decodedBytes.Length];
        for (int k = 0; k < res.Length; k++)
        {
            res[k] = CachedThunk.ReadyThunk(new ValueNumber(decodedBytes[k]));
        }
        return new ValueArray(res);
    }

    internal static Value BuiltinBase64Decode(Interpreter i, Value input)
    {
        if (input is not ValueString vStr)
        {
            throw new RuntimeError("base64DecodeBytes requires a string, got " + input.Type.Name, i.GetCurrentStackTrace());
        }
        byte[] decodedBytes = Base64DecodeGoBytes(i, vStr.GetGoString());
        return new ValueFlatString(Yaml.GoString.FromBytes(decodedBytes));
    }

    internal static Value BuiltinParseInt(Interpreter i, Value x)
    {
        ValueString str = i.GetString(x);
        string s = str.GetGoString();
        // strconv.ParseInt(s, 10, 64): an optional sign and ASCII digits only.
        if (!GoStrconv.ParseInt64(s, out long res))
        {
            throw i.Error(s + " is not a base 10 integer");
        }
        return new ValueNumber(res);
    }

    // Hashes the UTF-8 bytes in chunks: they can be more than the largest .NET array holds.
    private static Value HashBuiltin(Interpreter i, Value x, HashAlgorithm algorithm)
    {
        using HashAlgorithm h = algorithm;
        string s = i.GetString(x).GetGoString();
        const int chunk = 1 << 20;
        byte[] buf = new byte[Utf8.GetMaxByteCount(chunk)];
        for (int start = 0; start < s.Length;)
        {
            int n = Math.Min(chunk, s.Length - start);
            if (n < s.Length - start && char.IsHighSurrogate(s[start + n - 1]))
            {
                n--;
            }
            h.TransformBlock(buf, 0, Utf8.GetBytes(s, start, n, buf, 0), null, 0);
            start += n;
        }
        h.TransformFinalBlock(buf, 0, 0);
        byte[] sum = h.Hash!;
        StringBuilder sb = new(sum.Length * 2);
        foreach (byte b in sum)
        {
            sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        }
        return new ValueFlatString(sb.ToString());
    }

    internal static Value BuiltinMd5(Interpreter i, Value x) => HashBuiltin(i, x, MD5.Create());

    internal static Value BuiltinSha1(Interpreter i, Value x) => HashBuiltin(i, x, SHA1.Create());

    internal static Value BuiltinSha256(Interpreter i, Value x) => HashBuiltin(i, x, SHA256.Create());

    internal static Value BuiltinSha512(Interpreter i, Value x) => HashBuiltin(i, x, SHA512.Create());

    internal static Value BuiltinSha3(Interpreter i, Value x) => HashBuiltin(i, x, new Sha3());
}
