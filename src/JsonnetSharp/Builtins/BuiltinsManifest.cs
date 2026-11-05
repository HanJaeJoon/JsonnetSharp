// Ports the parsing and manifestation builtins of go-jsonnet builtins.go: parseJson, parseYaml,
// manifestJsonEx (with jsonEncode), manifestTomlEx (tomlIsSection, tomlEncodeKey, tomlRenderValue,
// tomlRenderTableArray, tomlRenderTable, tomlTableInternal) and manifestYamlDoc (yamlBareSafe).

using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;

namespace JsonnetSharp;

internal static partial class Builtins
{
    internal static Value BuiltinParseJSON(Interpreter i, Value str)
    {
        string s = i.GetString(str).GetGoString();
        try
        {
            return GoJson.Unmarshal(s);
        }
        catch (GoJsonException e)
        {
            throw i.Error("failed to parse JSON: " + e.Message);
        }
    }

    internal static Value BuiltinParseYAML(Interpreter i, Value str)
    {
        string s = i.GetString(str).GetGoString();
        object? parsed;
        try
        {
            parsed = Yaml.YamlToJson.ParseYaml(s);
        }
        catch (Yaml.YamlException e)
        {
            throw i.Error("failed to parse YAML: " + e.Message);
        }
        return Interpreter.JsonToValue(i, parsed);
    }

    private const string MaxManifestDepthMsg = "max manifest depth exceeded, possible infinite recursion";

    // fmt's %v of a []string.
    private static string GoStringSlice(List<string> path) => "[" + string.Join(" ", path) + "]";

    private static List<string> AddToPath(List<string> path, string tail) => new(path) { tail };

    // tomlIsSection checks whether an object or array is a section - a TOML section is an
    // object or an an array has all of its children being objects
    private static bool TomlIsSection(Interpreter i, Value val)
    {
        switch (val)
        {
            case ValueObject:
                return true;
            case ValueArray v:
                if (v.Length() == 0)
                {
                    return false;
                }
                foreach (CachedThunk thunk in v.Elements)
                {
                    if (thunk.GetValue(i) is not ValueObject)
                    {
                        return false;
                    }
                }
                return true;
            default:
                return false;
        }
    }

    // tomlEncodeKey encodes a key - returning same string if it does not need quoting,
    // otherwise return it quoted; returns empty key as ''
    private static string TomlEncodeKey(string s)
    {
        if (s.Length == 0)
        {
            return "''";
        }
        foreach (char c in s)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c == '-' || c == '_')
            {
                continue;
            }
            return Interpreter.UnparseString(s);
        }
        return s;
    }

    // tomlRenderValue returns a rendered value as string, with proper indenting
    private static string TomlRenderValue(Interpreter i, Value val, string sindent, List<string> indexedPath, bool inline, string cindent, int depth)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (depth <= 0)
        {
            throw i.Error(MaxManifestDepthMsg);
        }
        switch (val)
        {
            case ValueNull:
                throw i.Error("Tried to manifest \"null\" at " + GoStringSlice(indexedPath));
            case ValueBoolean b:
                return b.Value ? "true" : "false";
            case ValueNumber n:
                return Interpreter.UnparseNumber(n.Value);
            case ValueString s:
                return Interpreter.UnparseString(s.GetGoString());
            case ValueFunction:
                throw i.Error("Tried to manifest function at " + GoStringSlice(indexedPath));
            case ValueArray v:
            {
                if (v.Elements.Length == 0)
                {
                    return "[]";
                }

                // initialize indenting and separators based on whether this is added inline or not
                string newIndent = cindent + sindent;
                string separator = "\n";
                if (inline)
                {
                    newIndent = "";
                    separator = " ";
                }

                // open the square bracket to start array values
                StringBuilder res = new("[" + separator);

                // iterate over elents and add their values to result
                for (int j = 0; j < v.Elements.Length; j++)
                {
                    Value thunkValue = v.Elements[j].GetValue(i);
                    List<string> childIndexedPath = AddToPath(indexedPath, j.ToString(CultureInfo.InvariantCulture));
                    if (j > 0)
                    {
                        res.Append(',').Append(separator);
                    }
                    res.Append(newIndent);
                    res.Append(TomlRenderValue(i, thunkValue, sindent, childIndexedPath, true, "", depth - 1));
                }

                res.Append(separator);
                if (inline)
                {
                    res.Append(cindent);
                }

                // close the array and return it
                return res.Append(']').ToString();
            }
            case ValueObject v:
            {
                StringBuilder res = new();
                List<string> fields = Objects.ObjectFields(v, Hidden.WithoutHidden);
                fields.Sort(Util.GoStringComparer);

                // iterate over sorted field keys and render their values
                for (int j = 0; j < fields.Count; j++)
                {
                    string fieldName = fields[j];
                    Value fieldValue = v.Index(i, fieldName);
                    List<string> childIndexedPath = AddToPath(indexedPath, fieldName);
                    string value = TomlRenderValue(i, fieldValue, sindent, childIndexedPath, true, "", depth - 1);
                    if (j > 0)
                    {
                        res.Append(", ");
                    }
                    res.Append(TomlEncodeKey(fieldName)).Append(" = ").Append(value);
                }

                // wrap fields in an array
                return "{ " + res + " }";
            }
            default:
                throw i.Error("Unknown object type " + val.GetType().Name + " at " + GoStringSlice(indexedPath));
        }
    }

    private static string TomlSectionName(string cindent, string open, List<string> path, string close)
    {
        StringBuilder section = new(cindent + open);
        for (int k = 0; k < path.Count; k++)
        {
            if (k > 0)
            {
                section.Append('.');
            }
            section.Append(TomlEncodeKey(path[k]));
        }
        return section.Append(close).ToString();
    }

    private static string TomlRenderTableArray(Interpreter i, ValueArray v, string sindent, List<string> path, List<string> indexedPath, string cindent, int depth)
    {
        if (depth <= 0)
        {
            throw i.Error(MaxManifestDepthMsg);
        }

        List<string> sections = new(v.Elements.Length);

        // render all elements of an array
        for (int j = 0; j < v.Elements.Length; j++)
        {
            Value thunkValue = v.Elements[j].GetValue(i);
            if (thunkValue is not ValueObject tv)
            {
                throw i.Error("invalid type for section: " + thunkValue.GetType().Name);
            }

            // render the entire path as section name
            string section = TomlSectionName(cindent, "[[", path, "]]");

            // add newline if the table has elements
            if (Objects.ObjectFields(tv, Hidden.WithoutHidden).Count > 0)
            {
                section += "\n";
            }

            List<string> childIndexedPath = AddToPath(indexedPath, j.ToString(CultureInfo.InvariantCulture));

            // render the table and add it to result
            section += TomlTableInternal(i, tv, sindent, path, childIndexedPath, cindent + sindent, depth - 1);
            sections.Add(section);
        }

        // combine all sections
        return string.Join("\n\n", sections);
    }

    private static string TomlRenderTable(Interpreter i, ValueObject v, string sindent, List<string> path, List<string> indexedPath, string cindent, int depth)
    {
        if (depth <= 0)
        {
            throw i.Error(MaxManifestDepthMsg);
        }
        string res = TomlSectionName(cindent, "[", path, "]");
        if (Objects.ObjectFields(v, Hidden.WithoutHidden).Count > 0)
        {
            res += "\n";
        }
        return res + TomlTableInternal(i, v, sindent, path, indexedPath, cindent + sindent, depth - 1);
    }

    private static string TomlTableInternal(Interpreter i, ValueObject v, string sindent, List<string> path, List<string> indexedPath, string cindent, int depth)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        if (depth <= 0)
        {
            throw i.Error(MaxManifestDepthMsg);
        }
        List<string> resFields = new();
        List<string> resSections = new() { "" };
        List<string> fields = Objects.ObjectFields(v, Hidden.WithoutHidden);
        fields.Sort(Util.GoStringComparer);

        // iterate over non-section items
        foreach (string fieldName in fields)
        {
            Value fieldValue = v.Index(i, fieldName);
            bool isSection = TomlIsSection(i, fieldValue);
            List<string> childIndexedPath = AddToPath(indexedPath, fieldName);

            if (isSection)
            {
                // render as section and add to array of sections
                List<string> childPath = AddToPath(path, fieldName);
                switch (fieldValue)
                {
                    case ValueObject fv:
                        resSections.Add(TomlRenderTable(i, fv, sindent, childPath, childIndexedPath, cindent, depth - 1));
                        break;
                    case ValueArray fv:
                        resSections.Add(TomlRenderTableArray(i, fv, sindent, childPath, childIndexedPath, cindent, depth - 1));
                        break;
                    default:
                        throw i.Error("invalid type for section: " + fieldValue.GetType().Name);
                }
            }
            else
            {
                // render as value and append to result fields
                string renderedValue = TomlRenderValue(i, fieldValue, sindent, childIndexedPath, false, "", depth - 1);
                resFields.AddRange((TomlEncodeKey(fieldName) + " = " + renderedValue).Split('\n'));
            }
        }

        // create the result string
        string res = resFields.Count > 0 ? cindent : "";
        return res + string.Join("\n" + cindent, resFields) + string.Join("\n\n", resSections);
    }

    internal static Value BuiltinManifestTomlEx(Interpreter i, Value?[] arguments)
    {
        Value val = arguments[0]!;
        string sindent = i.GetString(arguments[1]!).GetGoString();
        if (val is ValueObject v)
        {
            return new ValueFlatString(TomlTableInternal(i, v, sindent, new List<string>(), new List<string>(), "", i.Stack.Limit));
        }
        throw i.Error("TOML body must be an object. Got " + val.Type.Name);
    }

    // We have a very similar logic here /interpreter.go@v0.16.0#L695 and here: /interpreter.go@v0.16.0#L627
    // These should ideally be unified
    // For backwards compatibility reasons, we are manually marshalling to json so we can control formatting
    // In the future, it might be apt to use a library [pretty-printing] function
    internal static Value BuiltinManifestJSONEx(Interpreter i, Value?[] arguments)
    {
        Value val = arguments[0]!;
        string sindent = i.GetString(arguments[1]!).GetGoString();
        string newline = i.GetString(arguments[2]!).GetGoString();
        string kvSep = i.GetString(arguments[3]!).GetGoString();

        StringBuilder buf = new();
        ManifestJsonExAux(i, buf, val, new List<string>(), "", i.Stack.Limit, sindent, newline, kvSep);
        return new ValueFlatString(Util.ToStringChecked(buf));
    }

    private static void ManifestJsonExAux(Interpreter i, StringBuilder buf, Value ov, List<string> path, string cindent, int depth,
        string sindent, string newline, string kvSep)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Util.CheckLength(buf);
        if (depth <= 0)
        {
            throw i.Error(MaxManifestDepthMsg);
        }
        switch (ov)
        {
            case ValueNull:
                buf.Append("null");
                break;
            case ValueString s:
                buf.Append(GoJson.EncodeString(s.GetGoString()));
                break;
            case ValueNumber n:
                buf.Append(GoStrconv.FormatFloatShortestF(n.Value));
                break;
            case ValueBoolean b:
                buf.Append(b.Value ? "true" : "false");
                break;
            case ValueFunction:
                throw i.Error("tried to manifest function at " + GoStringSlice(path));
            case ValueArray v:
            {
                string newIndent = cindent + sindent;
                buf.Append('[').Append(newline);
                for (int aI = 0; aI < v.Elements.Length; aI++)
                {
                    Value cTv = v.Elements[aI].GetValue(i);
                    if (aI > 0)
                    {
                        buf.Append(',').Append(newline);
                    }
                    buf.Append(newIndent);
                    ManifestJsonExAux(i, buf, cTv, AddToPath(path, aI.ToString(CultureInfo.InvariantCulture)), newIndent, depth - 1, sindent, newline, kvSep);
                }
                buf.Append(newline).Append(cindent).Append(']');
                break;
            }
            case ValueObject v:
            {
                string newIndent = cindent + sindent;
                buf.Append('{').Append(newline);
                List<string> fields = Objects.ObjectFields(v, Hidden.WithoutHidden);
                fields.Sort(Util.GoStringComparer);
                for (int k = 0; k < fields.Count; k++)
                {
                    string fieldName = fields[k];
                    Value fieldValue = v.Index(i, fieldName);
                    if (k > 0)
                    {
                        buf.Append(',').Append(newline);
                    }
                    buf.Append(newIndent).Append(GoJson.EncodeString(fieldName)).Append(kvSep);
                    ManifestJsonExAux(i, buf, fieldValue, AddToPath(path, fieldName), newIndent, depth - 1, sindent, newline, kvSep);
                }
                buf.Append(newline).Append(cindent).Append('}');
                break;
            }
            default:
                throw i.Error("unknown type to marshal to JSON: " + ov.GetType().Name);
        }
    }

    private const string YamlIndent = "  ";

    private static readonly string[] YamlReserved =
    {
        // Boolean types taken from https://yaml.org/type/bool.html
        "true", "false", "yes", "no", "on", "off", "y", "n",
        // Numerical words taken from https://yaml.org/type/float.html
        ".nan", "-.inf", "+.inf", ".inf", "null",
        // Invalid keys that contain no invalid characters
        "-", "---", "''",
    };

    private static readonly Regex YamlTimestampPattern = new("^(?:[0-9]*-){2}[0-9]*$", RegexOptions.CultureInvariant);
    private static readonly Regex YamlBinaryPattern = new("^[-+]?0b[0-1_]+$", RegexOptions.CultureInvariant);
    private static readonly Regex YamlHexPattern = new("[-+]?0x[0-9a-fA-F_]+", RegexOptions.CultureInvariant);

    private static bool YamlReservedString(string s)
    {
        foreach (string r in YamlReserved)
        {
            // s is ASCII here (yamlBareSafe checks the characters first), so EqualFold is an ASCII compare.
            if (string.Equals(s, r, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static bool YamlBareSafe(string s)
    {
        if (s.Length == 0)
        {
            return false;
        }

        // String contains unsafe char
        foreach (char c in s)
        {
            bool isAlpha = (c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z');
            bool isDigit = c >= '0' && c <= '9';
            if (!isAlpha && !isDigit && c != '_' && c != '-' && c != '/' && c != '.')
            {
                return false;
            }
        }

        if (YamlReservedString(s))
        {
            return false;
        }

        if (YamlTimestampPattern.IsMatch(s))
        {
            return false;
        }

        // Check binary /
        if (YamlBinaryPattern.IsMatch(s) || YamlHexPattern.IsMatch(s))
        {
            return false;
        }

        // Is integer
        if (GoStrconv.ParseInt64(s, out _))
        {
            return false;
        }
        // Is float
        if (GoStrconv.ParseFloatOk(s))
        {
            return false;
        }

        return true;
    }

    internal static Value BuiltinManifestYamlDoc(Interpreter i, Value?[] arguments)
    {
        Value val = arguments[0]!;
        bool indentArrInObj = i.GetBoolean(arguments[1]!).Value;
        bool quoteKeys = i.GetBoolean(arguments[2]!).Value;

        StringBuilder buf = new();
        ManifestYamlDocAux(i, val, buf, "", i.Stack.Limit, indentArrInObj, quoteKeys);
        return new ValueFlatString(Util.ToStringChecked(buf));
    }

    private static void ManifestYamlDocAux(Interpreter i, Value ov, StringBuilder buf, string cindent, int depth, bool indentArrInObj, bool quoteKeys)
    {
        RuntimeHelpers.EnsureSufficientExecutionStack();
        Util.CheckLength(buf);
        if (depth <= 0)
        {
            throw i.Error(MaxManifestDepthMsg);
        }
        switch (ov)
        {
            case ValueNull:
                buf.Append("null");
                break;
            case ValueBoolean b:
                buf.Append(b.Value ? "true" : "false");
                break;
            case ValueString vs:
            {
                string s = vs.GetGoString();
                if (s.Length == 0)
                {
                    buf.Append("\"\"");
                }
                else if (s.EndsWith("\n", StringComparison.Ordinal))
                {
                    s = s.Substring(0, s.Length - 1);
                    buf.Append('|');
                    foreach (string line in s.Split('\n'))
                    {
                        buf.Append('\n');
                        buf.Append(cindent);
                        buf.Append(YamlIndent);
                        buf.Append(line);
                    }
                }
                else
                {
                    Interpreter.UnparseString(buf, s);
                }
                break;
            }
            case ValueNumber n:
                buf.Append(GoStrconv.FormatFloatShortestF(n.Value));
                break;
            case ValueArray v:
            {
                if (v.Length() == 0)
                {
                    buf.Append("[]");
                    return;
                }
                for (int ix = 0; ix < v.Elements.Length; ix++)
                {
                    if (ix != 0)
                    {
                        buf.Append('\n');
                        buf.Append(cindent);
                    }
                    Value thunkValue = v.Elements[ix].GetValue(i);
                    buf.Append('-');

                    if (thunkValue is ValueArray arr && arr.Length() > 0)
                    {
                        buf.Append('\n');
                        buf.Append(cindent);
                        buf.Append(YamlIndent);
                    }
                    else
                    {
                        buf.Append(' ');
                    }

                    string childIndent = thunkValue is ValueArray or ValueObject ? cindent + YamlIndent : cindent;
                    ManifestYamlDocAux(i, thunkValue, buf, childIndent, depth - 1, indentArrInObj, quoteKeys);
                }
                break;
            }
            case ValueObject v:
            {
                List<string> fields = Objects.ObjectFields(v, Hidden.WithoutHidden);
                if (fields.Count == 0)
                {
                    buf.Append("{}");
                    return;
                }
                fields.Sort(Util.GoStringComparer);
                for (int ix = 0; ix < fields.Count; ix++)
                {
                    string fieldName = fields[ix];
                    Value fieldValue = v.Index(i, fieldName);

                    if (ix != 0)
                    {
                        buf.Append('\n');
                        buf.Append(cindent);
                    }

                    if (quoteKeys || !YamlBareSafe(fieldName))
                    {
                        Interpreter.UnparseString(buf, fieldName);
                    }
                    else
                    {
                        buf.Append(fieldName);
                    }
                    buf.Append(':');

                    string childIndent = cindent;
                    if (fieldValue is ValueArray arr && arr.Length() > 0)
                    {
                        buf.Append('\n');
                        buf.Append(cindent);
                        if (indentArrInObj)
                        {
                            buf.Append(YamlIndent);
                            childIndent = cindent + YamlIndent;
                        }
                    }
                    else if (fieldValue is ValueObject obj)
                    {
                        if (Objects.ObjectFields(obj, Hidden.WithoutHidden).Count > 0)
                        {
                            buf.Append('\n');
                            buf.Append(cindent);
                            buf.Append(YamlIndent);
                            childIndent = cindent + YamlIndent;
                        }
                        else
                        {
                            buf.Append(' ');
                        }
                    }
                    else
                    {
                        buf.Append(' ');
                    }
                    ManifestYamlDocAux(i, fieldValue, buf, childIndent, depth - 1, indentArrInObj, quoteKeys);
                }
                break;
            }
        }
    }
}
