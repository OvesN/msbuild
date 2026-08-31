# Reproducing the native-observer and BuildXL comparison

The repository contains a Windows-only differential harness in
`MSBuild.Benchmarks`. It runs the same evaluation inside a BuildXL Detours
sandbox while the native observer records semantic filesystem paths.

This harness is prototype evidence collection. BuildXL reports low-level
filesystem activity and can itself omit operations. A zero BuildXL-only path
count is not proof that every possible evaluation input is observed.

## Prerequisites

- Windows x64.
- A repository build that includes the .NET Framework benchmark output.
- The `Microsoft.BuildXL.Processes` package restored from the repository's
  configured feeds.

Build the repository once to create the bootstrap, then build the opt-in x64
benchmark variant:

```powershell
.\build.cmd -c Release -v quiet
.\artifacts\bin\bootstrap\core\dotnet.exe restore `
  .\src\MSBuild.Benchmarks\MSBuild.Benchmarks.csproj `
  --no-dependencies `
  -p:Platform=x64 `
  -p:EnableEvaluationObservationDetours=true
.\artifacts\bin\bootstrap\core\dotnet.exe build `
  .\src\MSBuild.Benchmarks\MSBuild.Benchmarks.csproj `
  -c Release `
  -f net472 `
  --no-restore `
  -p:Platform=x64 `
  -p:EnableEvaluationObservationDetours=true
```

The x64 platform uses separate binary and intermediate directories, and the
opt-in property uses a separate NuGet project-extensions directory. The
repository's default x86 .NET Framework benchmark outputs and assets remain
unchanged.

## Run

Run the combined native and Detours benchmark:

```powershell
.\artifacts\bin\MSBuild.Benchmarks\x64\Release\net472\MSBuild.Benchmarks.exe `
  --filter "*EvaluationObservationBenchmark.NativeAndDetours*" `
  --job Dry `
  --artifacts .\artifacts\BenchmarkDotNet\EvaluationObservationBuildXL
```

The benchmark creates deterministic synthetic projects for the `Typical`,
`GlobHeavy`, and `AmbientAndSdk` scenarios. Each child process performs one
unobserved warmup before the marked measurement window. The Detours broker
fails if injection fails, either marker is missing, the child fails, or no
filesystem accesses are reported.

The harness itself performs a literal comparison of normalized path sets. It
does not infer semantic ownership. In particular, BuildXL commonly reports
directories traversed while the native observer reports the glob and its
matching members. Those expected shape differences still require explicit
classification.

The console log includes:

- `NativeUniquePaths`, `DetoursUniquePaths`, and their exact normalized
  overlap;
- `NativeOnlyPaths` and `DetoursOnlyPaths`;
- one `EVALUATION_OBSERVATION_DETOURS_ONLY_PATH` line for each path seen only
  by BuildXL.

The three comparison fields are `-1` in the Detours-only benchmark because no
native path set exists. Path-difference lines are emitted only by the combined
benchmark.

BenchmarkDotNet stores the console log under the requested `--artifacts`
directory. Its generated CSV and Markdown tables contain timing columns, but
not the custom path summary lines.

The comparison is case-insensitive and filters both native and BuildXL sets to
the generated scenario root. Relative native glob and enumeration members are
rooted against their semantic directory, and escaped glob members are decoded
before normalization. A retained result that still contains a wildcard fails
the run with its glob role, root, and include because it is not a concrete path
set. Native paths are collected only from the first measured evaluation so
multiple iterations do not inflate the path set.

`NativeAndDetours` retains detailed native paths and therefore is a coverage
diagnostic, not a native-observer overhead measurement. Use the same-job
`Baseline` versus `Native` benchmarks to measure observation overhead.

## Interpretation

Every BuildXL-only path must be explained by a native semantic owner, an
explicitly excluded dependency domain, or a demonstrated tracing limitation.
Do not classify directories as harmless merely because they are traversal
intermediates: directory membership can affect a glob.

SDK resolver internals are outside the prototype contract until resolvers can
report their dependencies. The harness does not yet compare complete evaluated
imports, properties, items, and metadata; that semantic equivalence check is a
separate requirement.

See also:

- [BuildXL differential validation](evaluation-native-observation-buildxl-validation.md)
- [Adversarial comparison report](evaluation-native-observation-buildxl-adversarial-report.md)
- [Post-fix real-project report](evaluation-native-observation-buildxl-post-fix-report.md)
