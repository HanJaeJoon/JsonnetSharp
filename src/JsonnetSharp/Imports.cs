// Ports go-jsonnet imports.go.

using JsonnetSharp.Ast;

namespace JsonnetSharp;

/// <summary>
/// Imports data from a path (go-jsonnet's <c>Importer</c>).
/// </summary>
/// <remarks>
/// It is required that for a given (importedFrom, importedPath) the same (contents, foundAt) are returned
/// on subsequent calls, and that for a given foundAt the very same <see cref="JsonnetContents"/> instance
/// is returned. Importing the same file multiple times must be cheap, so implementations should cache.
/// The passed importedFrom may be "" when the import comes from an anonymous snippet. Report a missing
/// file by throwing, not by returning null. An importer shared by VMs on several threads must be thread-safe.
/// </remarks>
public interface IJsonnetImporter
{
    /// <summary>
    /// Fetches the data for <paramref name="importedPath"/>, which may be relative to the file
    /// <paramref name="importedFrom"/>. Throw an exception to report failure; its message becomes the
    /// Jsonnet error message.
    /// </summary>
    /// <param name="importedFrom">The file the import appears in, or "" for an anonymous snippet.</param>
    /// <param name="importedPath">The path as written in the import expression.</param>
    /// <param name="foundAt">The canonical location of the data, used as the cache key and file name.</param>
    /// <returns>The imported data.</returns>
    JsonnetContents Import(string importedFrom, string importedPath, out string foundAt);
}

/// <summary>
/// Imported data: an immutable byte buffer (go-jsonnet's <c>Contents</c>).
/// Importers must return the same instance for the same location.
/// </summary>
public sealed class JsonnetContents
{
    internal readonly byte[] Bytes;
    private string? _string;

    private JsonnetContents(byte[] bytes) => Bytes = bytes;

    /// <summary>Creates contents from a string (stored as UTF-8).</summary>
    /// <param name="s">The text.</param>
    /// <returns>The contents.</returns>
    public static JsonnetContents FromString(string s) => new(GoUtf8.Utf8.GetBytes(s));

    /// <summary>Creates contents from raw, possibly non-UTF-8, bytes. The array is not copied.</summary>
    /// <param name="bytes">The data.</param>
    /// <returns>The contents.</returns>
    public static JsonnetContents FromBytes(byte[] bytes) => new(bytes ?? throw new ArgumentNullException(nameof(bytes)));

    /// <summary>Gets a copy of the raw bytes.</summary>
    public byte[] Data => (byte[])Bytes.Clone();

    /// <summary>Returns the data decoded as UTF-8; each invalid byte becomes U+FFFD, as in Go.</summary>
    /// <returns>The text.</returns>
    public override string ToString() => _string ??= Yaml.GoString.FromBytes(Bytes);
}

// An importer failure (Go: the error returned by Importer.Import). InnerException is the importer's exception.
internal sealed class ImportError : Exception
{
    public ImportError(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

// importCache represents a cache of imported data.
//
// While the user-defined Importer implementations
// are required to cache file content, this cache
// is an additional layer of optimization that caches values
// (i.e. the result of executing the file content).
// It also verifies that the content pointer is the same for two foundAt values.
internal sealed class ImportCache
{
    private readonly Dictionary<string, JsonnetContents> _foundAtVerification = new();
    private readonly Dictionary<string, (Node? Node, Exception? Err)> _astCache = new();
    private Dictionary<string, CachedThunk> _codeCache = new();
    private readonly IJsonnetImporter _importer;

    public ImportCache(IJsonnetImporter importer) => _importer = importer;

    public void FlushValueCache() => _codeCache = new Dictionary<string, CachedThunk>();

    // Throws ImportError when the importer fails.
    public JsonnetContents ImportData(string importedFrom, string importedPath, out string foundAt)
    {
        JsonnetContents contents;
        try
        {
            contents = _importer.Import(importedFrom, importedPath, out foundAt);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw new ImportError(e.Message, e);
        }
        if (contents == null || foundAt == null)
        {
            throw new ImportError("IJsonnetImporter.Import returned null " + (contents == null ? "contents" : "foundAt") +
                " for " + Yaml.GoFormat.Quote(importedPath));
        }
        if (_foundAtVerification.TryGetValue(foundAt, out JsonnetContents? cached))
        {
            if (!ReferenceEquals(cached, contents))
            {
                throw new InvalidOperationException("importer problem: a different instance of Contents returned when importing " +
                    Yaml.GoFormat.Quote(foundAt) + " again");
            }
        }
        else
        {
            _foundAtVerification[foundAt] = contents;
        }
        return contents;
    }

    // Throws ImportError or StaticError.
    public Node ImportAST(string importedFrom, string importedPath, out string foundAt)
    {
        JsonnetContents contents = ImportData(importedFrom, importedPath, out foundAt);
        if (!_astCache.TryGetValue(foundAt, out (Node? Node, Exception? Err) entry))
        {
            try
            {
                entry = (Program.SnippetToAST(foundAt, foundAt, contents.Bytes), null);
            }
            catch (StaticError e)
            {
                entry = (null, e);
            }
            _astCache[foundAt] = entry;
        }
        if (entry.Err != null)
        {
            throw entry.Err;
        }
        return entry.Node!;
    }

    // ImportString imports a string, caches it and then returns it.
    public ValueString ImportString(string importedFrom, string importedPath, Interpreter i)
    {
        JsonnetContents data;
        try
        {
            data = ImportData(importedFrom, importedPath, out _);
        }
        catch (ImportError e)
        {
            throw new RuntimeError(e.Message, i.GetCurrentStackTrace(), e.InnerException);
        }
        return new ValueFlatString(data.ToString());
    }

    // ImportBinary imports an array of bytes, caches it and then returns it.
    public ValueArray ImportBinary(string importedFrom, string importedPath, Interpreter i)
    {
        JsonnetContents data;
        try
        {
            data = ImportData(importedFrom, importedPath, out _);
        }
        catch (ImportError e)
        {
            throw new RuntimeError(e.Message, i.GetCurrentStackTrace(), e.InnerException);
        }
        byte[] bytes = data.Bytes;
        CachedThunk[] elements = new CachedThunk[bytes.Length];
        for (int k = 0; k < bytes.Length; k++)
        {
            elements[k] = CachedThunk.ReadyThunk(new ValueNumber(bytes[k]));
        }
        return new ValueArray(elements);
    }

    internal static CachedThunk NodeToPV(Interpreter i, string filename, Node node) =>
        new(Interpreter.MakeInitialEnv(filename, i.BaseStd), node);

    internal static CachedThunk CodeToPV(Interpreter i, string filename, string code)
    {
        Node node;
        try
        {
            node = Program.SnippetToAST(filename, "", code);
        }
        catch (StaticError e)
        {
            // TODO(sbarzowski) we should wrap (static) error here
            // within a RuntimeError. Because whether we get this error or not
            // actually depends on what happens in Runtime (whether import gets
            // evaluated).
            // The same thinking applies to external variables.
            return CachedThunk.ErrorThunk(e);
        }
        return NodeToPV(i, filename, node);
    }

    // ImportCode imports code from a path.
    public Value ImportCode(string importedFrom, string importedPath, Interpreter i)
    {
        Node node;
        string foundAt;
        try
        {
            node = ImportAST(importedFrom, importedPath, out foundAt);
        }
        catch (Exception e) when (e is ImportError || e is StaticError)
        {
            throw new RuntimeError(e.Message, i.GetCurrentStackTrace(), e.InnerException);
        }
        if (!_codeCache.TryGetValue(foundAt, out CachedThunk? pv))
        {
            // File hasn't been parsed and analyzed before, update the cache record.
            pv = new CachedThunk(Interpreter.MakeInitialEnv(foundAt, i.BaseStd), node);
            _codeCache[foundAt] = pv;
        }
        return i.EvaluatePV(pv);
    }
}

// Concrete importers
// -------------------------------------

/// <summary>
/// Imports data from the filesystem (go-jsonnet's <c>FileImporter</c>).
/// Relative imports are resolved against the importing file's directory first, then against
/// <see cref="JPaths"/> from the last entry to the first.
/// </summary>
/// <remarks>
/// Files are read once and cached for the importer's lifetime. An instance may be shared by VMs on
/// different threads (do not change <see cref="JPaths"/> meanwhile).
/// </remarks>
public sealed class FileImporter : IJsonnetImporter
{
    private readonly Dictionary<string, (bool Exists, JsonnetContents? Contents)> _fsCache = new();
    private IList<string> _jpaths = new List<string>();

    /// <summary>
    /// Gets or sets extra paths to search for relative imports. This is not an isolation or restriction
    /// mechanism: absolute paths and paths that go up the directory tree can reach any file.
    /// A <see langword="null"/> entry is treated as "".
    /// </summary>
    public IList<string> JPaths
    {
        get => _jpaths;
        set => _jpaths = value ?? throw new ArgumentNullException(nameof(value));
    }

    private bool TryPath(string dir, string importedPath, out JsonnetContents? contents, out string foundHere)
    {
        string absPath = GoFilePath.IsAbs(importedPath) ? importedPath : GoFilePath.Join(dir, importedPath);
        if (absPath.IndexOf('\0') >= 0)
        {
            // Go's os.ReadFile rejects the path before asking the OS; the error is not cached.
            throw new JsonnetException("open " + absPath + ": invalid argument");
        }
        lock (_fsCache)
        {
            return TryPathLocked(absPath, out contents, out foundHere);
        }
    }

    private bool TryPathLocked(string absPath, out JsonnetContents? contents, out string foundHere)
    {
        if (!_fsCache.TryGetValue(absPath, out (bool Exists, JsonnetContents? Contents) entry))
        {
            byte[]? contentBytes = null;
            string osPath = GoFilePath.FixLongPath(absPath);
            try
            {
                if (absPath.Length > 0)
                {
                    contentBytes = File.ReadAllBytes(osPath);
                }
            }
            catch (FileNotFoundException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            // A name the OS rejects (e.g. '"' on Windows) cannot exist, matching Go on Linux.
            catch (Exception e) when (e is ArgumentException || e is NotSupportedException || e.HResult == unchecked((int)0x8007007B))
            {
            }
            catch (UnauthorizedAccessException) when (Directory.Exists(osPath))
            {
                throw new JsonnetException("read " + absPath + ": is a directory");
            }
            // Go's *PathError, not cached; .NET's own text names the path differently and is localized.
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                throw new JsonnetException("open " + absPath + ": " + OsErrorText(e), e);
            }
            entry = contentBytes == null ? (false, null) : (true, JsonnetContents.FromBytes(contentBytes));
            _fsCache[absPath] = entry;
        }
        contents = entry.Contents;
        foundHere = absPath;
        return entry.Exists;
    }

    // syscall.Errno.Error(): Go asks Windows for the English text whatever the UI language.
    // shortcut: only the codes a readable path can give; others keep .NET's text.
    private static string OsErrorText(Exception e)
    {
        if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows))
        {
            switch (e.HResult & 0xFFFF)
            {
                case 5: // ERROR_ACCESS_DENIED
                    return "Access is denied.";
                case 32: // ERROR_SHARING_VIOLATION
                    return "The process cannot access the file because it is being used by another process.";
                case 33: // ERROR_LOCK_VIOLATION
                    return "The process cannot access the file because another process has locked a portion of the file.";
            }
        }
        else if (e is UnauthorizedAccessException)
        {
            return "permission denied";
        }
        return e.Message;
    }

    /// <summary>Imports a file from the filesystem.</summary>
    /// <param name="importedFrom">The importing file, or "" for an anonymous snippet.</param>
    /// <param name="importedPath">The path as written in the import expression.</param>
    /// <param name="foundAt">The path the file was found at.</param>
    /// <returns>The file contents.</returns>
    public JsonnetContents Import(string importedFrom, string importedPath, out string foundAt)
    {
        if (importedFrom == null)
        {
            throw new ArgumentNullException(nameof(importedFrom));
        }
        if (importedPath == null)
        {
            throw new ArgumentNullException(nameof(importedPath));
        }
        // TODO(sbarzowski) Make sure that dir is absolute and resolving of ""
        // is independent from current CWD. The default path should be saved
        // in the importer.
        string dir = GoFilePath.Split(importedFrom).Dir;
        bool found = TryPath(dir, importedPath, out JsonnetContents? content, out string foundHere);

        for (int k = JPaths.Count - 1; !found && k >= 0; k--)
        {
            // A null entry acts as "", Go's zero value for a []string element.
            found = TryPath(JPaths[k] ?? "", importedPath, out content, out foundHere);
        }

        if (!found)
        {
            throw new JsonnetException("couldn't open import " + Yaml.GoFormat.Quote(importedPath) + ": no match locally or in the Jsonnet library paths");
        }
        foundAt = foundHere;
        return content!;
    }
}

/// <summary>
/// "Imports" data from an in-memory map (go-jsonnet's <c>MemoryImporter</c>). All paths are treated as absolute keys.
/// </summary>
public sealed class MemoryImporter : IJsonnetImporter
{
    private IDictionary<string, JsonnetContents> _data = new Dictionary<string, JsonnetContents>();

    /// <summary>
    /// Gets or sets the map from import path to contents. Replacing an entry that a VM already imported
    /// breaks the same-instance contract of <see cref="IJsonnetImporter"/>; reassign
    /// <see cref="JsonnetVm.Importer"/> afterwards to clear the VM's cache.
    /// </summary>
    public IDictionary<string, JsonnetContents> Data
    {
        get => _data;
        set => _data = value ?? throw new ArgumentNullException(nameof(value));
    }

    /// <summary>Fetches data from a map entry.</summary>
    /// <param name="importedFrom">Ignored, but must not be <see langword="null"/>.</param>
    /// <param name="importedPath">The key to look up.</param>
    /// <param name="foundAt">Set to <paramref name="importedPath"/>.</param>
    /// <returns>The contents stored under the key.</returns>
    public JsonnetContents Import(string importedFrom, string importedPath, out string foundAt)
    {
        if (importedFrom == null)
        {
            throw new ArgumentNullException(nameof(importedFrom));
        }
        if (importedPath == null)
        {
            throw new ArgumentNullException(nameof(importedPath));
        }
        if (Data.TryGetValue(importedPath, out JsonnetContents? content))
        {
            foundAt = importedPath;
            return content;
        }
        throw new JsonnetException("import not available " + importedPath);
    }
}
