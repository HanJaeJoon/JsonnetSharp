// Ports go-jsonnet vm.go.

using JsonnetSharp.Ast;

namespace JsonnetSharp;

// extKind indicates the kind of external variable that is being initialized for the VM
internal enum ExtKind
{
    Var, // a simple string
    Code, // a code snippet represented as a string
}

// External variable or top level argument provided before execution
internal readonly struct VmExt
{
    // jsonnet code to evaluate (kind=Code) or string to pass (kind=Var)
    public readonly string Value;
    // the kind of external variable that is specified.
    public readonly ExtKind Kind;

    public VmExt(string value, ExtKind kind)
    {
        Value = value;
        Kind = kind;
    }
}

/// <summary>
/// The Jsonnet interpreter: parses and evaluates Jsonnet (go-jsonnet's <c>VM</c>).
/// </summary>
/// <remarks>
/// <para>A VM is not thread-safe; use one per thread.</para>
/// <para>go-jsonnet grows its stack up to 1 GB, so each evaluation (and <see cref="FindDependencies"/>)
/// runs on a thread with a big stack (see <see cref="EvaluationStackSize"/>) while the calling thread waits.
/// Importers, native functions and the trace writer are called on that thread, so one that waits for the
/// calling thread (a lock it holds, a UI thread) deadlocks unless <see cref="EvaluationStackSize"/> is 0.
/// Where threads cannot be started (browser WebAssembly), evaluation runs on the calling thread instead.
/// If the waiting thread is interrupted (or aborted on .NET Framework), the evaluation is stopped at its
/// next function call (or inside <c>std.range</c> and <c>std.makeArray</c>) and the exception propagates once
/// it has stopped; other builtins finish their current call first.</para>
/// <para>Jsonnet call depth is limited by <see cref="MaxStack"/>. A program that needs more native stack
/// than the evaluation thread has (only possible with a much larger <see cref="MaxStack"/> or extremely
/// deep source nesting) fails with the same "max stack frames exceeded." or
/// "Exceeded maximum parse depth limit." error instead of crashing the process.</para>
/// </remarks>
public sealed class JsonnetVm
{
    private enum EvalKind
    {
        Regular,
        Multi,
        Stream,
    }

    private Dictionary<string, VmExt> _ext = new();
    private Dictionary<string, VmExt> _tla = new();
    private readonly Dictionary<string, JsonnetNativeFunction> _nativeFuncs = new();
    private IJsonnetImporter _importer = new FileImporter();
    private readonly TermErrorFormatter _errorFormatter = new(20, false);
    private ImportCache _importCache;
    private TextWriter _traceOut = Console.Error;

    /// <summary>Creates a VM with default parameters (go-jsonnet's <c>MakeVM</c>).</summary>
    public JsonnetVm()
    {
        _importCache = new ImportCache(_importer);
    }

    /// <summary>Gets the version of go-jsonnet this library is a port of.</summary>
    public static string Version => "v0.22.0";

    /// <summary>Gets or sets the maximum number of Jsonnet stack frames. Default 500.</summary>
    public int MaxStack { get; set; } = 500;

    /// <summary>Gets or sets the maximum number of stack trace lines in error messages before cropping (0 = no limit). Default 20.</summary>
    public int MaxTrace
    {
        get => _errorFormatter.MaxStackTraceSize;
        set => _errorFormatter.MaxStackTraceSize = value;
    }

    /// <summary>Gets or sets whether the program must evaluate to a string, which is output directly instead of as JSON.</summary>
    public bool StringOutput { get; set; }

    /// <summary>Gets or sets whether a trailing newline is added to the output. Default true.</summary>
    public bool OutputNewline { get; set; } = true;

    /// <summary>
    /// Gets or sets the importer used for <c>import</c>, <c>importstr</c>, <c>importbin</c> and
    /// <see cref="EvaluateFile"/>. Default is a <see cref="FileImporter"/>. Setting it clears all caches.
    /// It is called on the evaluation thread (see <see cref="EvaluationStackSize"/>).
    /// </summary>
    public IJsonnetImporter Importer
    {
        get => _importer;
        set
        {
            _importer = value ?? throw new ArgumentNullException(nameof(value));
            FlushCache();
        }
    }

    // Fully flush cache. This should be executed when we are no longer sure that the source files
    // didn't change, for example when the importer changed.
    private void FlushCache() => _importCache = new ImportCache(_importer);

    // Flush value cache. This should be executed when calculated values may no longer be up to date,
    // for example due to change in extVars.
    private void FlushValueCache() => _importCache.FlushValueCache();

    /// <summary>
    /// Sets the output stream for <c>std.trace()</c>. Default is <see cref="Console.Error"/>. It is written on the
    /// evaluation thread (see <see cref="EvaluationStackSize"/>).
    /// </summary>
    /// <param name="traceOut">The writer.</param>
    public void SetTraceOut(TextWriter traceOut) => _traceOut = traceOut ?? throw new ArgumentNullException(nameof(traceOut));

    /// <summary>Binds a Jsonnet external variable to the given string value.</summary>
    /// <param name="key">The variable name.</param>
    /// <param name="val">The value.</param>
    public void ExtVar(string key, string val)
    {
        _ext[key] = new VmExt(NotNull(val, nameof(val)), ExtKind.Var);
        FlushValueCache();
    }

    /// <summary>Binds a Jsonnet external variable to the given Jsonnet code.</summary>
    /// <param name="key">The variable name.</param>
    /// <param name="val">The code.</param>
    public void ExtCode(string key, string val)
    {
        _ext[key] = new VmExt(NotNull(val, nameof(val)), ExtKind.Code);
        FlushValueCache();
    }

    /// <summary>Removes all external variables.</summary>
    public void ExtReset()
    {
        _ext = new Dictionary<string, VmExt>();
        FlushValueCache();
    }

    /// <summary>Binds a top-level argument to the given string value.</summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="val">The value.</param>
    public void TlaVar(string key, string val) =>
        // Setting a TLA does not require flushing the cache.
        // Only the results of evaluation of imported files are cached
        // and the TLAs do not affect these unlike extVars.
        _tla[key] = new VmExt(NotNull(val, nameof(val)), ExtKind.Var);

    /// <summary>Binds a top-level argument to the given Jsonnet code.</summary>
    /// <param name="key">The parameter name.</param>
    /// <param name="val">The code.</param>
    public void TlaCode(string key, string val) => _tla[key] = new VmExt(NotNull(val, nameof(val)), ExtKind.Code);

    /// <summary>Removes all top-level arguments.</summary>
    public void TlaReset() => _tla = new Dictionary<string, VmExt>();

    /// <summary>
    /// Registers a native function, callable from Jsonnet as <c>std.native(name)</c>. It is called on the
    /// evaluation thread (see <see cref="EvaluationStackSize"/>).
    /// </summary>
    /// <param name="f">The function.</param>
    public void NativeFunction(JsonnetNativeFunction f)
    {
        if (f == null)
        {
            throw new ArgumentNullException(nameof(f));
        }
        _nativeFuncs[f.Name] = f;
        FlushValueCache();
    }

    // Go strings cannot be nil.
    private static string NotNull(string? s, string paramName) => s ?? throw new ArgumentNullException(paramName);

    // Go grows goroutine stacks up to 1 GB; a big stack keeps the reachable depth independent of the caller's
    // thread (1 MB main thread, 256 KB IIS workers, .NET Framework's bigger frames). A 32-bit process has
    // little address space to spare, and .NET Framework commits a thread's whole stack (32 MB there still parses
    // 10000 top-level locals, which 16 MB does not, and matches go-jsonnet for 50000-deep values).
    private static readonly int DefaultEvaluationStackSize =
        IntPtr.Size == 4 ? 16 * 1024 * 1024 :
        System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription.StartsWith(".NET Framework", StringComparison.Ordinal) ? 32 * 1024 * 1024 :
        256 * 1024 * 1024;

    /// <summary>
    /// The smallest nonzero <see cref="EvaluationStackSize"/>, 1 MB (the default stack of a .NET main thread).
    /// </summary>
    /// <remarks>
    /// On a much smaller stack, .NET's stack guard fails at once (so even <c>1</c> reports
    /// "Exceeded maximum parse depth limit.") or the first evaluation overflows it and the process dies.
    /// </remarks>
    public const int MinEvaluationStackSize = 1024 * 1024;

    private int _evaluationStackSize = DefaultEvaluationStackSize;

    /// <summary>
    /// Gets or sets the stack size, in bytes, of the thread each evaluation (and <see cref="FindDependencies"/>)
    /// runs on while the calling thread waits; 0 evaluates on the calling thread. Default 256 MB, 32 MB on
    /// .NET Framework, 16 MB in a 32-bit process.
    /// </summary>
    /// <remarks>
    /// <para>go-jsonnet grows its stack up to 1 GB, so a big stack lets deep programs behave as in go-jsonnet
    /// whatever the caller's thread. On .NET the stack is reserved and used only as deep as the program goes;
    /// .NET Framework commits all of it, so each concurrent evaluation costs that much commit charge there.
    /// Threads are kept for a second after an evaluation for reuse. In a 32-bit process, evaluations beyond
    /// 512 MB of evaluation thread stacks run on the calling thread, so they cannot use up the address space.</para>
    /// <para>With 0, the depth a program can reach depends on the calling thread's stack, which should be at
    /// least <see cref="MinEvaluationStackSize"/> (a program that needs more fails with "max stack frames
    /// exceeded."), but callbacks run on the calling thread: use it when an importer, native function or trace
    /// writer needs the caller's thread (a lock the caller holds, a UI thread, thread-static state), which
    /// otherwise deadlocks or is missing.</para>
    /// </remarks>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative, or nonzero and less than
    /// <see cref="MinEvaluationStackSize"/>.</exception>
    public int EvaluationStackSize
    {
        get => _evaluationStackSize;
        set => _evaluationStackSize = value == 0 || value >= MinEvaluationStackSize ? value :
            throw new ArgumentOutOfRangeException(nameof(value), value, "Use 0 or at least " + MinEvaluationStackSize + " bytes.");
    }

    private T OnEvaluationThread<T>(Func<CancelFlag, T> body) =>
        _evaluationStackSize == 0 ? body(CancelFlag.Never) : EvaluationThread.Run(_evaluationStackSize, body);

    private void BuildConfiguredInterpreter(CancelFlag cancel, out Interpreter i)
    {
        Interpreter.BuildInterpreter(_ext, _nativeFuncs, MaxStack, _importCache, _traceOut, out i);
        i.Cancel = cancel;
    }

    // Go returns the error; Format turns it into the text the returned error prints.
    private JsonnetException MakeException(Exception err, Interpreter? i)
    {
        // The interpreter state is not unwound on error, so this is the stack where it was thrown.
        if (err is InsufficientExecutionStackException && i != null)
        {
            err = new RuntimeError("max stack frames exceeded.", i.GetCurrentStackTrace());
        }
        else if (err is StringTooLongException && i != null)
        {
            err = new RuntimeError(err.Message, i.GetCurrentStackTrace());
        }
        switch (err)
        {
            case RuntimeError r:
                return new JsonnetRuntimeException(_errorFormatter.Format(err), r.Cause);
            case StaticError:
                return new JsonnetStaticException(_errorFormatter.Format(err));
            case ImportError:
                return new JsonnetException(_errorFormatter.Format(err), err.InnerException);
            default:
                // Go recovers panics as "(CRASH) <panic>\n<stack>"; .NET's trace uses Environment.NewLine.
                return new JsonnetException(TermErrorFormatter.FormatInternal("(CRASH) " + err.Message + "\n" +
                    err.StackTrace?.Replace("\r\n", "\n") + "\n"), err);
        }
    }

    private object EvaluateNode(Node node, EvalKind kind, CancelFlag cancel)
    {
        Interpreter? i = null;
        try
        {
            BuildConfiguredInterpreter(cancel, out i);
            return kind switch
            {
                EvalKind.Regular => Interpreter.EvaluateTop(i, node, _tla, StringOutput, OutputNewline),
                EvalKind.Multi => Interpreter.EvaluateMultiTop(i, node, _tla, StringOutput, OutputNewline),
                _ => Interpreter.EvaluateStreamTop(i, node, _tla),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw MakeException(e, i);
        }
    }

    private object EvaluateSnippet(string diagnosticFileName, string filename, string snippet, EvalKind kind) => OnEvaluationThread(cancel =>
    {
        Node node;
        try
        {
            node = Program.SnippetToAST(diagnosticFileName, filename, snippet);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw MakeException(e, null);
        }
        return EvaluateNode(node, kind, cancel);
    });

    private object EvaluateMainFile(string filename, EvalKind kind) =>
        OnEvaluationThread(cancel => EvaluateNode(ImportMainFile(filename), kind, cancel));

    private Node ImportMainFile(string filename)
    {
        try
        {
            return _importCache.ImportAST("", filename, out _);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw MakeException(e, null);
        }
    }

    /// <summary>
    /// Evaluates a string containing Jsonnet code and returns the JSON output.
    /// Imports are resolved as if from an anonymous location ("").
    /// </summary>
    /// <param name="filename">The name used in error messages, e.g. "&lt;cmdline&gt;".</param>
    /// <param name="snippet">The Jsonnet code.</param>
    /// <returns>The JSON text (or the string when <see cref="StringOutput"/> is set).</returns>
    /// <exception cref="JsonnetException">The program failed; the message is the formatted error.</exception>
    public string EvaluateAnonymousSnippet(string filename, string snippet) =>
        (string)EvaluateSnippet(NotNull(filename, nameof(filename)), "", NotNull(snippet, nameof(snippet)), EvalKind.Regular);

    /// <summary>
    /// Evaluates a string containing Jsonnet code to an array and returns the JSON text of each element
    /// (the <c>--yaml-stream</c> mode).
    /// </summary>
    /// <param name="filename">The name used in error messages.</param>
    /// <param name="snippet">The Jsonnet code.</param>
    /// <returns>One JSON document per array element, each with a trailing newline.</returns>
    /// <exception cref="JsonnetException">The program failed; the message is the formatted error.</exception>
    public IReadOnlyList<string> EvaluateAnonymousSnippetStream(string filename, string snippet) =>
        (List<string>)EvaluateSnippet(NotNull(filename, nameof(filename)), "", NotNull(snippet, nameof(snippet)), EvalKind.Stream);

    /// <summary>
    /// Evaluates a string containing Jsonnet code to an object and returns the JSON text of each field
    /// (the <c>--multi</c> mode).
    /// </summary>
    /// <param name="filename">The name used in error messages.</param>
    /// <param name="snippet">The Jsonnet code.</param>
    /// <returns>A map from field name to JSON text, in sorted key order.</returns>
    /// <exception cref="JsonnetException">The program failed; the message is the formatted error.</exception>
    public IReadOnlyDictionary<string, string> EvaluateAnonymousSnippetMulti(string filename, string snippet) =>
        (Dictionary<string, string>)EvaluateSnippet(NotNull(filename, nameof(filename)), "", NotNull(snippet, nameof(snippet)), EvalKind.Multi);

    /// <summary>Evaluates the Jsonnet file fetched through <see cref="Importer"/> and returns the JSON output.</summary>
    /// <param name="filename">The file to evaluate.</param>
    /// <returns>The JSON text (or the string when <see cref="StringOutput"/> is set).</returns>
    /// <exception cref="JsonnetException">The program failed; the message is the formatted error.</exception>
    public string EvaluateFile(string filename) => (string)EvaluateMainFile(NotNull(filename, nameof(filename)), EvalKind.Regular);

    /// <summary>Evaluates a Jsonnet file to an array and returns the JSON text of each element.</summary>
    /// <param name="filename">The file to evaluate.</param>
    /// <returns>One JSON document per array element, each with a trailing newline.</returns>
    /// <exception cref="JsonnetException">The program failed; the message is the formatted error.</exception>
    public IReadOnlyList<string> EvaluateFileStream(string filename) => (List<string>)EvaluateMainFile(NotNull(filename, nameof(filename)), EvalKind.Stream);

    /// <summary>Evaluates a Jsonnet file to an object and returns the JSON text of each field.</summary>
    /// <param name="filename">The file to evaluate.</param>
    /// <returns>A map from field name to JSON text, in sorted key order.</returns>
    /// <exception cref="JsonnetException">The program failed; the message is the formatted error.</exception>
    public IReadOnlyDictionary<string, string> EvaluateFileMulti(string filename) =>
        (Dictionary<string, string>)EvaluateMainFile(NotNull(filename, nameof(filename)), EvalKind.Multi);

    // getAbsPath. shortcut: canonical paths are cleaned but symlinks are not resolved (Go uses EvalSymlinks).
    private static string GetAbsPath(string path, bool canonicalPaths)
    {
        string absPath = GoFilePath.IsAbs(path) ? path : Directory.GetCurrentDirectory() + Path.DirectorySeparatorChar + path;
        return canonicalPaths ? GoFilePath.Clean(absPath) : absPath;
    }

    private string CleanedPath(string foundAt, bool canonicalPaths) =>
        _importer is FileImporter ? GetAbsPath(foundAt, canonicalPaths) : foundAt;

    private void FindDependenciesRec(string filePath, Node node, HashSet<string> dependencies, List<TraceFrame> stackTrace, bool canonicalPaths)
    {
        switch (node)
        {
            case Import i:
            {
                Node imported;
                string foundAt;
                try
                {
                    imported = _importCache.ImportAST(filePath, i.File.Value, out foundAt);
                }
                catch (Exception e) when (e is ImportError || e is StaticError)
                {
                    stackTrace.Insert(0, new TraceFrame("", i.LocRange));
                    throw;
                }
                string cleanedAbsPath = CleanedPath(foundAt, canonicalPaths);
                // Check that we haven't already parsed the imported file.
                if (!dependencies.Add(cleanedAbsPath))
                {
                    return;
                }
                try
                {
                    FindDependenciesRec(foundAt, imported, dependencies, stackTrace, canonicalPaths);
                }
                catch (Exception e) when (e is ImportError || e is StaticError)
                {
                    stackTrace.Insert(0, new TraceFrame("", i.LocRange));
                    throw;
                }
                break;
            }
            case ImportStr i:
                dependencies.Add(CleanedPath(ResolveImportForDeps(filePath, i.File.Value, i.LocRange, stackTrace), canonicalPaths));
                break;
            case ImportBin i:
                dependencies.Add(CleanedPath(ResolveImportForDeps(filePath, i.File.Value, i.LocRange, stackTrace), canonicalPaths));
                break;
            default:
                foreach (Node child in Parser.Children(node))
                {
                    FindDependenciesRec(filePath, child, dependencies, stackTrace, canonicalPaths);
                }
                break;
        }
    }

    private string ResolveImportForDeps(string filePath, string importedPath, LocationRange loc, List<TraceFrame> stackTrace)
    {
        try
        {
            _importCache.ImportData(filePath, importedPath, out string foundAt);
            return foundAt;
        }
        catch (ImportError)
        {
            stackTrace.Insert(0, new TraceFrame("", loc));
            throw;
        }
    }

    /// <summary>
    /// Returns a sorted list of the unique transitive dependencies (via import, importstr and importbin)
    /// of <paramref name="importedPaths"/>, which are themselves excluded. The paths are resolved as if
    /// imported from a Jsonnet file located at <paramref name="importedFrom"/>.
    /// </summary>
    /// <param name="importedFrom">The file the paths are imported from ("" for an anonymous location).</param>
    /// <param name="importedPaths">The files whose dependencies to find.</param>
    /// <param name="canonicalPaths">With a <see cref="FileImporter"/>, whether to return cleaned absolute paths. Default true.</param>
    /// <returns>The dependencies in sorted order.</returns>
    /// <exception cref="JsonnetException">An import failed.</exception>
    public IReadOnlyList<string> FindDependencies(string importedFrom, IReadOnlyList<string> importedPaths, bool canonicalPaths = true)
    {
        NotNull(importedFrom, nameof(importedFrom));
        if (importedPaths == null || importedPaths.Contains(null!))
        {
            throw new ArgumentNullException(nameof(importedPaths));
        }
        return OnEvaluationThread(_ => FindDependenciesTop(importedFrom, importedPaths, canonicalPaths));
    }

    private IReadOnlyList<string> FindDependenciesTop(string importedFrom, IReadOnlyList<string> importedPaths, bool canonicalPaths)
    {
        List<Node> nodes = new();
        List<TraceFrame> stackTrace = new();
        string[] filePaths = new string[importedPaths.Count];
        string[] depsToExclude = new string[importedPaths.Count];
        HashSet<string> deps = new();

        for (int k = 0; k < importedPaths.Count; k++)
        {
            Node node;
            string foundAt;
            try
            {
                node = _importCache.ImportAST(importedFrom, importedPaths[k], out foundAt);
            }
            catch (Exception e) when (e is ImportError || e is StaticError)
            {
                throw new JsonnetException(e.Message, e.InnerException);
            }
            string cleanedAbsPath = CleanedPath(foundAt, canonicalPaths);
            nodes.Add(node);
            filePaths[k] = foundAt;

            // Add `importedPaths` to the dependencies so that they are not parsed again.
            // Will be removed before returning.
            deps.Add(cleanedAbsPath);
            depsToExclude[k] = cleanedAbsPath;
        }

        for (int k = 0; k < filePaths.Length; k++)
        {
            try
            {
                FindDependenciesRec(filePaths[k], nodes[k], deps, stackTrace, canonicalPaths);
            }
            catch (Exception e) when (e is ImportError || e is StaticError)
            {
                RuntimeError err = new(e.Message, stackTrace);
                throw new JsonnetRuntimeException(_errorFormatter.Format(err), e.InnerException);
            }
        }

        // Exclude `importedPaths` from the dependencies.
        foreach (string dep in depsToExclude)
        {
            deps.Remove(dep);
        }

        List<string> dependencies = new(deps);
        dependencies.Sort(Util.GoStringComparer);
        return dependencies;
    }

    /// <summary>
    /// Finds the actual path where the imported file can be found, as if imported from a Jsonnet file located
    /// at <paramref name="importedFrom"/>. The contents are cached, so later evaluation sees the same data.
    /// </summary>
    /// <param name="importedFrom">The importing file ("" for an anonymous location).</param>
    /// <param name="importedPath">The path as written in the import expression.</param>
    /// <returns>The location the importer found the file at.</returns>
    /// <exception cref="JsonnetException">The importer failed; the message is its error text.</exception>
    public string ResolveImport(string importedFrom, string importedPath)
    {
        ImportData(importedFrom, importedPath, out string foundAt);
        return foundAt;
    }

    /// <summary>
    /// Fetches the data just as if it was imported from a Jsonnet file located at <paramref name="importedFrom"/>.
    /// It shares the cache with evaluation.
    /// </summary>
    /// <param name="importedFrom">The importing file ("" for an anonymous location).</param>
    /// <param name="importedPath">The path as written in the import expression.</param>
    /// <param name="foundAt">The location the importer found the file at.</param>
    /// <returns>The file contents: <see cref="JsonnetContents.Data"/> for the raw bytes (as go's string carries them),
    /// <see cref="JsonnetContents.ToString"/> for the text.</returns>
    /// <exception cref="JsonnetException">The importer failed; the message is its error text.</exception>
    public JsonnetContents ImportData(string importedFrom, string importedPath, out string foundAt)
    {
        NotNull(importedFrom, nameof(importedFrom));
        NotNull(importedPath, nameof(importedPath));
        try
        {
            return _importCache.ImportData(importedFrom, importedPath, out foundAt);
        }
        catch (ImportError e)
        {
            throw new JsonnetException(e.Message, e.InnerException);
        }
    }
}
