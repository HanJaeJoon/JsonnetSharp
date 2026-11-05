// Ports the parts of Go's path/filepath that FileImporter uses (IsAbs, Split, Join, Clean),
// for the host OS like Go does: on Windows '\' and '/' are separators and paths have volumes.

using System.Text;

namespace JsonnetSharp;

internal static class GoFilePath
{
    private static readonly bool Windows = Path.DirectorySeparatorChar == '\\';
    private static readonly char Separator = Windows ? '\\' : '/';

    private static bool IsSlash(char c) => c == '/' || (Windows && c == '\\');

    // shortcut: drive letters and \\host\share UNC paths only; Go also knows \\?\ and \\.\ device paths.
    private static int VolumeNameLen(string path)
    {
        if (!Windows)
        {
            return 0;
        }
        if (path.Length >= 2 && path[1] == ':')
        {
            return 2;
        }
        if (path.Length < 2 || !IsSlash(path[0]) || !IsSlash(path[1]))
        {
            return 0;
        }
        // \\host\share
        int k = 2;
        while (k < path.Length && !IsSlash(path[k]))
        {
            k++;
        }
        if (k >= path.Length)
        {
            return path.Length;
        }
        k++;
        while (k < path.Length && !IsSlash(path[k]))
        {
            k++;
        }
        return k;
    }

    internal static bool IsAbs(string path)
    {
        if (!Windows)
        {
            return path.StartsWith("/", StringComparison.Ordinal);
        }
        int l = VolumeNameLen(path);
        if (l == 0)
        {
            return false;
        }
        if (l > 2)
        {
            // UNC
            return true;
        }
        string rest = path.Substring(l);
        return rest.Length > 0 && IsSlash(rest[0]);
    }

    // os.fixLongPath (addExtendedPrefix): the \\?\ form lifts MAX_PATH, which .NET Framework applies.
    // Like Go, UNC and relative paths and paths with ".." elements are left alone.
    internal static string FixLongPath(string path)
    {
        if (!Windows || path.Length < 248 || !IsAbs(path) || VolumeNameLen(path) != 2)
        {
            return path;
        }
        StringBuilder sb = new(@"\\?\", path.Length + 5);
        sb.Append(path, 0, 2);
        int r = 2;
        int n = path.Length;
        while (r < n)
        {
            if (IsSlash(path[r]) || (path[r] == '.' && (r + 1 == n || IsSlash(path[r + 1]))))
            {
                r++;
            }
            else if (path[r] == '.' && path[r + 1] == '.' && (r + 2 == n || IsSlash(path[r + 2])))
            {
                return path;
            }
            else
            {
                sb.Append('\\');
                for (; r < n && !IsSlash(path[r]); r++)
                {
                    sb.Append(path[r]);
                }
            }
        }
        // A drive's root directory needs a trailing \
        if (sb.Length == 6)
        {
            sb.Append('\\');
        }
        return sb.ToString();
    }

    // Split splits path immediately following the final separator.
    internal static (string Dir, string File) Split(string path)
    {
        int vol = VolumeNameLen(path);
        int i = path.Length - 1;
        while (i >= vol && !IsSlash(path[i]))
        {
            i--;
        }
        return (path.Substring(0, i + 1), path.Substring(i + 1));
    }

    internal static string Join(string a, string b)
    {
        if (!Windows)
        {
            if (a.Length > 0)
            {
                return Clean(a + "/" + b);
            }
            return b.Length > 0 ? Clean(b) : "";
        }
        StringBuilder sb = new();
        char lastChar = '\0';
        foreach (string elem in new[] { a, b })
        {
            string e = elem;
            if (sb.Length == 0)
            {
                // Add the first non-empty path element unchanged.
            }
            else if (IsSlash(lastChar))
            {
                // Strip leading slashes to avoid creating a UNC path from non-UNC elements.
                int k = 0;
                while (k < e.Length && IsSlash(e[k]))
                {
                    k++;
                }
                e = e.Substring(k);
            }
            else if (lastChar == ':')
            {
                // Join(`C:`, `f`) = `C:f`
            }
            else
            {
                sb.Append('\\');
                lastChar = '\\';
            }
            if (e.Length > 0)
            {
                sb.Append(e);
                lastChar = e[e.Length - 1];
            }
        }
        return sb.Length == 0 ? "" : Clean(sb.ToString());
    }

    // shortcut: skips Go's Windows postClean (".\" prefix for "a:b"-like first elements, "\??" paths).
    internal static string Clean(string originalPath)
    {
        int volLen = VolumeNameLen(originalPath);
        string path = originalPath.Substring(volLen);
        if (path.Length == 0)
        {
            if (volLen > 1 && IsSlash(originalPath[0]) && IsSlash(originalPath[1]))
            {
                // should be UNC
                return FromSlash(originalPath);
            }
            return originalPath + ".";
        }
        bool rooted = IsSlash(path[0]);
        int n = path.Length;
        StringBuilder outBuf = new(n);
        int r = 0;
        int dotdot = 0;
        if (rooted)
        {
            outBuf.Append(Separator);
            r = 1;
            dotdot = 1;
        }
        while (r < n)
        {
            if (IsSlash(path[r]))
            {
                r++;
            }
            else if (path[r] == '.' && (r + 1 == n || IsSlash(path[r + 1])))
            {
                r++;
            }
            else if (path[r] == '.' && path[r + 1] == '.' && (r + 2 == n || IsSlash(path[r + 2])))
            {
                r += 2;
                if (outBuf.Length > dotdot)
                {
                    int w = outBuf.Length - 1;
                    while (w > dotdot && !IsSlash(outBuf[w]))
                    {
                        w--;
                    }
                    outBuf.Length = w;
                }
                else if (!rooted)
                {
                    if (outBuf.Length > 0)
                    {
                        outBuf.Append(Separator);
                    }
                    outBuf.Append("..");
                    dotdot = outBuf.Length;
                }
            }
            else
            {
                if ((rooted && outBuf.Length != 1) || (!rooted && outBuf.Length != 0))
                {
                    outBuf.Append(Separator);
                }
                for (; r < n && !IsSlash(path[r]); r++)
                {
                    outBuf.Append(path[r]);
                }
            }
        }
        if (outBuf.Length == 0)
        {
            outBuf.Append('.');
        }
        return FromSlash(originalPath.Substring(0, volLen) + outBuf);
    }

    private static string FromSlash(string path) => Windows ? path.Replace('/', '\\') : path;
}
