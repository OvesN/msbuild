# Evaluation filesystem timestamp invalidation prototype

> [!IMPORTANT]
> These measurements cover the current restacked prototype, including optimized
> FileMatcher traversal observation, strict default-deny admission, typed-existence
> replay, and reparse-component validation.
>
> The benchmark uses the analysis-only filesystem slice. Current observation coverage
> remains intentionally ineligible for a real cache hit, and the measurements exclude
> cache lookup, serialization, loading, result materialization, concurrency, eviction,
> non-filesystem dependency validation, and the validation-to-materialization race.

## Current result

Three-launch BenchmarkDotNet runs on 2026-09-02 show:

- complete unchanged validation costs 11.969 ms for OrchardCore Core, 19.342 ms
  for OrchardCore CMS, and 16.800 ms for Roslyn Workspaces;
- those scans cost 16.9%, 20.9%, and 14.4% of fresh evaluation respectively,
  making them approximately 5.9x, 4.8x, and 6.9x faster than reevaluation;
- project-file and imported-file invalidation costs 7.2-10.5% of fresh
  evaluation after paying the mandatory complete reparse-component scan;
- naturally triggered glob-membership invalidation costs 20.6% of fresh
  OrchardCore CMS evaluation and 13.7% of fresh Roslyn evaluation;
- observation alone adds approximately 1.04-1.13x evaluation time and
  1.04-1.05x managed allocation;
- observation plus snapshot capture costs approximately 1.38-1.54x fresh
  evaluation time and 1.07-1.08x managed allocation.

The current complete scan does not meet the predeclared continuation target of
less than 10% of fresh evaluation. It is still substantially cheaper than
reevaluation, so the result supports a focused optimization experiment, not a
claim that the present invalidation design is cheap enough or that a complete
persistent cache will be beneficial.

Timestamp-only invalidation is not sufficient for production correctness. It
cannot detect timestamp-preserving changes, changes within filesystem timestamp
granularity, or replacement that preserves both the timestamp and every existence
predicate evaluation actually consumed.

## Current restacked mechanism and limitations

Observer activation no longer changes FileMatcher driver selection. The optimized
callback driver records each directory before reading cached or uncached entries; the
optimized direct driver records its root and every child accepted for recursion.
Excluded subtrees are not recorded, missing fixed roots retain a negative directory
observation, and unsupported patterns use their existing legacy fallback with the same
observer. Process-wide glob-result cache hits still fail closed when no traversal
evidence exists in the current observation session. Observed and unobserved optimized
evaluations now share the normal result-cache partition, so a cache entry populated
before observation can reduce snapshot admission rather than silently reuse missing
evidence.

Observation adds one synchronized directory record and a first-use timestamp read for
each optimized directory traversal. The direct optimized driver is otherwise
callback-free, so this work can be a larger proportional overhead than it was on legacy
traversal and is included in the current observer-overhead measurements.

The observation session records a canonical absolute path, independent expected
results for file existence, directory existence, and generic file-or-directory
existence, the consumed last-write timestamp, and source flags for each filesystem
identity needed by timestamp validation. Opposite-kind probes therefore remain
independent: `File.Exists` can be false while `Directory.Exists` is true for the same
path. Successful file reads record file existence, and successful enumerations record
directory existence. Logically entailed predicates are collapsed, so a known file or
directory does not also retain a redundant generic-existence check. Repeated
observations reuse one path-level timestamp during an evaluation.

The snapshot includes dependencies from:

- root and imported project sources;
- file reads and file, directory, or file-or-directory probes;
- consumed last-write-time metadata;
- complete directory enumerations;
- directories actually traversed by each glob, including repeated expansion within the
  same observation session;
- every ordered upward-search candidate and the selected result.

Glob result membership is invalidated through timestamps of the directories
that the glob actually traversed. Exact-file includes do not add an artificial
directory dependency. Benchmark mutations exclude generated and tool roots
such as `.dotnet`, `.git`, `artifacts`, `bin`, `obj`, and `packages`.

Before admitting a snapshot, the prototype checks every unique observed filesystem
path and its ancestors for `FileAttributes.ReparsePoint`. This rejects file and
directory symbolic links, Windows junctions, and observed paths beneath them,
regardless of whether the dependency came from a project source, file read, probe,
metadata read, enumeration, glob, or search. Missing path components are treated as
absent while their existing ancestors are still checked, and the missing components are
recorded and rechecked later. Any other attribute-read failure rejects the snapshot as
`ReparsePointStateUnknown`, including access failures and unreachable filesystem roots.
The sorted set of checked components is stored in the snapshot and rechecked before
every complete `Validate()` reuse check, so a reparse point present at reuse invalidates
the snapshot even when timestamps match. A link already present during construction is
`Unsupported`; one appearing during final or reuse validation is `Changed`; an
attribute failure is `Failed`. Validation also rejects a missing, duplicate, or
incomplete persisted component set, or any non-canonical persisted path, before probing
the filesystem. The component scan and following timestamp reads are not atomic;
closing that validation-to-materialization race remains production work.

Capture checks components while constructing the snapshot and deliberately performs
the complete component scan again during its final validation pass. The first pass
rejects a link used by evaluation even if it disappears before final validation; the
second rejects a link introduced during capture. This brackets, but does not make
atomic, snapshot construction. A successful capture therefore issues exactly twice the
stored component count in attribute probes. The benchmark exposes
`ValidReparsePointValidation` and
`ValidTimestampValidationWithoutReparsePointCheck` separately so the new cost can be
measured. The latter is benchmark-only and is not a valid reuse decision. Capture
reports the total number of component probes issued across both passes. These checks do
not change normal evaluation or glob results. A production implementation can reduce
syscalls by reading attributes, existence, and timestamps through one metadata probe.

The prototype deliberately rejects every `ReparsePoint` tag, including non-aliasing
cloud, deduplication, and container placeholders. This is conservative and can reduce
admission to zero for repositories hosted by those systems or beneath a stable
filesystem symlink such as macOS `/var`. It does not detect alias mechanisms that do
not expose a reparse component, such as `subst` drives, mapped-drive retargeting, or
DFS namespace changes. Hard-link identity is also not distinguished, although hard
links share file timestamps and remain subject to the general timestamp-preserving
replacement limitation. A production implementation should classify name-surrogate
links precisely and record and validate both logical and resolved target identities.

A glob-result cache hit populated by another evaluation does not replay traversal
evidence. Snapshot capture therefore fails closed for that case. Lazy wildcards also
currently fail closed because no traversal occurs.

Filesystem-snapshot admission is exhaustive and default deny. `Capture` rejects an
unsuccessful evaluation, an unsupported observation or
property-function-classification version, any non-cacheable reason, a missing or
duplicate category, any category whose implementation coverage is not `Complete`, any
category state other than `NotExercised` or `Observed`, a missing or mismatched request,
and a report without a parsed root source matching the evaluated project path. The
current observer deliberately reports `Partial` implementation coverage for every
non-completion category, so no report can currently produce an admissible filesystem
snapshot. Passing this gate does not make a complete cache entry eligible:
non-filesystem inputs still require cache-key fields or dependency contracts.

Filesystem mechanism tests and timing use
`CaptureFilesystemSliceForAnalysis`. That analysis-only path bypasses report-level
admission but still fails closed when filesystem-category observation is incomplete, a
project source changed while it was read, observations or timestamps conflict, a path
is unrooted, an unsupported provider or metadata operation was used, a filesystem
operation failed, or required glob/search traversal evidence is missing. Toolset- and
SDK-resolver-mediated filesystem dependencies remain deferred. Its output measures
scan cost, returns `AnalysisOnly`, and is marked
`IsFilesystemSnapshotAdmissible = false`; it is not evidence of an admissible cache
hit.

Validation performs these operations:

1. Recheck every stored path component for `ReparsePoint`, returning `Changed` with
   `ReparsePointTraversal` on the first match or `Failed` with
   `ReparsePointStateUnknown` on any other attribute error.
2. Read each dependency's current last-write timestamp.
3. Replay each file, directory, and generic-existence predicate that evaluation
   actually consumed.
4. Return `Changed` on the first timestamp or typed-existence mismatch.

It does not reread or hash contents, rerun complete probe/search operations, or
reenumerate globs; it replays only stored existence predicates. After a matching
timestamp, each entry performs only the non-entailed existence checks retained in its
snapshot state. This is generally one additional `FileExists` or `DirectoryExists`
call for typed file and directory dependencies; generic `exists == true` is free when
the matching timestamp already proves that some path exists. The current timing rows
include these checks.
The reported check counts include the operation that found a change or failed.

A negative typed probe can bind the snapshot to the path-level timestamp of an existing
opposite-kind object. Churn inside that object may therefore invalidate conservatively
even though the consumed typed predicate remains false.

This prototype covers only filesystem mutations. Environment variables,
global properties, toolset selection, SDK resolver results, Registry values,
process ambient state, task registrations, and other non-filesystem inputs
remain part of the observation report and require cache-key fields or their own
versioned dependency contracts. SDK resolver filesystem dependencies are
intentionally deferred to the resolver contract.

## Current BenchmarkDotNet setup

| Property | Value |
| --- | --- |
| Date | 2026-09-02 |
| Prototype implementation | `981f36c87de4001d6c614bcbe33a32831b51f716` plus benchmark-only batching in this update |
| Platform | Windows 11 `10.0.26200.9106`, Hyper-V |
| Processor | AMD EPYC 7763, 8 physical / 16 logical cores |
| Memory | 63.95 GB |
| Power plan | High performance |
| Runtime | .NET 11 RC1, x64 RyuJIT |
| SDK | `11.0.100-rc.1.26420.103` |
| BenchmarkDotNet | `0.16.0-preview.1` |
| Configuration | Release |
| Job | Monitoring, 3 launches, 3 warmups, 12 measured iterations |
| Per-invocation batching | 2 evaluations, 8 captures, or 24 validations; reported values are normalized per operation |

| Workload | Commit | Project | Timestamps | Reparse components | Capture probes |
| --- | --- | --- | ---: | ---: | ---: |
| OrchardCore Core | `e3f8acb327a95f1dec6e75cefccaef2ad5eefb45` | `src\OrchardCore\OrchardCore\OrchardCore.csproj` | 90 | 149 | 298 |
| OrchardCore CMS | `e3f8acb327a95f1dec6e75cefccaef2ad5eefb45` | `src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj` | 151 | 236 | 472 |
| Roslyn Workspaces | `0f82fdec3c901702ec7fc3f0e9a813330a903ec9` | `src\Workspaces\Core\Portable\Microsoft.CodeAnalysis.Workspaces.csproj` | 161 | 233 | 466 |

The benchmark checkouts were restored and clean before and after every run.
BenchmarkDotNet reported no minimum-iteration-time warnings in the final runs.
It did report the Hyper-V environment and multimodal distributions for some
evaluation rows, so the direct validation measurements are more stable than
small differences between independent full evaluations. Errors below are
BenchmarkDotNet's 99.9% confidence-interval half widths.

### Reproduction

Run from the MSBuild repository root with disposable restored OrchardCore and
Roslyn checkouts:

```powershell
$OrchardRoot = 'C:\src\OrchardCore'
$RoslynRoot = 'C:\src\roslyn'
$ResultsRoot = 'C:\benchmark-results\evaluation-timestamp'
$SdkVersion = (Get-Content .\global.json -Raw | ConvertFrom-Json).tools.dotnet

$env:MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_SDK_ROOT =
    (Resolve-Path ".\.dotnet\sdk\$SdkVersion").Path
$env:MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_PROJECTS = @(
    "$OrchardRoot\src\OrchardCore\OrchardCore\OrchardCore.csproj"
    "$OrchardRoot\src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj"
    "$RoslynRoot\src\Workspaces\Core\Portable\Microsoft.CodeAnalysis.Workspaces.csproj"
) -join [IO.Path]::PathSeparator
Remove-Item Env:\MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_MUTATIONS -ErrorAction SilentlyContinue

.\src\MSBuild.Benchmarks\Run-Benchmarks.ps1 `
    -Filter '*OrchardCoreEvaluationFilesystemTimestampBenchmark.*' `
    -Framework net11.0 `
    -LaunchCount 3 `
    -ArtifactsPath "$ResultsRoot\normal"
```

```powershell
$env:MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_MUTATIONS = 'ProjectFile,ImportFile'

.\src\MSBuild.Benchmarks\Run-Benchmarks.ps1 `
    -Filter '*OrchardCoreEvaluationFilesystemTimestampStaleBenchmark.*' `
    -Framework net11.0 `
    -LaunchCount 3 `
    -ArtifactsPath "$ResultsRoot\file-import"
```

```powershell
$env:MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_PROJECTS = @(
    "$OrchardRoot\src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj"
    "$RoslynRoot\src\Workspaces\Core\Portable\Microsoft.CodeAnalysis.Workspaces.csproj"
) -join [IO.Path]::PathSeparator
$env:MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_MUTATIONS = 'GlobMembership'

.\src\MSBuild.Benchmarks\Run-Benchmarks.ps1 `
    -Filter '*OrchardCoreEvaluationFilesystemTimestampStaleBenchmark.*' `
    -Framework net11.0 `
    -LaunchCount 3 `
    -ArtifactsPath "$ResultsRoot\glob"
```

### Evaluation, observation, and capture overhead

| Workload | Fresh evaluation | Observed evaluation | Time ratio | Allocation ratio | Observed and capture | Time ratio | Allocation ratio | Capture only |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| OrchardCore Core | 70.714 +/- 3.964 ms | 72.880 +/- 4.155 ms | 1.04x | 1.04x | 96.689 +/- 5.768 ms | 1.38x | 1.07x | 23.490 +/- 2.617 ms |
| OrchardCore CMS | 92.384 +/- 4.974 ms | 103.938 +/- 7.782 ms | 1.13x | 1.05x | 140.850 +/- 10.805 ms | 1.54x | 1.08x | 29.382 +/- 1.913 ms |
| Roslyn Workspaces | 116.306 +/- 8.309 ms | 128.887 +/- 9.868 ms | 1.12x | 1.05x | 163.131 +/- 10.757 ms | 1.42x | 1.07x | 25.934 +/- 1.681 ms |

Observation alone adds approximately 3-13% mean evaluation time and 4-5%
managed allocation. Observation plus capture adds approximately 38-54% mean
time and 7-8% managed allocation. Snapshot capture alone costs 22-33% of fresh
evaluation because capture performs the initial metadata reads and a final
complete validation, including two reparse-component passes overall.

### Unchanged validation versus fresh reevaluation

| Workload | Reparse check only | Timestamp and existence only | Complete validation | Percent of fresh | Fresh / validation |
| --- | ---: | ---: | ---: | ---: | ---: |
| OrchardCore Core | 6.621 +/- 0.774 ms | 6.986 +/- 0.763 ms | 11.969 +/- 0.868 ms | 16.9% | 5.9x |
| OrchardCore CMS | 9.544 +/- 0.669 ms | 10.367 +/- 0.677 ms | 19.342 +/- 1.090 ms | 20.9% | 4.8x |
| Roslyn Workspaces | 8.222 +/- 0.510 ms | 9.550 +/- 0.638 ms | 16.800 +/- 0.736 ms | 14.4% | 6.9x |

The isolated reparse and timestamp rows are diagnostic and are not additive:
they run separately with different filesystem-cache state. The
timestamp-without-reparse row is benchmark-only and cannot make a safe reuse
decision. The complete validation row is the relevant cache-hit invalidation
cost.

### Changed dependency and miss costs

The stale suite mutates a real observed dependency before each measured
iteration and exactly restores it afterward. Glob membership is changed by
creating a file and relying on the filesystem's natural parent-directory
timestamp update; the benchmark does not force that directory timestamp.
OrchardCore Core has no eligible observed glob directory, so its glob case is
not applicable.

| Workload | Mutation | Fresh evaluation | Stale validation | Percent of fresh | Validation and reevaluation | Combined ratio |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| OrchardCore Core | Project file | 62.861 ms | 5.561 ms | 8.8% | 66.090 ms | 1.06x |
| OrchardCore Core | Imported file | 80.804 ms | 7.309 ms | 9.0% | 72.529 ms | 0.91x |
| OrchardCore CMS | Project file | 88.410 ms | 8.644 ms | 9.8% | 95.588 ms | 1.09x |
| OrchardCore CMS | Imported file | 92.039 ms | 9.685 ms | 10.5% | 107.247 ms | 1.17x |
| OrchardCore CMS | Glob membership | 91.070 ms | 18.770 ms | 20.6% | 111.630 ms | 1.24x |
| Roslyn Workspaces | Project file | 107.566 ms | 7.741 ms | 7.2% | 122.131 ms | 1.15x |
| Roslyn Workspaces | Imported file | 113.682 ms | 8.372 ms | 7.4% | 116.926 ms | 1.04x |
| Roslyn Workspaces | Glob membership | 115.410 ms | 15.830 ms | 13.7% | 135.950 ms | 1.20x |

The current algorithm scans every stored component before reading the first
timestamp. Consequently, project and import changes no longer produce the
sub-millisecond early-out seen in the historical prototype. The timestamp
scan still exits early, while a glob mutation is detected near the end of the
complete scan.

The direct stale-validation rows are the useful incremental measurement.
Combined rows include normal reevaluation variance and, for glob membership,
a genuinely changed item set. In particular, the 0.91x Core import result is
measurement noise and does not mean validation makes reevaluation faster.

## Historical BuildXL coverage comparison

On the pre-restack base, the observer was run against BuildXL Detours for the same
restored OrchardCore projects. Paths were converted to absolute canonical identities,
Windows device prefixes and trailing separators were normalized, and
BuildXL's randomized case-sensitivity probe was excluded.

| Project | Native identities | BuildXL identities | Exact overlap | Native-only | BuildXL-only |
| --- | ---: | ---: | ---: | ---: | ---: |
| OrchardCore Core | 327 | 456 | 233 | 94 | 223 |
| OrchardCore CMS Web | 2,753 | 533 | 313 | 2,440 | 220 |

The large native-only CMS set is expected: MSBuild records semantic glob result
members even when BuildXL observes only the directory traversal. BuildXL-only
identities were classified as host/runtime/SDK/toolset accesses or
implementation-level recursive-glob traversal paths. No unexplained built-in
project-source, import, file-read, probe, search, or glob dependency remained.

This comparison covered the legacy FileMatcher traversal on the pre-restack base. It is
historical evidence, not BuildXL coverage evidence for the newly observer-enabled
optimized drivers, and not a proof over every possible project, custom filesystem
provider, property function, or future SDK resolver.

## Decision

The current restacked result supersedes the historical timing rows. Complete
validation is 4.8-6.9x faster than fresh reevaluation, but it consumes
14.4-20.9% of reevaluation time and therefore does not pass the predeclared
less-than-10% continuation threshold.

The result is sufficient to justify one targeted optimization stage: combine
attributes, existence, and timestamps into fewer metadata probes, deduplicate
component work, and interleave component and timestamp checks to restore
early-out behavior. The benchmark must then be rerun. This report does not
justify implementing persistence or claiming an end-to-end cache speedup yet.
