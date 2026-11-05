// Ports go-jsonnet error_formatter.go (termErrorFormatter without color).

using System.Globalization;
using System.Text;
using JsonnetSharp.Ast;

namespace JsonnetSharp;

internal sealed class TermErrorFormatter
{
    // go-jsonnet links its own tracker; a crash here is a bug in this port.
    private const string BugURL = "https://github.com/HanJaeJoon/JsonnetSharp/issues";

    // sp is never set in Go either; a SourceProvider is stateless.
    private readonly SourceProvider _sp = new();

    // maxStackTraceSize is the maximum length of stack trace before cropping
    public int MaxStackTraceSize;
    public bool Pretty;

    public TermErrorFormatter(int maxStackTraceSize, bool pretty)
    {
        MaxStackTraceSize = maxStackTraceSize;
        Pretty = pretty;
    }

    public string Format(Exception err) => err switch
    {
        RuntimeError r => FormatRuntime(r),
        StaticError s => FormatStatic(s),
        _ => FormatInternal(err),
    };

    private string FormatRuntime(RuntimeError err) => err.Error() + "\n" + BuildStackTrace(err.StackTraceFrames);

    private string FormatStatic(StaticError err)
    {
        StringBuilder buf = new();
        buf.Append(err.Error()).Append('\n');
        ShowCode(buf, err.Loc);
        return buf.ToString();
    }

    internal static string FormatInternal(Exception err) => FormatInternal(err.Message);

    internal static string FormatInternal(string message) =>
        "INTERNAL ERROR: " + message + "\n" +
        "Please report a bug here: " + BugURL + "\n";

    private void ShowCode(StringBuilder buf, LocationRange loc)
    {
        if (loc.WithCode())
        {
            // TODO(sbarzowski) include line numbers
            // TODO(sbarzowski) underline errors instead of depending only on color
            buf.Append('\n');
            LocationRange beginning = LocationRange.LineBeginning(loc);
            LocationRange ending = LocationRange.LineEnding(loc);
            buf.Append(_sp.GetSnippet(beginning));
            buf.Append(_sp.GetSnippet(loc));
            buf.Append(_sp.GetSnippet(ending));
            buf.Append('\n');
        }
        buf.Append('\n');
    }

    private void Frame(TraceFrame frame, StringBuilder buf)
    {
        // TODO(sbarzowski) tabs are probably a bad idea
        buf.Append('\t').Append(frame.Loc.ToString()).Append('\t').Append(frame.Name).Append('\n');
        if (Pretty)
        {
            ShowCode(buf, frame.Loc);
        }
    }

    private string BuildStackTrace(List<TraceFrame> frames)
    {
        // https://github.com/google/jsonnet/blob/master/core/libjsonnet.cpp#L594
        int maxAbove = MaxStackTraceSize / 2;
        int maxBelow = MaxStackTraceSize - maxAbove;
        StringBuilder buf = new();
        int sz = frames.Count;
        for (int i = 0; i < sz; i++)
        {
            // TODO(sbarzowski) make pretty format more readable (it's already useful)
            if (Pretty)
            {
                buf.Append("-------------------------------------------------\n");
            }
            if (MaxStackTraceSize > 0 && i >= maxAbove && i < sz - maxBelow)
            {
                if (Pretty)
                {
                    buf.Append("\t... (skipped ").Append((sz - maxAbove - maxBelow).ToString(CultureInfo.InvariantCulture)).Append(" frames)\n");
                }
                else
                {
                    buf.Append("\t...\n");
                }

                i = sz - maxBelow - 1;
            }
            else
            {
                Frame(frames[sz - i - 1], buf);
            }
        }
        return buf.ToString();
    }
}
