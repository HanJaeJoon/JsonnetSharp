## JsonnetSharp

[![NuGet](https://img.shields.io/nuget/v/JsonnetSharp)](https://www.nuget.org/packages/JsonnetSharp)
[![Build](https://github.com/HanJaeJoon/JsonnetSharp/actions/workflows/nuget-publish.yml/badge.svg)](https://github.com/HanJaeJoon/JsonnetSharp/actions/workflows/nuget-publish.yml)
[![License: Apache-2.0](https://img.shields.io/badge/License-Apache%202.0-blue.svg)](LICENSE)

JsonnetSharp is a pure C# implementation of the [Jsonnet](https://jsonnet.org) data templating language.
It is a port of [google/go-jsonnet](https://github.com/google/go-jsonnet) v0.22.0: the lexer, parser, desugarer,
static analysis, interpreter and builtins follow go-jsonnet file by file, the standard library is the
`std.jsonnet` go-jsonnet embeds, and `std.parseYaml` uses a port of the YAML parser go-jsonnet uses
(goyaml.v2 from sigs.k8s.io/yaml). Output and error messages are the same text go-jsonnet produces.
It has no runtime dependencies.

Targets `netstandard2.0`, `netstandard2.1`, `net8.0`, and `net10.0`.

## Installation

``` bash
dotnet add package JsonnetSharp
```

## Usage

### Snippets and files

``` c#
using JsonnetSharp;

var vm = new JsonnetVm();
string json = vm.EvaluateAnonymousSnippet("example.jsonnet", "{ name: 'app', replicas: 1 + 2 }");
// {
//    "name": "app",
//    "replicas": 3
// }

string fromFile = vm.EvaluateFile("config/main.jsonnet"); // imports resolve relative to the file
```

The file name given to `EvaluateAnonymousSnippet` is used in error messages; imports in the snippet resolve
relative to the current directory and the importer's library paths. The result is the JSON text, with a trailing
newline unless `OutputNewline` is `false`. With `StringOutput = true` the program must evaluate to a string, which
is returned as is (go-jsonnet's `-S`).

### External variables and top-level arguments

``` c#
var vm = new JsonnetVm();
vm.ExtVar("env", "prod");                       // std.extVar('env') == "prod"
vm.ExtCode("limits", "{ cpu: 2, mem: '1Gi' }"); // evaluated as Jsonnet code
vm.TlaVar("name", "api");                       // arguments of a top-level function
vm.TlaCode("replicas", "3");

string json = vm.EvaluateAnonymousSnippet("main.jsonnet", @"
function(name, replicas) {
  name: name,
  env: std.extVar('env'),
  replicas: replicas,
  cpu: std.extVar('limits').cpu,
}");
```

`ExtReset` and `TlaReset` clear them. `MaxStack` (default 500) and `MaxTrace` (default 20, 0 for no limit) are
go-jsonnet's `-s` and `-t`; `SetTraceOut` redirects `std.trace` output (standard error by default).

### Importers

`vm.Importer` is a `FileImporter` by default. Add library directories with `JPaths` (go-jsonnet's `-J`; later
entries win):

``` c#
var vm = new JsonnetVm { Importer = new FileImporter { JPaths = { "vendor", "lib" } } };
```

`MemoryImporter` serves imports from a dictionary, which is handy for tests or code kept in a database:

``` c#
var vm = new JsonnetVm
{
    Importer = new MemoryImporter
    {
        Data =
        {
            ["lib.libsonnet"] = JsonnetContents.FromString("{ greet(n): 'hello ' + n }"),
            ["logo.png"] = JsonnetContents.FromBytes(File.ReadAllBytes("logo.png")), // for importbin
        },
    },
};
vm.EvaluateAnonymousSnippet("main.jsonnet", "(import 'lib.libsonnet').greet('world')"); // "hello world"
```

Custom importers implement `IJsonnetImporter`. They report a missing file by throwing, and must return the same
`JsonnetContents` instance each time for the same location, because the VM caches imports. Assigning `vm.Importer`
clears that cache; otherwise the same VM does not re-read a file changed on disk, as in go-jsonnet. A `FileImporter`
may be shared by VMs on different threads; a shared custom importer must be thread-safe.

`ResolveImport`, `ImportData` and `FindDependencies` expose the importer as go-jsonnet's VM does. `ImportData`
returns the `JsonnetContents`, whose `Data` keeps the raw bytes.

### Native functions

``` c#
var vm = new JsonnetVm();
vm.NativeFunction(new JsonnetNativeFunction("sha256", new[] { "s" }, args =>
{
    using var sha = System.Security.Cryptography.SHA256.Create();
    byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes((string)args[0]!));
    return BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant();
}));
vm.EvaluateAnonymousSnippet("x.jsonnet", "std.native('sha256')('abc')");
```

Arguments arrive as JSON values: `null`, `bool`, `double`, `string`, `List<object?>` and
`Dictionary<string, object?>`. The result may be one of those, an `int`, `long`, `short` or `sbyte`, or any
`IList<object?>` or `IDictionary<string, object?>`. An exception the function throws becomes a Jsonnet runtime
error with the exception as `InnerException` (an `OutOfMemoryException` is not caught); so does one thrown while the
result is read.

### Multiple files and streams

``` c#
IReadOnlyDictionary<string, string> files = vm.EvaluateAnonymousSnippetMulti("m.jsonnet",
    "{ 'a.json': { x: 1 }, 'b.json': [1, 2] }");      // go-jsonnet's -m: one JSON text per field
IReadOnlyList<string> docs = vm.EvaluateAnonymousSnippetStream("s.jsonnet",
    "[{ kind: 'Service' }, { kind: 'Deployment' }]"); // go-jsonnet's -y: one JSON text per element
```

`EvaluateFileMulti` and `EvaluateFileStream` do the same for files.

### Errors

``` c#
try
{
    vm.EvaluateAnonymousSnippet("bad.jsonnet", "{ a: 1 }.b");
}
catch (JsonnetException e)
{
    Console.Error.WriteLine(e.Message);
    // RUNTIME ERROR: Field does not exist: b
    //     bad.jsonnet:1:1-11    $
    //     During evaluation
}
```

`JsonnetStaticException` is thrown for syntax and static errors, `JsonnetRuntimeException` for errors during
evaluation; both derive from `JsonnetException`, whose message is the text go-jsonnet prints (stack trace lines
are tab-separated). "INTERNAL ERROR" messages link this project's issue tracker instead of go-jsonnet's. A `null`
argument throws `ArgumentNullException`.

## Threads and stack depth

go-jsonnet grows its stack up to 1 GB, so each evaluation runs on a thread with a big stack while the caller
waits: 256 MB by default, 32 MB on .NET Framework (which commits a thread's whole stack, so each concurrent
evaluation costs that much commit charge) and 16 MB in a 32-bit process. The depth a program can reach then does
not depend on the caller's thread. Evaluation threads are reused for back-to-back evaluations. In a 32-bit process,
evaluations beyond 512 MB of evaluation thread stacks run on the calling thread instead. A nonzero
`EvaluationStackSize` must be at least `JsonnetVm.MinEvaluationStackSize` (1 MB).

Importers, native functions and the `std.trace` writer are called on that thread. One that needs the caller's
thread (a lock the caller holds, a UI thread, thread-static state) deadlocks or misses it; set
`vm.EvaluationStackSize = 0` to evaluate on the calling thread instead, which limits the depth to what that
thread's stack allows (it should be at least 1 MB). Culture and `AsyncLocal` values flow to the evaluation thread.

There is no cancellation token. If the waiting thread is interrupted (or aborted on .NET Framework), the evaluation
stops at its next function call (or inside `std.range` and `std.makeArray`) before the exception propagates; other
builtins finish their current call first.

A `JsonnetVm` is not thread-safe; use one per thread (creating one is cheap).

## Differences from go-jsonnet

- Source nested deeper than the evaluation stack allows fails with the static error "Exceeded maximum parse depth
  limit.". go-jsonnet has no such error; it crashes at about 100,000 levels.
- Strings are .NET strings, so a string longer than about 2^30 UTF-16 code units (or, for `std.parseYaml` and
  `std.encodeUTF8`, one whose UTF-8 form exceeds 2 GB) fails with "string is too long for .NET". go-jsonnet allows
  larger strings.
- go-jsonnet's output is nondeterministic in a few cases (which of several colliding YAML keys wins, which bad key
  an error names); JsonnetSharp always gives one of go-jsonnet's answers.
- `FileImporter` reports a directory import as "is a directory" and a path with reserved characters as not found
  on every OS, as go-jsonnet does on Linux.
- Not ported: `ExtNode`/`TlaNode` (AST-valued variables), the deprecated `EvaluateSnippet`, the command line
  programs (`jsonnet`, `jsonnetfmt`, `jsonnet-lint`), the formatter, the linter and the debugger.

## Tests

The tests run go-jsonnet's test suites through the library: the `testdata` golden files, the cpp-jsonnet test
suite and command tests, and the unit tests of the lexer, parser, builtins, VM and YAML parser.
[docs/dogfooding](docs/dogfooding) records the differential runs against go-jsonnet 0.22.0 done before release.

## License

Apache-2.0, like go-jsonnet. JsonnetSharp is derived from go-jsonnet, Copyright 2015-2017 Google Inc.; see
[LICENSE](LICENSE). The standard library `std.jsonnet` comes from [google/jsonnet](https://github.com/google/jsonnet)
(Apache-2.0). The YAML parser is ported from sigs.k8s.io/yaml (MIT and BSD) and its goyaml.v2 (Apache-2.0,
Copyright Canonical Ltd.), which is in part a port of libyaml (MIT, Copyright Kirill Simonov). Parts of the Go
standard library and golang.org/x/crypto are ported too (BSD). Their notices are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md), which the NuGet package includes.
