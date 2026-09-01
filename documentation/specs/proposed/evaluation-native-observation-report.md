# Native Evaluation Observation Report

BuildXL differential validation is documented in
[evaluation-native-observation-buildxl-validation.md](evaluation-native-observation-buildxl-validation.md).
The adversarial follow-up and confirmed counterexamples are documented in
[evaluation-native-observation-buildxl-adversarial-report.md](evaluation-native-observation-buildxl-adversarial-report.md).
The post-fix six-scenario comparison is documented in
[evaluation-native-observation-buildxl-post-fix-report.md](evaluation-native-observation-buildxl-post-fix-report.md).
Current total overhead is documented in
[evaluation-native-observation-timing-report.md](evaluation-native-observation-timing-report.md).

## Current scope and claims

This is an observation prototype, not a cache-admission decision. It records the value or
semantic result consumed at MSBuild-owned interception points and separately reports
known incomplete, unsupported, conflicting, or unverifiable inputs.

SDK observation is complete only at the MSBuild/resolver boundary: the report contains
the complete request, returned `SdkResult`, hit/miss, and cache
owner/scope/epoch/key/entry identity. Resolver discovery and resolver-internal file,
environment, Registry, network, workload, manifest, and host dependencies are not
observed. A live SDK cache entry proves that the same boundary result remains stored; it
does not prove those internal dependencies are unchanged.

Until resolvers expose a complete dependency manifest or an authoritative validity
token/generation with defined scope, lifetime, and invalidation semantics, any
correctness-capable evaluation cache, including a process-local MSBuild Server cache,
must reject SDK-bearing evaluations.

A measurement-only experiment may bind to the exact SDK entry while it remains current.
Normal build entries are submission- or node-build-scoped, but a retained
`EvaluationContext` can keep its own entry current across independent evaluations.
Currentness is never sufficient for correctness-capable admission: the policy rejects a
cross-build Server candidate without the resolver contract. A shared-context SDK
benchmark is an invalidation-disabled upper-bound measurement, not submission-cache
behavior or cache correctness. See
[SDK boundary and future dependency contract](evaluation-observation-layer-design-details.md#sdk-boundary-and-future-dependency-contract).

## Session lifecycle

1. File-based `Project` and `ProjectInstance` loads hash and stamp successful root
   sources on the resulting `ProjectRootElement`; a temporary capture retains metadata
   for load or parse failures. A malformed root produces a minimal failed report before
   `Evaluator` exists; its request category is incomplete.
2. `Evaluator` calls `EvaluationObservationSession.TryCreate`. When the feature is
   disabled it returns `null`, so the normal evaluator path is unchanged.
3. The session is passed to `PropertyTrackingEvaluatorDataWrapper`. The active evaluation
   `IFileSystem`, including any directory-cache wrapper, is wrapped by
   `RecordingFileSystem`.
4. `RecordInitialObservationSnapshot` records the root source and effective request state
   once. It reuses existing evaluator, toolset, environment, trait, and PRE data.
5. `EvaluationObservationSession.Enter` makes the session available through the
   thread-static `Current` property and the Framework `EvaluationInputObserver` scope while
   `Evaluator.Evaluate` runs.
6. Recorders add typed observations to per-session keyed collections. One lock protects
   mutation and completion. Repeated identical facts are deduplicated; conflicting facts,
   unsupported operations, partial results, and failures add typed reasons.
7. `Evaluator` calls `Complete` from `finally`, including failed evaluations. Completion
   closes the session under the lock and creates one read-only
   `EvaluationObservationReport`.
8. Report finalization transfers the populated dictionaries/lists to read-only report
   views without array copies. The completed session detaches its references because it
   can remain reachable through evaluator objects. Late callbacks see completion and
   cannot change the report.

## What is collected and where

| Category | What is recorded | Observation seam | Existing data reused |
| --- | --- | --- | --- |
| Request | Global properties, load/evaluation settings, toolset, runtime/OS/culture, feature switches, escape hatches, working directories, provider/cache modes | `Evaluator.RecordInitialObservationSnapshot` | Effective evaluator state, `BuildEnvironmentHelper`, `Traits`, toolset provider, global-property dictionary |
| Project/import sources | Root, imported, linked, generated, and in-memory sources; parsed/parse-failure/load-failure outcome, path/provider, PRE version, content identity, consumed last-write time | `Evaluator` root/import processing and `ProjectRootElement` load paths | PRE/link versions, load-time timestamps, and cached source hashes; malformed imports finish hashing through the same open stream rather than reopening the file |
| File probes and reads | Canonical absolute positive/negative file and directory probes, Windows extended-path alias normalization, content hashes, failures, provider identity; raw-byte `Directory.Parse.config` content hash and parse outcome | `RecordingFileSystem` over the active evaluation `IFileSystem`; direct evaluator/intrinsic/parser hooks only where no filesystem call exists | Existing filesystem or parser result, effective base directory, and provider; no validation-time reprobe |
| Metadata | Filesystem times, lengths, attributes, returned value/failure | `RecordingFileSystem`, time-based `ItemSpecModifiers`, and classified property functions | Existing metadata result used by evaluation; lexical path calculations are ambient path-resolution records |
| Enumerations and globs | Request pattern, recursion, complete `EnumerationOptions` identity where applicable, excludes, completion, count, ordered result fingerprint, optional retained details | `RecordingFileSystem`, classified property functions, and `EngineFileUtilities`/`FileMatcher` semantic completion | The enumeration/glob result already produced for evaluation; no second enumeration |
| Searches | Ordered candidates/fingerprint and ordered selected paths/count/fingerprint; an empty selected sequence is a miss, while ignored wildcard matches remain selected | `FileUtilities` through `IEvaluationInputObserver`, plus evaluator import/toolset searches | Existing candidate sequence and selected results; selected paths remain retained when candidate details are omitted |
| Environment | Imported, missing imported, SDK-injected, and live process values | `PropertyTrackingEvaluatorDataWrapper`; `Environment` property-function interception | Existing property lookup or property-function result |
| Registry and ambient inputs | Registry requests/results/failures, culture/time/runtime/tool-location values, lexical item/path-member results, `MakeRelative` resolved base/path, volatile values | `ItemSpecModifiers`, intrinsic expansion, path-normalization seams, and post-execution property-function interception | Actual input/base/instance and returned value consumed by expansion |
| Property functions | Receiver/member, classified effect, arguments/result or failure | `Expander.Function` after dispatch | Existing invocation result; known-pure calls are omitted |
| SDK resolution | Complete resolver request, returned `SdkResult`, hit/miss, and cache owner/scope/key/epoch/entry identity with a live-entry validator | `CachingSdkResolverService` and out-of-proc SDK service | Exact boundary result and cache-entry lifetime; resolver-internal dependencies remain opaque and block correctness-capable reuse without a resolver contract |
| Tasks and toolsets | Effective `UsingTask` registration and selected toolset/provider identity | `TaskRegistry` and evaluator initialization | Resolved task registration and toolset already selected |
| Side effects and issues | Mutations, partial operations, typed failures with category/operation/path/provider/error, conflicts, unsupported or unverifiable inputs; localized failure messages are diagnostic-only | Evaluator, intrinsic/property-function hooks, and recorder conflict handling | Existing operation outcome; observation-internal failures cannot replace evaluation exceptions |

## Reuse

The observer attaches to existing semantic owners instead of re-running operations. It
reuses PRE versions and source hashes, the active `IFileSystem`, directory/file caches,
`FileMatcher` results, property lookups, property-function return values, toolset/task
selection, and SDK cache results.

SDK resolver dependencies are not observed. The current SDK cache returns the stored
`SdkResult` for the same SDK name within its cache scope until that cache is cleared.
The recorded live-entry validator can bind a measurement to that exact entry only for
its owner-defined lifetime. It is not sufficient for correctness-capable evaluation
reuse, including process-local Server reuse. Normal build entries do not survive
submission or node-build teardown, while a retained `EvaluationContext` entry can remain
current across evaluations; neither lifetime supplies resolver dependency validity.

The implementation does not yet persist evaluated projects, validate reports, or perform
cache lookup/invalidation.

## Current Total Overhead

Measured on September 1, 2026 after rebasing the PR onto `main` at `c4d2a5f766`, using
.NET SDK `11.0.100-rc.1.26420.103` on a Windows Hyper-V VM. Only report documentation
changed after measurement.

### Synthetic evaluation benchmark

`EvaluationObservationBenchmark` used BenchmarkDotNet `MediumRun` with two launches,
10 warmups, 15 measured iterations, and 50 independent evaluations per child process.
Its child accumulator includes two jitting, ten warmup, and fifteen measured invocations
per launch, for 27 fresh child-process samples per launch. The primary time is the child
host's timed evaluation loop; BenchmarkDotNet's outer method also includes child-process
startup and semantic preflight.

The `±` values are pooled standard deviations of the 54 child-process samples per cell.
They are not standard errors or confidence intervals for the cell means or deltas.

| Scenario | Disabled mean ± SD, 50 evaluations | Enabled mean ± SD, 50 evaluations | Total time overhead | Added allocation per evaluation |
| --- | ---: | ---: | ---: | ---: |
| Typical | 251.891 ± 13.198 ms | 258.035 ± 9.156 ms | +6.144 ms / **+2.4%** | 15.9 KiB / +4.4% |
| Glob-heavy | 409.882 ± 14.486 ms | 439.238 ± 18.084 ms | +29.355 ms / **+7.2%** | 12.2 KiB / +0.8% |
| Ambient/SDK | 313.047 ± 11.691 ms | 350.710 ± 17.192 ms | +37.663 ms / **+12.0%** | 32.0 KiB / +7.3% |

BenchmarkDotNet's process-level ratios were 0.99, 1.03, and 1.06 respectively. Relative
to the child-loop deltas, non-loop time shifted by approximately -14 ms, -2 ms, and
+20 ms. That variation is treated as process-level noise, not corroboration; the Typical
process-level comparison is slightly negative. Both Baseline launches ran before both
Native launches for each scenario, so fixed-order drift remains a limitation and
statistical significance is not assessed.

### Orchard Core warm no-op build

`OrchardCoreNoOpBuildBenchmark` measured
`src/OrchardCore/OrchardCore/OrchardCore.csproj` at OrchardCore commit
`e3f8acb327a95f1dec6e75cefccaef2ad5eefb45` for `net10.0`. Both cells used the same
Release bootstrap SDK and differed only by
`MSBUILDPROTOTYPEEVALUATIONOBSERVATION`. Each independent run used three warmups and
12 measured external `dotnet build --no-restore` invocations with MSBuild Server and node
reuse disabled. In each run BenchmarkDotNet executed the disabled cell before the
enabled cell, so the samples are unpaired and susceptible to VM drift.

| Run | Observation disabled | Observation enabled | Delta |
| --- | ---: | ---: | ---: |
| 1 | 5.567 s | 5.791 s | +224 ms / +4.0% |
| 2 | 5.619 s | 5.781 s | +162 ms / +2.9% |
| 3 | 5.791 s | 5.761 s | -30 ms / -0.5% |
| **Aggregate means** | **5.659 s** | **5.778 s** | **+119 ms / +2.1%** |

The Hyper-V run-to-run range is wide and fixed-order drift remains a confounder. The
aggregate is a descriptive central estimate; no per-category or precise causal
attribution is inferred. Isolated synthetic evaluation loops measured 2.4-12.0% of
evaluation-loop time. The three-run Orchard aggregate measured 2.1% of total warm no-op
build wall time, with individual run deltas from -0.5% to +4.0%; the aggregate is not
treated as a statistically established across-run effect. These percentages have
different denominators and are not directly comparable.

Monitoring mode retained outliers. Median-based Orchard deltas were +202 ms, +173 ms,
and +130 ms across the three runs. Run 3's negative mean delta is caused by two retained
baseline stalls at 6.18 s and 7.31 s; its median delta is +130 ms.

See [evaluation-native-observation-timing-report.md](evaluation-native-observation-timing-report.md)
for exact commands, raw run summaries, and measurement limitations.
