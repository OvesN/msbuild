# Evaluation filesystem timestamp invalidation prototype

> [!IMPORTANT]
> These measurements were collected on the pre-restack base, before current `main`
> introduced the optimized FileMatcher drivers. They are retained as historical
> prototype evidence only and are not decision-grade for the restacked PR.
>
> The compatibility restack routes observer-active globs through the legacy driver to
> preserve traversed-directory evidence, while an unobserved evaluation may use the
> optimized driver. Fresh evaluation and validation benchmarks must use the same driver
> and evaluation graph before this report can support a continuation decision.
>
> The historical rows also predate strict snapshot admission, the
> conflicting-observation analysis gate, and reparse-component scanning during capture
> and validation, as well as independent typed-existence replay. Current benchmark setup
> may reject a blocked report instead of reproducing those rows, and validation costs
> have changed; the rows are retained only as prior directional evidence.

## Historical result

On the pre-restack base, the prototype showed that timestamp validation itself was
substantially cheaper
than reevaluating the OrchardCore and Roslyn projects exercised here:

- a valid snapshot scan cost 3.656-6.425 ms, about 3-5% of a fresh evaluation;
- a file mutation detected early cost 0.089-0.313 ms;
- a glob membership mutation detected late cost 5.864-7.072 ms, about 4-6%
  of a fresh evaluation;
- native observation plus filesystem-slice capture cost about 1.13-1.23x a
  fresh evaluation.

Those historical results did not prove that a persistent evaluation cache was worthwhile,
because cache serialization, loading, materialization, and real hit rates are
not measured. On that base, the measured validation work was approximately 21-30 times
cheaper than reevaluation before accounting for cache-load cost. That conclusion is not
carried forward to the restacked branch.

Timestamp-only invalidation is not sufficient for production correctness. It
cannot detect timestamp-preserving changes, changes within filesystem timestamp
granularity, or replacement that preserves both the timestamp and every existence
predicate evaluation actually consumed.

## Current restacked mechanism and limitations

Current `main` can select optimized FileMatcher drivers. The compatibility restack forces
observer-active globs through the legacy driver because only that path reports every
traversed directory. An unobserved evaluation may still use an optimized driver, so the
existing benchmarks are intentionally historical until graph alignment is implemented.

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
the matching timestamp already proves that some path exists. Current historical timing
rows predate these checks.
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

## OrchardCore BenchmarkDotNet setup

| Property | Value |
| --- | --- |
| Date | 2026-08-28 |
| Platform | Windows |
| Runtime | .NET 11 preview |
| Configuration | Release |
| Job | 1 launch, 3 warmups, 12 measured iterations, 1 invocation |
| Core project | `src\OrchardCore\OrchardCore\OrchardCore.csproj` |
| CMS project | `src\OrchardCore.Cms.Web\OrchardCore.Cms.Web.csproj` |
| Core snapshot | 90 timestamp identities |
| CMS snapshot | 151 timestamp identities |

The normal suite compares an unobserved evaluation, an observed evaluation,
an observed evaluation followed by snapshot capture, snapshot capture alone,
isolated reparse-component validation, benchmark-only timestamp validation without the
component check, and complete validation of an unchanged snapshot. Snapshot capture
includes a final full validation pass before returning the snapshot.

### Filesystem-slice capture and validation costs

| Workload | Core mean | Core vs. fresh | CMS mean | CMS vs. fresh |
| --- | ---: | ---: | ---: | ---: |
| Fresh evaluation | 84.169 ms | 1.00x | 136.465 ms | 1.00x |
| Observed evaluation | 95.773 ms | 1.14x | 153.426 ms | 1.12x |
| Observed evaluation and snapshot capture | 103.342 ms | 1.23x | 160.308 ms | 1.17x |
| Snapshot capture | 3.718 ms | 0.04x | 6.629 ms | 0.05x |
| Valid snapshot validation | 3.656 ms | 0.04x | 6.425 ms | 0.05x |

Observation added approximately 304 KB per Core evaluation and 487 KB per CMS
evaluation. Observation plus snapshot capture added approximately 324 KB and
505 KB, respectively.

### Mutation and miss costs

The stale suite mutates a real observed dependency before each measured
iteration and restores the exact contents and timestamp afterward.

| Mutation | Fresh evaluation | Stale validation | Validation and reevaluation | Combined ratio |
| --- | ---: | ---: | ---: | ---: |
| Core project file | 95.603 ms | 0.089 ms | 88.264 ms | 0.93x |
| Core imported `Directory.Build.props` | 89.181 ms | 0.283 ms | 88.857 ms | 1.00x |
| CMS glob membership | 125.621 ms | 7.072 ms | 144.237 ms | 1.15x |

The combined Core values are within run-to-run evaluation noise and must not be
interpreted as invalidation making reevaluation faster. These historical stale rows
predate reparse-component validation. The current algorithm scans all stored components
before reading the first timestamp, so every current hit or miss pays that complete
component-scan cost; only the subsequent timestamp scan retains early-out behavior. A
production implementation should validate each entry's memoized component chain
immediately before its timestamp read, preserving the same safety while restoring
early-out across the combined scan.

## Roslyn Workspaces BenchmarkDotNet results

The same statistical jobs were run on Roslyn commit
`0f82fdec3c901702ec7fc3f0e9a813330a903ec9`, using
`src\Workspaces\Core\Portable\Microsoft.CodeAnalysis.Workspaces.csproj`.
This evaluation allocated about 13.4 MB and produced a snapshot with 161
timestamp identities.

### Filesystem-slice capture and validation costs

| Workload | Mean | Ratio to fresh |
| --- | ---: | ---: |
| Fresh evaluation | 163.786 ms | 1.00x |
| Observed evaluation | 187.436 ms | 1.15x |
| Observed evaluation and snapshot capture | 184.270 ms | 1.13x |
| Snapshot capture | 6.041 ms | 0.04x |
| Valid snapshot validation | 5.475 ms | 0.03x |

Observation added approximately 675 KB per evaluation. Observation plus
snapshot capture added approximately 703 KB. The observed-plus-capture mean is
lower than the observed-only mean because the independently measured
evaluations vary by several milliseconds; the standalone 6.041 ms capture row
is the clearer measurement of capture work.

### Mutation and miss costs

| Mutation | Fresh evaluation | Stale validation | Validation and reevaluation | Combined ratio |
| --- | ---: | ---: | ---: | ---: |
| Workspaces project file | 159.073 ms | 0.090 ms | 178.483 ms | 1.12x |
| Roslyn `Directory.Build.props` import | 158.594 ms | 0.313 ms | 166.774 ms | 1.05x |
| `src\Dependencies\PooledObjects` glob membership | 166.512 ms | 5.864 ms | 169.699 ms | 1.02x |

The direct stale-validation cost remains the useful incremental measurement.
The combined rows include normal reevaluation variance and should not be used
to infer that the sub-millisecond file scan caused the full difference.

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
historical evidence, not coverage evidence for the optimized drivers now present on
`main`, and not a proof over every possible project, custom filesystem provider,
property function, or future SDK resolver.

## Historical decision withdrawn

The pre-restack continuation statement is withdrawn for the current branch. The next
decision-grade run must first align the observed, validation, and fresh-evaluation graphs
and then answer only whether a complete timestamp scan is sufficiently cheaper than
fresh evaluation to continue.
