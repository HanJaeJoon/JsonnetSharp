# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [0.1.0] - 2026-11-06

### Added

- Initial C# port of [google/go-jsonnet](https://github.com/google/go-jsonnet) v0.22.0 (lexer, parser, desugarer,
  static analysis, interpreter, all builtins, and the cpp-jsonnet `std.jsonnet` that go-jsonnet embeds), plus the
  YAML parser of `sigs.k8s.io/yaml` v1.4.0 (goyaml.v2) for `std.parseYaml`. Error messages and stack traces
  are go-jsonnet's text.
- `JsonnetVm`: external variables and code (`ExtVar`, `ExtCode`), top-level arguments (`TlaVar`, `TlaCode`),
  native functions (`NativeFunction`, `JsonnetNativeFunction`), `EvaluateAnonymousSnippet` and `EvaluateFile` in
  regular, `*Multi` and `*Stream` modes, `MaxStack`, `MaxTrace`, `StringOutput`, `OutputNewline`,
  `SetTraceOut`, `FindDependencies`, `ResolveImport` and `ImportData` (returns the raw `JsonnetContents`).
- Importers: `IJsonnetImporter`, `FileImporter` (with `JPaths`; safe to share across threads) and
  `MemoryImporter`.
- Exceptions: `JsonnetException`, `JsonnetRuntimeException`, `JsonnetStaticException`; an exception thrown by a
  native function or importer is kept as `InnerException`. `null` arguments throw `ArgumentNullException`.
- Evaluation runs on a reused thread with a big stack (`JsonnetVm.EvaluationStackSize`: 256 MB, 32 MB on
  .NET Framework, 16 MB in a 32-bit process; 0 evaluates on the calling thread, other values must be at least
  `JsonnetVm.MinEvaluationStackSize`, 1 MB), so deep programs behave as in go-jsonnet whatever the caller's
  thread stack is. Interrupting (or aborting) the waiting thread stops the evaluation.
- Deviations from go-jsonnet: source nested beyond what the evaluation stack allows fails with the static error
  "Exceeded maximum parse depth limit." (go-jsonnet has no such limit and crashes at about 100,000 levels);
  strings longer than .NET allows fail with "string is too long for .NET"; "INTERNAL ERROR" messages link this
  project's issue tracker. `ExtNode`/`TlaNode`, the command line program, the formatter and the linter are not
  ported.

### Fixed

Found in dogfooding round 1 ([docs/dogfooding/round1.md](docs/dogfooding/round1.md)):

- Objects folded from thousands of layers, long `+` chains, many top-level locals, deep `==` and recursion within
  `MaxStack` failed on the caller's thread (1 MB, 256 KB workers, .NET Framework) with a misleading
  "max stack frames exceeded." or "Exceeded maximum parse depth limit."; they now evaluate as in go-jsonnet.
- `null` arguments were accepted and failed later as "INTERNAL ERROR: (CRASH)" or `NullReferenceException`.
- A native function's exception lost its `InnerException`, and its `OutOfMemoryException` became a Jsonnet error.
- `+Inf`, `-Inf` and `NaN` (from `std.sum`/`std.avg` overflow) print as in go-jsonnet instead of 309-digit numbers.
- On .NET Framework, number literals, `std.parseJson` and `std.parseYaml` parse decimals correctly rounded,
  and `std.manifestJson*`/`std.manifestYamlDoc` and YAML float keys use go-jsonnet's shortest digits.
- A YAML float key beyond float32's range becomes `.inf`/`-.inf`.
- An importer returning `null` contents or `foundAt` gives a runtime error naming the contract.
- `FileImporter` is safe to share across threads; a path containing NUL fails with go's "invalid argument" error.
- `std.trace` ignores trace writer I/O errors, like go-jsonnet.
- A string longer than .NET allows is a runtime error instead of an `OutOfMemoryException`.
- The netstandard2.0 assembly no longer depends on System.Memory.
- Small evaluations no longer start a thread each (about 5x faster on .NET, 40x on .NET Framework).
- Interrupting (or aborting) the waiting thread stops the evaluation instead of leaving it running on the VM.
- Comparing a cyclic or extremely deep value (`local o = {a: self}; o == o`) fails within a second with
  "max stack frames exceeded." instead of after tens of seconds of process-wide GC pauses.
- Parsing deeply nested source no longer allocates memory quadratic in the depth.
- `std.splitLimitR` is linear instead of quadratic.
- `std.strReplace`, `std.escapeStringJson`, `std.toString`, manifestation and the output report a string longer
  than .NET allows as a runtime error instead of an `OutOfMemoryException` or an internal error.
- A lone UTF-16 surrogate from the host (external variables, top-level arguments, native results) becomes
  U+FFFD on every path, including string output and multi-mode file names.
- `std.equalsIgnoreCase` and the Go-quoted names in error messages use Unicode 15.0 on every runtime, as
  go-jsonnet does (.NET Framework missed newer case pairs; .NET 10 printed Unicode 16 characters unescaped).
- `std.parseYaml`: a `.nan` key in an error message no longer crashes; mixed-type and non-ASCII keys in error
  messages are ordered as in go-jsonnet; `!!binary` values keep invalid UTF-8 bytes until JSON conversion
  (`\xff` in error messages, the right winner among colliding keys); a repeated `-0.0`/`0.0` key keeps the
  last spelling.
- `JsonnetNativeFunction` rejects null or duplicate parameter names with `ArgumentException`.
- An importer's `OutOfMemoryException` propagates; a null element of `FindDependencies`'s paths throws
  `ArgumentNullException`; a null `FileImporter.JPaths` entry acts as "".
- The license and package copyright name go-jsonnet's authors (Google Inc.).
- An `EvaluationStackSize` below 1 MB throws `ArgumentOutOfRangeException` instead of killing the process or
  failing every program with "Exceeded maximum parse depth limit.".
- In a 32-bit process, evaluations past 512 MB of evaluation thread stacks run on the calling thread instead of
  using up the address space until the process dies.
- On .NET Framework the default evaluation stack is 32 MB instead of 64 MB, which it commits per concurrent
  evaluation; the README no longer suggests `disableCommitThreadStack`, which does not change that.
- A string of 2^31 or more code points (`+`, `std.join`, `std.repeat`) is the "string is too long for .NET"
  runtime error instead of an internal error.
- Manifesting a field whose Go-quoted name would exceed .NET's string limit no longer throws
  `OutOfMemoryException`: the "Field ..." trace text is built only for an error.
- `FileImporter` reports unreadable or locked files as go-jsonnet does ("open <path>: Access is denied."),
  in English whatever the UI culture.
- Error columns after invalid UTF-8 bytes in an imported file match go-jsonnet (one column per byte).
- A trace writer that throws any exception is ignored, like go-jsonnet ignores write errors.
- A cyclic native function result fails within a second with "max stack frames exceeded.".
- Internal error messages use LF line breaks only, also on Windows.
- `std.parseYaml` rejects a document whose JSON form nests deeper than 10000 levels with go-jsonnet's
  "error unmarshaling JSON: while decoding JSON: invalid character '[' exceeded max depth".
- `std.md5`, `std.sha1`, `std.sha256`, `std.sha512` and `std.sha3` hash strings whose UTF-8 form exceeds 2 GB, as
  go-jsonnet does; `std.parseYaml` and `std.encodeUTF8` on such a string give the "string is too
  long for .NET" runtime error instead of an internal error.
- On .NET Framework, `FileImporter` reads files whose path is 248 characters or longer (go's `\\?\` prefix).
- An exception thrown while reading a native function's result (a custom `IList`/`IDictionary`) is a runtime
  error with the exception as `InnerException`, not an internal error.
- `MemoryImporter.Import` throws `ArgumentNullException` for a null argument, like `FileImporter.Import`.
- Interrupting the waiting thread also stops a long `std.range` or `std.makeArray` (and so `std.repeat`).

[Unreleased]: https://github.com/HanJaeJoon/JsonnetSharp/compare/v0.1.0...HEAD
[0.1.0]: https://github.com/HanJaeJoon/JsonnetSharp/releases/tag/v0.1.0
