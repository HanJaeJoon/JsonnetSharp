// Ports go-jsonnet ast/location.go.

using System.Globalization;

namespace JsonnetSharp.Ast
{
    // Source represents a source file.
    internal sealed class Source
    {
        // The imported path or a special string for indicating stdin, extvars and
        // other non-imported sources. Only used for diagnostics, never passed to an importer.
        public string DiagnosticFileName;

        // Lines are kept as UTF-8 bytes because Location.Column is a byte offset (as in Go).
        // Every line ends with '\n'.
        public List<byte[]> Lines;

        public Source(string diagnosticFileName, List<byte[]> lines)
        {
            DiagnosticFileName = diagnosticFileName;
            Lines = lines;
        }

        // BuildSource transforms a source file string into a Source.
        public static Source Build(string dFilename, string s) => Build(dFilename, GoUtf8.Utf8.GetBytes(s));

        public static Source Build(string dFilename, byte[] s)
        {
            List<byte[]> result = new();
            int lineStart = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '\n')
                {
                    result.Add(Slice(s, lineStart, i + 1));
                    lineStart = i + 1;
                }
            }
            // Stuff after last end-of-line (EOF or some more code)
            byte[] rest = new byte[s.Length - lineStart + 1];
            System.Array.Copy(s, lineStart, rest, 0, s.Length - lineStart);
            rest[rest.Length - 1] = (byte)'\n';
            result.Add(rest);
            return new Source(dFilename, result);
        }

        private static byte[] Slice(byte[] s, int from, int to)
        {
            byte[] r = new byte[to - from];
            System.Array.Copy(s, from, r, 0, r.Length);
            return r;
        }
    }

    // Location represents a single location in an (unspecified) file.
    internal struct Location
    {
        public int Line;
        // Column is a byte offset from the beginning of the line
        public int Column;

        public Location(int line, int column)
        {
            Line = line;
            Column = column;
        }

        public readonly bool IsSet() => Line != 0;

        public override readonly string ToString() =>
            Line.ToString(CultureInfo.InvariantCulture) + ":" + Column.ToString(CultureInfo.InvariantCulture);

        // LocationBefore returns whether a refers to a location closer to the beginning of the file than b.
        public static bool Before(Location a, Location b)
        {
            if (a.Line != b.Line)
            {
                return a.Line < b.Line;
            }
            return a.Column < b.Column;
        }
    }

    // LocationRange represents a range of a source file.
    internal struct LocationRange
    {
        public Source? File;
        private string? _fileName;
        public Location Begin;
        public Location End;

        // FileName should be the imported path or "" for snippets etc.
        public string FileName
        {
            readonly get => _fileName ?? "";
            set => _fileName = value;
        }

        // MakeLocationRange
        public static LocationRange Make(string fn, Source? fc, Location begin, Location end) =>
            new() { FileName = fn, File = fc, Begin = begin, End = end };

        // MakeLocationRangeMessage creates a pseudo-LocationRange with a message but no location
        // information, e.g. for the manifestation entry point.
        public static LocationRange MakeMessage(string msg) => new() { FileName = msg };

        // LocationRangeBetween returns a LocationRange containing both a and b.
        public static LocationRange Between(in LocationRange a, in LocationRange b)
        {
            if (a.File != b.File)
            {
                throw new InvalidOperationException("Cannot create a LocationRange between different files");
            }
            return Make(a.FileName, a.File, a.Begin, b.End);
        }

        public readonly bool IsSet() => Begin.IsSet();

        // WithCode returns true iff the LocationRange is linked to code.
        public readonly bool WithCode() => Begin.Line != 0;

        public override readonly string ToString()
        {
            if (!IsSet())
            {
                return FileName;
            }
            string filePrefix = "";
            if (File!.DiagnosticFileName.Length > 0)
            {
                filePrefix = File.DiagnosticFileName + ":";
            }
            if (Begin.Line == End.Line)
            {
                if (Begin.Column == End.Column)
                {
                    return filePrefix + Begin.ToString();
                }
                return filePrefix + Begin.ToString() + "-" + End.Column.ToString(CultureInfo.InvariantCulture);
            }
            return filePrefix + "(" + Begin.ToString() + ")-(" + End.ToString() + ")";
        }

        // LineBeginning returns the part of a line directly before loc.
        public static LocationRange LineBeginning(in LocationRange loc) => new()
        {
            Begin = new Location(loc.Begin.Line, 1),
            End = loc.Begin,
            FileName = loc.FileName,
            File = loc.File,
        };

        // LineEnding returns the part of a line directly after loc.
        public static LocationRange LineEnding(in LocationRange loc) => new()
        {
            Begin = loc.End,
            End = new Location(loc.End.Line, loc.File!.Lines[loc.End.Line - 1].Length),
            FileName = loc.FileName,
            File = loc.File,
        };

        internal static LocationRange TrimToLine(LocationRange loc, int line)
        {
            if (loc.Begin.Line > line)
            {
                throw new InvalidOperationException("invalid");
            }
            if (loc.Begin.Line != line)
            {
                loc.Begin.Column = 1;
            }
            loc.Begin.Line = line;
            if (loc.End.Line < line)
            {
                throw new InvalidOperationException("invalid");
            }
            if (loc.End.Line != line)
            {
                loc.End.Column = loc.File!.Lines[line - 1].Length;
            }
            loc.End.Line = line;
            return loc;
        }
    }

    internal sealed class SourceProvider
    {
        // GetSnippet returns a code snippet corresponding to loc.
        public string GetSnippet(LocationRange loc)
        {
            if (loc.Begin.Line == 0)
            {
                return "";
            }
            List<byte> result = new();
            for (int i = loc.Begin.Line; i <= loc.End.Line; i++)
            {
                LocationRange inLineRange = LocationRange.TrimToLine(loc, i);
                byte[] lineBytes = loc.File!.Lines[i - 1];
                for (int j = inLineRange.Begin.Column; j < inLineRange.End.Column; j++)
                {
                    result.Add(lineBytes[j - 1]);
                }
                if (i != loc.End.Line)
                {
                    result.Add((byte)'\n');
                }
            }
            return GoUtf8.Utf8.GetString(result.ToArray());
        }
    }
}
