# JsonnetSharp round 1 dogfooding findings

Target: `JsonnetSharp 0.1.0` from the working tree (`src/JsonnetSharp`, no commits yet), consumed as copies of the
Release DLLs: the net8.0 DLL from net8.0 console apps in `%TEMP%\dogfood-JsonnetSharp` (`iso/`, `perf/`) and the
finder harnesses, and the netstandard2.0 DLL from .NET Framework 4.8 consoles (x64 and x86).
The scenario app was written from the README and the generated XML docs only.
Reference implementation: go-jsonnet 0.22.0 `jsonnet.exe` for Windows. Expected behavior is exactly what it prints;
for errors, `JsonnetException.Message` is compared with go's `err.Error()` from the VM API (the CLI's stderr
without `Fprintln`'s trailing newline), plus `std.trace` output and the exception type.
Besides the scenario app, six differential finders (semantics, builtins, numbers and culture, YAML and manifest,
API boundary, robustness and performance) each ran three passes; the bugs found in a pass were fixed before the next
one, and every pass replayed the earlier corpora against the rebuilt DLLs.

## Scenarios

- [x] 1. jsonnet-like CLI on the library (`-e`, `-J`, `-V`/`--ext-code`, `-A`/`--tla-code`, `-m`, `-y`, `-S`, `-s`, `-t`), 60 invocations vs `jsonnet.exe`
- [x] 2. Real-world libraries byte for byte: kube-prometheus, grafonnet-lib, kube-libsonnet, xtd, k8s-libsonnet (111 files)
- [x] 3. C# host generating manifests and a dashboard (ext vars, TLAs, 3 JPaths, 2 native functions, Multi)
- [x] 4. Error message quality: 44 typical user mistakes
- [x] 5. ASP.NET-like host: VM per request, shared importers, small worker stacks, cultures, runaway programs
- [x] 6. Differential semantics: hand corpora and type-directed fuzz (objects, functions, imports, traces, depth)
- [x] 7. Every `std` function: named and positional arguments, format, unicode, hashes, Inf/NaN, scale
- [x] 8. Numbers and culture: net8 vs net48 vs x86, 6 to 12 cultures, parse and shortest-format fuzz
- [x] 9. YAML and manifest: `std.parseYaml` edge cases, real-world YAML, every `manifest*` function
- [x] 10. API boundary: nulls, native functions, importers, VM reuse, settings, `EvaluationStackSize`, threads, FileImporter on Windows
- [x] 11. Robustness and performance: depth, strings beyond .NET limits, memory, concurrency, interrupts
- [x] 12. Package audit: nuspec, dependencies, README, CHANGELOG, XML docs, public surface by reflection

---

# Verdict: 0.1.0 can ship. 22 bugs (from 51 confirmed reports) were found and fixed in this round; the remaining gaps are intentional or not blocking.

The Jsonnet semantics hold up. Across about 9,500 differential programs in the first pass, and well over 100,000
replayed or new cases in the later passes, every remaining difference from go-jsonnet is a go fatal stack
overflow, a go crash the port replicates, go's own nondeterministic output, or one of the deliberate
deviations listed below. The real-world libraries (kube-prometheus, grafonnet) produce byte-identical output at
go's speed, and all 44 typical mistakes give go's exact error text.

The defects were all at the .NET boundary, which go-jsonnet's own test suites (2923 ported tests passing) cannot
see. The biggest class was the fixed-size native stack against go's growable one: valid programs failed with a
misleading "max stack frames exceeded." or "Exceeded maximum parse depth limit.". The others were .NET Framework
floating-point parsing and formatting, Inf/NaN formatting, null arguments, importer and native-function contracts,
string length limits, Windows file errors, and thread and memory costs. These would have been serious in a
0.1.0: wrong numbers on .NET Framework, Inf printed as 309 digits, a process kill from a small
`EvaluationStackSize`, a process kill from concurrent evaluations in a 32-bit process, and depth failures in
ordinary programs (folded objects, long `+` chains).

## Bugs found (all fixed in this round)

Regression tests: `tests/JsonnetSharp.Tests/DogfoodRound1Tests.cs` (59 tests in `DogfoodRound1Tests` and the
non-parallel `SerialDogfoodTests`). Before the first fix pass, 17 of the first 20 failed on net8.0 and 18 of 20 on
net48. Tests added in later passes use the confirmed repro inputs; not all of them were run against the code
before their fix (noted at the bug where known).

### 1. [BUG, was major] Valid programs failed for lack of native stack, with a misleading error

| program (default `MaxStack` 500) | go | before | after |
|---|---|---|---|
| `std.foldl(function(a, i) a + {['k' + i]: i}, std.range(1, 4000), {}).k1` | `1` | `max stack frames exceeded.` (main thread) | `1` (20000 layers) |
| `std.foldl(function(a, i) a + {v: super.v + 1}, std.range(1, 5000), {v: 0}).v` | 20-frame trace | header and `$` frame only | go's trace |
| self chain `o['f480']`, net48 | `480` | `max stack frames exceeded.` (net48 limit 456) | `480` |
| `local d = std.foldl(function(a, i) [a], std.range(1, 3000), []); d == d` | `true` | `max stack frames exceeded.` | `true` |
| 1000 top-level `local`s, or a 2000-term `1+1+...` | `999` / `2000` | `Exceeded maximum parse depth limit.` | same as go (10000 tested) |
| `f(450)` recursion on a 256 KB caller thread | `450` | `max stack frames exceeded.` | `450` |
| `1+1` on a 128 KB caller thread | `2` | `Exceeded maximum parse depth limit.` | `2` |
| `std.parseYaml` of 5000 nested `[` | `1` | `yaml: exceeded max depth` | `1`; 10001 gives go's `exceeded max depth of 10000` |

go grows goroutine stacks up to 1 GB; the port recursed on the caller's thread, so the reachable depth depended on
the host thread (1 MB main thread, 256 KB IIS workers, bigger frames on .NET Framework), and the error blamed
`MaxStack`, which the program had not reached. Fixed by running parse and evaluation (and `FindDependencies`) on a
dedicated thread with a big stack while the caller waits: 256 MB by default, 32 MB on .NET Framework, 16 MB in a
32-bit process (`JsonnetVm.EvaluationStackSize`; 0 evaluates on the calling thread). Threads are reused for
back-to-back evaluations. Merged reports: deep-inheritance-chain-overflow, object-layer-chain-depth,
native-overflow-trace-differs, net48-selfchain-within-maxstack, deep-equality-1mb-stack, parse-depth-stack-bound,
parse-depth-limit-rejects-valid, small-stack-below-maxstack, small-stack-false-maxstack,
small-stack-bogus-parse-error, yaml-depth-message-deviation. Tests: `FoldedObjectChainOfManyLayers`,
`SuperChainHitsMaxStackWithGoTrace`, `SelfChainWithinMaxStack`, `DeepEqualityAndComparison`, `LongProgramsParse`,
`RecursionWithinMaxStackOnSmallThread`, `TrivialProgramOnTinyThread`, `DeepYamlLikeGo`.

### 2. [BUG, was critical] `+Inf`, `-Inf` and `NaN` printed as 309-digit numbers

| expression (`local inf = std.sum([1e308, 1e308])`) | go | before | after |
|---|---|---|---|
| `inf` | `+Inf` | `179769313486231590772930519078902...137216` | `+Inf` |
| `inf + -inf` | `NaN` | `2.6965397022934739e+308` | `NaN` |
| `std.toString(inf)` | `"+Inf"` | the 309 digits | `"+Inf"` |
| `std.manifestToml({a: inf})` | `a = +Inf` | `a = 1797...` | `a = +Inf` |
| `std.substr("abc", 0, inf)` | `... got +Inf` | `... got 1797...137216.000000` | `... got +Inf` |
| `std.assertEqual(nan, nan)` | `Assertion failed. NaN != NaN` | digits | `Assertion failed. NaN != NaN` |

`std.md5(std.toString(inf))` changed with it (`680f447a...` before, go's `019bd3d1...` after). Fixed in
`Interpreter.UnparseNumber`, `GoStrconv.FormatFloatF` and `GoFormat.FormatFloatG` through one
`GoStrconv.NonFinite` helper. Test: `NonFiniteNumbersPrintLikeGo`.

### 3. [BUG, was critical] A YAML float key beyond float32 became `"I.nfinitye+07"`

`std.parseYaml('{1e39: a, -1e39: b}')` gave `{"-I.nfinitye+07": "b", "I.nfinitye+07": "a"}`; go gives
`{"-.inf": "b", ".inf": "a"}`. `GoFormat.FormatFloatG` checked for infinity before narrowing to float32. Test:
`YamlFloatKeyBeyondFloat32IsInf`.

### 4. [BUG, was critical] .NET Framework parsed some decimals to the wrong double

| input | go and net8 | net48 before |
|---|---|---|
| `1.909689623213514e-34 == 1.9096896232135139e-34` | `true` | `false` |
| `std.parseJson('2.2250738585072012e-308')` | `2.2250738585072014e-308` | `2.2250738585072009e-308` |
| `2.4703282292062327e-324` | `0` | `4.9406564584124654e-324` |
| `123456789012345678901234567890123456789e-360` | `1.2351641146031164e-322` | `1.1857575500189917e-322` |

13 of 6000 literals parsed differently on net48 (number literals, `std.parseJson`, `std.parseYaml`), 0 on net8.
.NET Framework's `double.Parse` is not correctly rounded. `GoStrconv.TryParseFloat` now does correctly rounded
conversion on non-.NET builds (Clinger fast path, `BigInteger` otherwise). A 20,000-case cross-runtime fuzz against
.NET 8 passed (not kept). Test: `DecimalsParseCorrectlyRounded`.

### 5. [BUG, was critical] .NET Framework printed non-shortest, sometimes non-round-tripping digits

| expression | go and net8 | net48 before |
|---|---|---|
| `std.manifestJsonMinified(1e15+0.3)` | `1000000000000000.2` | `1000000000000000.3` |
| `std.manifestJsonMinified(852625145403825050000...0)` | `852625145403825050000...0` | `852625145403825100000...0` (a different double) |
| `std.objectFields(std.parseYaml('{3034876.2: a, 36440.312: b}'))` | `["3.0348762e+06", "36440.312"]` | `["3.0348763e+06", "36440.313"]` |

33 of 6000 `manifestJsonMinified` outputs and 3 of 1772 float32 YAML keys differed on net48. The `#else` path of
`GoFormat.ShortestDigits` now uses exact digits with a binary search on length, checked with the exact parser of
bug 4. Test: `ShortestFormattingMatchesGo`.

### 6. [BUG, was critical] A small `EvaluationStackSize` killed the process

`new JsonnetVm { EvaluationStackSize = 1024 }.EvaluateAnonymousSnippet("x", "1")` ended the net8 process with
`0xC0000005` (sizes up to 65536); nothing was catchable. Sizes from 69632 to 131072 failed every program, even `1`,
with "Exceeded maximum parse depth limit.". Merged reports: eval-stack-size-small-process-crash,
ess-small-value-process-crash. The setter now rejects nonzero values below
`JsonnetVm.MinEvaluationStackSize` (1 MB) with `ArgumentOutOfRangeException`. Test: `EvaluationStackSizeHasAFloor`.

### 7. [BUG, was critical] Concurrent evaluations in a 32-bit process killed it

About 250 evaluations in flight reserved 16 MB of stack each until `Thread.Start` threw. By then the 2 GB address
space was gone, and the next allocation anywhere ended the process ("Unhandled exception. OutOfMemoryException." on
net48 x86; on net8 x86, a poisoned `Builtins` type initializer at n=260 and "Out of memory." at n=400). The
evaluation thread pool now caps live evaluation stacks at 512 MB in a 32-bit process; past that the evaluation runs
on the caller's thread. Test: `SerialDogfoodTests.EvaluationPastTheStackBudgetRunsOnTheCaller` (it lowers the
internal limit to 0; an actual 32-bit process was not run after the fix).

### 8. [BUG, was major] Null arguments failed late, as internal errors or raw `NullReferenceException`

| call | before | after |
|---|---|---|
| `vm.ExtVar("k", null)` (also `ExtCode`, `TlaVar`, `TlaCode`) | accepted; `std.extVar('k')` later gave `INTERNAL ERROR: (CRASH) Object reference not set ...` | `ArgumentNullException` (`val`) |
| `EvaluateAnonymousSnippet(null, "1")` | `"1\n"`; with an error in the snippet, a raw `NullReferenceException` | `ArgumentNullException` (`filename`) |
| `EvaluateAnonymousSnippet("x", null)` | `INTERNAL ERROR: (CRASH) Value cannot be null. (Parameter 's')` | `ArgumentNullException` (`snippet`) |
| `EvaluateFile(null)` | `INTERNAL ERROR: Object reference not set ...` | `ArgumentNullException` |
| `NativeFunction(null)`, `FindDependencies("", null)`, `FileImporter.Import(null, ...)` | `NullReferenceException` | `ArgumentNullException` |
| `MemoryImporter { Data = null }`, `FileImporter { JPaths = null }` | `RUNTIME ERROR: Object reference not set ...` | `ArgumentNullException` from the setter |
| `MemoryImporter.Import(null, "k", out _)` | returned the contents | `ArgumentNullException` (`importedFrom`) |

The crash texts also embedded a .NET stack trace with build-machine source paths. go strings cannot be nil, so there
is no reference behavior. Merged reports: null-args-internal-error, null-filename-nre-escapes,
null-args-not-validated, null-args-internal-crash, memoryimporter-null-importedfrom. Tests:
`NullArgumentsThrowArgumentNullException`, `MemoryImporterRejectsNullArguments`.

### 9. [BUG, was major] Strings beyond .NET's limits escaped as raw exceptions or internal errors

| program | go | before | after |
|---|---|---|---|
| a 2^30-char string through `std.md5` | the hash | raw `System.OutOfMemoryException` | `RUNTIME ERROR: string is too long for .NET (more than 1073741791 UTF-16 code units)` |
| `std.length(k + k)` with 2^31 chars | `2147483648` | `INTERNAL ERROR: (CRASH) Arithmetic operation resulted in an overflow.` | the same runtime error |
| `std.md5` of 738,197,504 CJK chars (UTF-8 over 2 GB) | the hash | `INTERNAL ERROR: (CRASH) Conversion buffer overflow.` | go's hash (streamed in chunks) |
| `std.parseYaml`/`std.encodeUTF8` of the same | the value | the same crash | `string is too long for .NET (its UTF-8 form is more than 2147483647 bytes)` |
| `std.toString({[2^28 soft hyphens]: 1})` | `268435463` | raw `OutOfMemoryException` from the "Field ..." trace label | `268435463` (label built only on error) |

Merged reports: huge-string-raw-oom, string-concat-2g-overflow-crash, utf8-over-2gb-internal-crash,
tostring-raw-oom. Tests: `StringBeyondDotNetLimitIsARuntimeError` (not run before the fix: it would allocate 4+ GB),
`ConcatBeyondIntRangeIsARuntimeError`, `FieldTraceLabelIsBuiltOnlyOnError`,
`SerialDogfoodTests.HugeUtf8StringsHashAndFailCleanly` (returns early on .NET Framework, whose 2 GB object limit
stops the input string first).

### 10. [BUG, was major] `FileImporter` reported OS errors with localized .NET text

| case | go | before | after |
|---|---|---|---|
| locked file | `open locked.libsonnet: The process cannot access the file because it is being used by another process.` | `The process cannot access the file 'C:\...\locked.libsonnet' because ...` (Korean on net48 with ko-KR UI culture) | go's text |
| access denied | `open denied.libsonnet: Access is denied.` | `Access to the path 'C:\...' is denied.` (localized on net48) | go's text |
| NUL in the path | `open a\0b: invalid argument` | `couldn't open import "a\x00b": no match ...` | go's text |

Win32 codes 5, 32 and 33 map to go's fixed English messages, whatever the UI culture. Merged reports:
fileimporter-os-error-localized, fileimporter-io-error-text, fileimporter-windows-oserror-text (NUL part). Tests:
`FileImporterReportsOsErrorsLikeGo` (Windows only, runs under ko-KR), `ImportPathWithNulIsInvalidArgument`.

### 11. [BUG, was major] .NET Framework could not import a file with a path of 260+ characters

`EvaluateFile`, an absolute import and a JPaths lookup all said `couldn't open import ...` on net48, even with
`LongPathsEnabled=1`; go and net8 read the file. `GoFilePath.FixLongPath` ports go's `os.fixLongPath` (adds the
`\\?\` prefix to absolute paths of 248+ chars). The prefix is used only for the read; the cache key, `foundAt` and
error texts keep the plain path. Test: `FileImporterReadsLongPaths` (a 461-char path; only the net48 run exercises
the bug).

### 12. [BUG, was major] Native function failures lost the exception or became internal errors

- An exception thrown by a native function became a runtime error with `InnerException == null`, and an
  `OutOfMemoryException` was swallowed as `RUNTIME ERROR: oom`. Now the exception is the `InnerException` and OOM
  propagates. Test: `NativeFunctionExceptionIsKept`.
- An exception thrown while reading the result (a custom `IList`/`IDictionary`) gave
  `INTERNAL ERROR: (CRASH) count boom` with a .NET stack trace. It is now `RUNTIME ERROR: count boom` with the
  exception as `InnerException`. Test: `NativeResultReadErrorIsARuntimeError`.
- A cyclic `Dictionary` result took 22 to 47 s to fail. It now fails in about 0.5 s with
  "max stack frames exceeded." (conversion shares the 100,000-level comparison depth cap). Test:
  `CyclicNativeResultFailsFast`.

### 13. [BUG, was major] `std.parseYaml` accepted documents nested deeper than JSON allows

`std.parseYaml("a:\n  " + std.repeat("[", 10000) + std.repeat("]", 10000))` returned a value; go rejects it with
`error unmarshaling JSON: while decoding JSON: invalid character '[' exceeded max depth`. The YAML-to-JSON pass now
tracks depth like go's decoder and reports the first container past 10,000 levels; an unsupported NaN/Inf value
still wins, as in go. Test: `YamlDeeperThanJsonAllowsIsRejected`.

### 14. [BUG] A custom importer returning `null` gave an internal error

`Import` returning `null` contents, or a `null` `foundAt`, gave `INTERNAL ERROR: (CRASH) Object reference not set ...`
or `Value cannot be null. (Parameter 'key')` with the go-jsonnet bug link. It is now a runtime error at the import
site: `IJsonnetImporter.Import returned null contents for "b"`. Merged reports:
importer-contract-violation-crash-text (case 2), importer-contract-crash. Test:
`ImporterReturningNullIsARuntimeError`.

### 15. [BUG] A `FileImporter` shared across threads corrupted its cache

A shared `FileImporter` (the natural DI singleton) failed 2 to 14 of thousands of requests with
`INTERNAL ERROR: Operations that change non-concurrent collections must have exclusive access ...` while the cache
was cold; a corrupted `Dictionary` can also hang. The cache is now locked, and the docs say a `FileImporter` may be
shared and a shared custom importer must be thread-safe. Test: `SharedFileImporterIsThreadSafe`.

### 16. [BUG] Internal errors pointed at go-jsonnet and mixed line endings

- `INTERNAL ERROR` messages said `Please report a bug here: https://github.com/google/go-jsonnet/issues`, even for
  bugs of the port or failures of the user's own code. They now link this project. Test:
  `InternalErrorsPointAtThisProject`.
- The embedded .NET stack trace used `\r\n` on Windows inside an otherwise `\n` message. Now `\n` only. Test:
  `CrashMessageUsesLineFeedsOnly`.
- A `std.trace` writer that throws (`IOException`, `NotSupportedException`, ...) gave `INTERNAL ERROR: (CRASH)`. go
  discards `Fprintf`'s error, so the port now ignores every writer exception except fatal ones. Test:
  `TraceWriterFailuresAreIgnored`.

### 17. [BUG] The parse-depth error for nested object literals had no location

The message was `" Exceeded maximum parse depth limit.\n\n"` (leading space, no file, no source line); other shapes
said `min.jsonnet:1:N-M ...`. The stack probe now skips location-less desugared nodes, so the located parent reports
the error. Test: `ParseDepthErrorHasALocation`.

### 18. [BUG] Error columns after invalid UTF-8 bytes were 2 too high per byte

For a file containing `'\xff' + error 'e'`, go reports `v3.jsonnet:1:7-16`; the port reported `1:9-18`. Imported
files are now lexed from their raw bytes as go does (one U+FFFD per invalid byte). Test:
`InvalidUtf8SourceKeepsGoColumns`.

### 19. [BUG, docs] "No runtime dependencies" was false on netstandard2.0

The nuspec declared System.Memory 4.6.3 for .NETStandard2.0, used only by two `AsSpan().SequenceEqual` calls in the
YAML parser. A net48 app with a plain DLL reference got `INTERNAL ERROR: (CRASH) Could not load file or assembly
'System.Memory'` from `std.parseYaml('a: 1')`. The calls now use LINQ, and the nuspec has no dependencies. The
`CHANGELOG.md` that `PackageReleaseNotes` points to was missing; it now exists, and the README covers importers,
native functions, errors and threads. Merged reports: readme-zero-deps-system-memory, readme-no-deps-claim,
package-readme-deps-changelog. Test: `NoRuntimeDependencies`.

### 20. [BUG] .NET Framework committed 64 MB per concurrent evaluation, and the docs suggested a fix that does nothing

.NET Framework commits a thread's whole stack: 100 evaluations in flight took 6.4 GB of commit charge (net8: 15 MB).
The `<disableCommitThreadStack>` setting named in the README had no effect. The default on .NET Framework is now
32 MB, the smallest that still parses 10,000 top-level locals; the docs state the cost and no longer suggest the
setting. Merged reports: net48-thread-stack-commit, net48-commit-per-evaluation. Test:
`DefaultEvaluationStackFitsTheRuntime`.

### 21. [BUG] Interrupting the caller waited for the whole builtin

After `Thread.Interrupt`, the caller stayed blocked until `std.makeArray(30000000, ...)` finished (5.4 s), while
memory kept growing. `std.makeArray` and `std.range` (and so `std.repeat`) now check the cancel flag every 65,536
iterations; other builtins finish their current call first, as the docs now say. Test:
`SerialDogfoodTests.InterruptStopsLongBuiltins` (both stop within 1.5 s).

### 22. [API] `JsonnetVm.ImportData` lost binary data

It returned decoded text, so bytes `FF 41 EF BB BF` came back as U+FFFD, `A`, U+FEFF. It now returns the
`JsonnetContents`, whose `Data` keeps the raw bytes, as go's string does. Test: `ImportDataKeepsRawBytes`.

### Also fixed during the round

Applied in the fix pass between the first and second finder passes; the CHANGELOG lists them and each has a test in
`DogfoodRound1Tests.cs`:

- Evaluation threads are reused, so small evaluations no longer start a thread each (`EvaluationThreadIsReused`).
- Interrupting or aborting the waiting thread stops the evaluation (`InterruptedCallerStopsTheEvaluation`).
- A cyclic or extremely deep comparison (`local o = {a: self}; o == o`) fails within a second
  (`ComparisonDepthIsCapped`).
- Parsing deeply nested source uses linear memory (`DeepNestingParsesInLinearMemory`); `std.splitLimitR` is linear
  (`SplitLimitRIsLinear`).
- `std.strReplace`, `std.escapeStringJson`, `std.toString`, manifestation and the output report an over-long string
  as a runtime error (`StringBuildersStopAtTheDotNetLimit`).
- Lone UTF-16 surrogates from the host become U+FFFD on every path (`LoneSurrogatesFromTheHostBecomeReplacementCharacters`).
- `std.equalsIgnoreCase` and Go-quoted error text use Unicode 15.0 on every runtime (`EqualsIgnoreCaseUsesUnicode15`,
  `QuotedFieldNamesUseUnicode15`, `UnicodeTablesMatchDotNet8`).
- `std.parseYaml` error texts with `.nan`, mixed-type or non-ASCII keys, `!!binary` values and repeated `-0.0` keys
  match go (`YamlNanKeyInErrorText`, `YamlMixedKeyTypesInGoOrder`, `YamlStringKeysInCodePointOrder`,
  `YamlBinaryKeepsInvalidBytesUntilJson`, `YamlSignedZeroKeyKeepsTheLastSpelling`).
- `JsonnetNativeFunction` rejects null or duplicate parameter names (`NativeFunctionParameterNamesAreValidated`);
  an importer's exception is kept as `InnerException` (`ImporterExceptionIsKept`); `FindDependencies` rejects a null
  element (`FindDependenciesRejectsNullElement`); a null `JPaths` entry acts as "" (`NullJPathsEntryActsAsEmpty`).
- `EvaluationStackSize = 0` runs callbacks on the calling thread (`CallbacksCanRunOnTheCallingThread`).
- The license and package copyright name go-jsonnet's authors (`CopyrightNamesGoJsonnetAuthors`).

## What was verified as working

**CLI (scenario 1).** A CLI over the library matches `jsonnet.exe` byte for byte (stdout, stderr, exit code, `-m`
files) in 59 of 60 invocations. The one difference is the CLI's own missing-file handling: go's CLI opens the
input file itself first.

**Real-world libraries (scenario 2).**

| workload | files | identical | go | JsonnetSharp |
|---|---|---|---|---|
| kube-prometheus `example.jsonnet` (7.4 MB JSON, also `-m` with 79 files) | 1 | yes | 2.7-3.0 s | 2.6-3.0 s |
| kube-prometheus `examples/*.jsonnet` | 41 | 41 | 91.8 s | 93.9 s |
| grafonnet-lib tests and examples | 36 | 36 | 4.0 s | 8.1 s (about 100 ms JIT startup per file) |
| kube-libsonnet tests (9 expected failures, error text compared) | 17 | 17 | 1.8 s | 3.8 s |
| kube-libsonnet examples | 4 | 4 | | |
| jsonnet-libs/xtd unit tests | 9 | 9 | 1.2 s | 2.3 s |
| k8s-libsonnet 1.34 manifests and a grafonnet-latest dashboard | 2 | 2 | | |

In process: a new VM plus a trivial evaluation takes 0.07 ms; a new VM importing `kube.libsonnet` takes 8.7 ms; a
re-evaluation on the same VM takes 0.56 ms.

**C# host (scenario 3).** `EvaluateAnonymousSnippetMulti` with ext vars, ext code, TLAs, three JPaths and two native
functions (one receiving a `Dictionary`) works. The import cache is flushed as in go when ext vars, ext code or
native functions change.

**Errors (scenario 4).** 44 of 44 typical mistakes (missing comma, unknown field, wrong arity, `self` outside an
object, missing extVar or import, index out of bounds, infinite recursion, text block indent, format mismatch,
duplicate field, missing TLA, ...) give go's exact error text.

**Host (scenario 5).** A VM per request with its own importer, or a shared `MemoryImporter`: 0 errors in 2000
parallel requests. Output is identical under de-DE, tr-TR, fa-IR and ar-SA. Infinite `tailstrict` recursion gives
a clean "max stack frames exceeded."; go kills the process on its 1 GB stack limit.

**Semantics (scenario 6).** Pass 1: about 9,500 programs (objects, functions, the `%` operator over every flag,
imports, stack traces with `MaxTrace` from 0 to 25, UTF-16 vs code-point ordering, depth near `MaxStack`, field
caching, string and stream output, and 7,800 fuzzed programs). Passes 2 and 3 replayed 23,693, then 10,223
programs, and added about 30,000 more: a visibility matrix, `super` and `$` scoping, asserts, laziness,
named-argument errors, `tailstrict` to 1,000,000 frames, slicing, mixed comparisons, CRLF, CR-only and BOM sources,
312 deep-value programs, 9,000 random function programs, 4,000 random format programs and 14,000 type-directed
fuzz programs. The only diffs left are the ones listed under "Left as-is". Imports are cached and flushed as in go
(by `ExtVar`, `ExtCode`, `NativeFunction` and `Importer`, not by `TlaVar` or `MaxStack`).

**Builtins (scenario 7).** All 153 `std` fields got at least 5 cases. There were 748 generated named-argument cases,
then replays of about 34,000 and 8,000 cases, plus about 4,300 new ones: math precision, unicode (overlong and
surrogate UTF-8, astral characters in every string builtin), format at extreme widths, every `manifest*`, errors
inside every higher-order builtin's callback, 10,000 random-value cases, 3,000 mutated-JSON cases, and 24 huge
width or index arguments. Under parallel load the library ran at 1x to 3x go's time, never quadratic; run alone it
was on par or faster (deep-indent `parseYaml` at 5000 levels: 4.9 s vs go's 13.3 s).

**Numbers and culture (scenario 8).** Output is byte-identical under invariant, de-DE, tr-TR, ar-SA, fa-IR, hi-IN,
th-TH, ja-JP, sv-SE, lt-LT and a custom culture with odd NumberFormat symbols, on net8 and net48, and with
`DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`. After bugs 4 and 5: a 10,481-double magnitude sweep through about 40
expressions each, and 557 exact binary midpoints of up to 1,099 digits, are identical to go on net8, net48, net8 x86
and the netstandard2.1 asset. tr-TR i/I, sharp s, ligatures and combining marks are compared ordinally everywhere.

**YAML and manifest (scenario 9).** 1,411 cases in pass 1 (k8s, Helm, GitHub Actions, compose, Ansible, OpenAPI,
GitLab CI, block scalars, anchors, merge keys, tags, alias bombs, depth up to 100,000), then 2,137 real-world files
(yaml-test-suite, starter workflows, awesome-compose, kubernetes/examples, Prometheus Helm charts), each run three
ways, plus 1,416 manifest cases over 146 tricky strings. Only go's own nondeterministic cases differ. Speed is on
par with go (80,000 k8s items: 5.3 s vs 4.2 s); `manifestIni`, `manifestPython` and `manifestXmlJsonml` are
superlinear in go too.

**API boundary (scenario 10).** Null arguments to every public member (37, then 38 checks), 45 native-function
return types (`int`/`long` through `JsonElement`, `DateTime`, anonymous objects), re-entrant evaluation from a
native function (200 deep), VM reuse after every error kind, `MaxStack` and `MaxTrace` at 0, -1, 1 and
`int.MaxValue` (diffed against `-s`/`-t`), `EvaluationStackSize` at 14 sizes in separate processes, culture and
`AsyncLocal` flow into callbacks, and 78 FileImporter path cases (BOM, Korean and emoji names, slash styles,
devices, reserved characters, JPaths order, long paths). Every public member has XML docs, and no go type leaks.

**Robustness and performance (scenario 11).** No process crash, hang or uncaught `StackOverflowException` in
about 230 depth cases, 36 recursion shapes at depth 3,000,000 with `MaxStack` 1e8 (net8 and net48, caller stacks
of 256 KB and 1 MB, `EvaluationStackSize` 0 and default), or 4,500 parser mutants. None of 84 perf and memory
cases was more than 5x slower than go; most were 0.5x to 2x, and much faster where go is quadratic (a string fold
over 100,000 items: go 34 s, the library 0.2 s). Memory: 2000 k8s Deployments and Services use 271 to 373 MB vs
go's 470 to 536 MB. 20,000 evaluations on one VM show no growth. 1000 concurrent evaluations and a 32-thread
shared-FileImporter stress run cleanly.

**Package (scenario 12).** Apache-2.0 license expression, packaged README, XML docs for all four TFMs, a snupkg
with four pdbs, and no dependency groups.

## Left as-is (with reasons)

1. **Directory import says `read lib: is a directory`** where go on Windows says `read lib: Incorrect function.`.
   This matches go-jsonnet's golden file `import_failure_directory.golden` (Linux text); copying Windows OS text
   would break the upstream goldens.
2. **An import path with reserved characters (`a<b>|c`) is "not found"**, where go on Windows reports the OS
   syntax error. Intentional, platform-neutral, as on Linux.
3. **Replacing a `MemoryImporter` entry after a VM imported it** gives
   `INTERNAL ERROR: (CRASH) importer problem: a different instance of Contents returned ...`. go panics with the
   same text. `MemoryImporter.Data` now documents that you must reassign `JsonnetVm.Importer` after such a change.
4. **go-style API names stay.** `ExtVar`, `ExtCode`, `TlaVar`, `TlaCode` and `NativeFunction` are named like nouns,
   the parameters are `val` and `f`, `SetTraceOut` is a method, and `Version` returns `v0.22.0` (go's `Version()`).
   They mirror go-jsonnet's `vm.go` one to one on purpose.
5. **`ExtNode`/`TlaNode` (AST values) are not ported.** Only string and code variants exist.
6. **"Exceeded maximum parse depth limit." is a port-only static error.** It is reported when parsing runs out of
   native stack. With the default evaluation stack, only extreme sources reach it (20,000 nested brackets with the
   32 MB .NET Framework default), which fail in go too ("max stack frames exceeded."), or kill go at about 100,000
   levels.
7. **With `EvaluationStackSize = 0`,** depth depends on the caller's stack again, and a caller under 1 MB can still
   hit the misleading errors of bug 1. There is no separate "stack too small" message. This is documented; the
   default avoids it.
8. **Callbacks run on the evaluation thread.** Importers, native functions and the trace writer do not see the
   caller's `[ThreadStatic]` state, and a lock the caller holds deadlocks them. Documented, with
   `EvaluationStackSize = 0` as the opt-out. Culture, UI culture and `AsyncLocal` do flow.
9. **`EvaluateFile` on a missing or unreadable file** gives `INTERNAL ERROR: couldn't open import ... Please report
   a bug here: ...`. This is the go VM API's wrapper text; the link now names this project.
10. **`std.thisFile` is `""` in `EvaluateAnonymousSnippet`.** This matches go's VM API.
11. **go's nondeterministic outputs** (colliding YAML keys such as `1e3` and `1_000`, which bad key is named in an
    error, the order of `std.mergePatch` traces) come out deterministic in the library, always as one of go's
    answers.
12. **Crash replicas** (`std.removeAt` out of range, `$objectFlatMerge`) crash as in go; only the stack trace body
    differs.
13. **`JsonnetVm` is not thread-safe** (documented; use one per thread). A shared VM across threads gave 1999
    `importer problem` crashes and 1 wrong result in 2000 requests.
14. **Strings near 2^30 code points on .NET Framework** hit the runtime's 2 GB object limit before the library's
    own checks.
15. **Each evaluation hands off to another thread.** This cost about 145 us per call before thread reuse.

Still open at the end of the round: the third finder pass reported further candidates (two `std.trace` cases, two
YAML/manifest cases, and subnormal results on net48 x86) that had not been confirmed when this report was written.
They are not covered here.

## API friction notes

1. ~~The README was one snippet.~~ It now covers installation, snippets and files, ext vars, TLAs, importers, native
   functions, multi and stream output, errors, threads and differences from go.
2. ~~Nothing said whether importers may be shared.~~ Documented (bug 15).
3. A native result of an unsupported type fails with `Not a json type: 1.5` without naming the accepted types
   (`float`, `decimal`, `uint`, `byte`, `char`, `List<int>` and `Dictionary<string, string>` are rejected). The
   XML docs of `JsonnetNativeFunction` list them; the message stays go's.
4. ~~Native exceptions lost `InnerException`.~~ Fixed (bug 12).
5. `JsonnetException` carries only the formatted text, with no structured file, line, column or frames.
6. There is no `CancellationToken` or timeout. A host can interrupt the waiting thread (`Thread.Interrupt`), which
   stops the evaluation at its next function call or inside `std.range`/`std.makeArray`.
7. `JsonnetVm.Version` is static, so the go-style `vm.Version` does not compile.
8. There is no non-anonymous `EvaluateSnippet(filename, code)`; it is deprecated in go. To evaluate in-memory main
   code with imports relative to a directory, use `EvaluateFile` with an importer.
9. The `FileImporter.JPaths` setter keeps the caller's list instance, so an array assigned to it makes a later
   `Add` throw `NotSupportedException`.

## Test results

After the last fix pass: `dotnet build -c Release` with 0 warnings and 0 errors. `dotnet test -c Release --no-build`
gave net48 2981 passed and 4 skipped (2985 total), net10.0 2981 passed and 4 skipped (2985 total), and net8.0 2981
passed and 4 skipped (2986 total). The one net8.0 failure is the timing test `SplitLimitRIsLinear`, which took 23 to
26 s against its 20 s budget under parallel load and passes in 612 ms when run alone.

## Assets

`scratchpad` below is the dogfooding session's directory,
`%TEMP%\claude\426de7dab356f6d7\2abd9be6-8afa-45e6-a317-951021ce72a9\scratchpad`.

- Scenario app: `%TEMP%\dogfood-JsonnetSharp` (`iso/`, `perf/`). CLI driver and notes:
  `scratchpad\find\dogfood` (`cli\run.sh`, `cases.txt`, `notes.md`).
- Finder work dirs: `scratchpad\find\r1-*`, `r2-*` and `r3-*` (one per dimension and pass), holding corpora,
  reference outputs and harnesses.
- One repro project per confirmed report: `scratchpad\verify\<id>`.
- Reference binary: `scratchpad\ref\jsonnet.exe` (go-jsonnet 0.22.0 for Windows).
