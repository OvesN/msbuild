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
unchanged. The opt-in net472 build uses BenchmarkDotNet 0.13.12 because its
.NET Framework toolchain reuses the already-built EXE that contains the conditional
broker. BenchmarkDotNet 0.16 rebuilds a net481 DLL and drops that method. Normal
benchmark builds continue to use the project-local 0.16 preview override.

## Run

Run the combined native and Detours benchmark:

```powershell
.\artifacts\bin\MSBuild.Benchmarks\x64\Release\net472\MSBuild.Benchmarks.exe `
  --filter "*EvaluationObservationBenchmark.NativeAndDetours*" `
  --job Dry `
  --artifacts .\artifacts\BenchmarkDotNet\EvaluationObservationBuildXL
```

The benchmark creates deterministic synthetic projects for the `Typical`,
`GlobHeavy`, and `AmbientAndSdk` scenarios. Before the marked measurement
window, every child process performs one observer-disabled reference
evaluation and one observer-enabled evaluation, then compares their semantic
results. All preflight and measured evaluations use
`RecordDuplicateButNotCircularImports`. The comparison is outside
`EvaluationTicks`, and its snapshots become unreachable before the forced GC
and measurement. Baseline and native modes therefore receive the same
preflight and JIT preparation. The Detours broker fails if injection fails,
either marker is missing, the child fails, or no filesystem accesses are
reported.

The semantic comparison covers:

- import paths in evaluation order, including duplicate imports;
- all evaluated properties, sorted by MSBuild name semantics;
- item types in deterministic name order, preserving item order and duplicates
  within each type; and
- effective custom metadata for every item, sorted by MSBuild name semantics.

Values are compared in their escaped form. A mismatch fails with its category
and location. Property, item, and metadata values are represented only by
length and first-difference position rather than copied into logs; import
mismatches also identify the file names. The fixtures assert two recorded
imports of the same project, ordered duplicate items, and escaped property,
item, and metadata values so those checks cannot pass vacuously.
`SemanticComparisons` is `1` for every mode, and the `SemanticImports`,
`SemanticProperties`, `SemanticItems`, and `SemanticMetadata` fields report the
compared cardinalities. Benchmark setup also verifies that the comparer
detects independent mutations in each category. Targets and target bodies,
`UsingTask` registrations, and `ItemDefinitionGroup` declarations themselves
are outside this semantic comparison; effective item-definition metadata is
included. The duplicate import deliberately adds the same symmetric
duplicate-detection and recording work to every measured mode so the
import-multiplicity check is non-vacuous.

For Detours modes, the observer-enabled preflight also retains and processes
detailed paths, matching the higher-risk observation configuration used by the
combined measurement. The host rejects process-wide file-existence or
enumeration caches because they could make the second evaluation depend on
state populated by the first.

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
`PrivateBytes` and `PeakWorkingSetBytes` are whole-process values and include
the common observer-enabled semantic preflight in every mode; do not use those
two fields to infer incremental observer memory. `EvaluationTicks` excludes
the preflight. `RetainedManagedBytes` is measured around the evaluation loop.
`AllocatedManagedBytes` is also a loop delta on .NET, but is unavailable and
reported as `0` on `net472`.

## Interpretation

Every BuildXL-only path must be explained by a native semantic owner, an
explicitly excluded dependency domain, or a demonstrated tracing limitation.
Do not classify directories as harmless merely because they are traversal
intermediates: directory membership can affect a glob.

SDK resolver internals are outside the prototype contract until resolvers can
report a complete dependency manifest or authoritative validity token. The
`AmbientAndSdk` scenario exercises an SDK-bearing evaluation, adds the returned SDK path
to the native path set, and checks the resulting imports and evaluated state. It does not
assert the SDK request/result record fields, establish resolver-internal dependency
completeness, or authorize a correctness-capable cache hit. The
semantic check compares observer-disabled and observer-enabled evaluations in
the same child process; it isolates the native observer's effect, but does not
prove equivalence between sandboxed and unsandboxed processes.

See also:

- [BuildXL differential validation](evaluation-native-observation-buildxl-validation.md)
- [Adversarial comparison report](evaluation-native-observation-buildxl-adversarial-report.md)
- [Post-fix real-project report](evaluation-native-observation-buildxl-post-fix-report.md)
