# Native Evaluation Observation Report

BuildXL differential validation is documented in
[evaluation-native-observation-buildxl-validation.md](evaluation-native-observation-buildxl-validation.md).
The adversarial follow-up and confirmed counterexamples are documented in
[evaluation-native-observation-buildxl-adversarial-report.md](evaluation-native-observation-buildxl-adversarial-report.md).
The post-fix six-scenario comparison and current overhead snapshot are documented in
[evaluation-native-observation-buildxl-post-fix-report.md](evaluation-native-observation-buildxl-post-fix-report.md).

## Session lifecycle

1. File-based `Project` and `ProjectInstance` loads pass a source capture into root
   acquisition. A malformed root produces a minimal failed report before `Evaluator`
   exists; its request category is incomplete.
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
| File probes and reads | Canonical absolute positive/negative file and directory probes, Windows extended-path alias normalization, content hashes, failures, provider identity; raw `Directory.Parse.config` bytes and parse outcome | `RecordingFileSystem` over the active evaluation `IFileSystem`; direct evaluator/intrinsic/parser hooks only where no filesystem call exists | Existing filesystem or parser result, effective base directory, and provider; no validation-time reprobe |
| Metadata | Filesystem times, lengths, attributes, returned value/failure | `RecordingFileSystem`, time-based `ItemSpecModifiers`, and classified property functions | Existing metadata result used by evaluation; lexical path calculations are ambient path-resolution records |
| Enumerations and globs | Request pattern, recursion, complete `EnumerationOptions` identity where applicable, excludes, completion, count, ordered result fingerprint, optional retained details | `RecordingFileSystem`, classified property functions, and `EngineFileUtilities`/`FileMatcher` semantic completion | The enumeration/glob result already produced for evaluation; no second enumeration |
| Searches | Ordered candidates, candidate fingerprint, selected path or miss | `FileUtilities` through `IEvaluationInputObserver`, plus evaluator import/toolset searches | Existing candidate sequence and selected result |
| Environment | Imported, missing imported, SDK-injected, and live process values | `PropertyTrackingEvaluatorDataWrapper`; `Environment` property-function interception | Existing property lookup or property-function result |
| Registry and ambient inputs | Registry requests/results/failures, culture/time/runtime/tool-location values, lexical item/path-member results, `MakeRelative` resolved base/path, volatile values | `ItemSpecModifiers`, intrinsic expansion, path-normalization seams, and post-execution property-function interception | Actual input/base/instance and returned value consumed by expansion |
| Property functions | Receiver/member, classified effect, arguments/result or failure | `Expander.Function` after dispatch | Existing invocation result; known-pure calls are omitted |
| SDK resolution | Complete resolver request, returned `SdkResult`, hit/miss, and cache owner/scope/key/epoch/entry identity with a live-entry validator | `CachingSdkResolverService` and out-of-proc SDK service | Existing SDK cache result while that exact entry remains live; resolver-internal files are intentionally opaque |
| Tasks and toolsets | Effective `UsingTask` registration and selected toolset/provider identity | `TaskRegistry` and evaluator initialization | Resolved task registration and toolset already selected |
| Side effects and issues | Mutations, partial operations, typed failures with category/operation/path/provider/error, conflicts, unsupported or unverifiable inputs; localized failure messages are diagnostic-only | Evaluator, intrinsic/property-function hooks, and recorder conflict handling | Existing operation outcome; observation-internal failures cannot replace evaluation exceptions |

## Reuse

The observer attaches to existing semantic owners instead of re-running operations. It
reuses PRE versions and source hashes, the active `IFileSystem`, directory/file caches,
`FileMatcher` results, property lookups, property-function return values, toolset/task
selection, and SDK cache results.

SDK resolver dependencies are not observed. The current SDK cache returns the stored
`SdkResult` for the same SDK name within its cache scope until that cache is cleared.

The implementation does not yet persist evaluated projects, validate reports, or perform
cache lookup/invalidation.

## Orchard Core Measurements

Measured with `OrchardCoreNoOpBuildBenchmark` against
`src/OrchardCore/OrchardCore/OrchardCore.csproj` (`net10.0`). Restore and the initial
Release build run before measurement; each sample is an external
`dotnet build --no-restore` with unchanged inputs.

Before zero-copy report finalization, three independent 12-iteration runs measured
**+3.7%, +3.8%, and +5.2%**. Their aggregate means were 4.910 s baseline and
5.107 s with observation: **approximately +4.0% / +197 ms**.

After transferring completed collections directly to the report, two independent
12-iteration runs measured:

| Run | Observation disabled | Observation enabled | Delta |
| --- | ---: | ---: | ---: |
| 1 | 4.859 s | 4.992 s | +133 ms / +2.7% |
| 2 | 4.889 s | 4.980 s | +91 ms / +1.9% |
| **Aggregate means** | **4.874 s** | **4.986 s** | **+112 ms / +2.3%** |

The aggregate delta is **85 ms / 43% lower**, while the stronger signal is that the
post-change range (**+1.9% to +2.7%**) does not overlap the earlier range
(**+3.7% to +5.2%**). These are unpaired Hyper-V VM runs, not exact attribution.

Caching process-constant request values and reading `Traits.Instance` once per evaluation
then measured:

| Run | Observation disabled | Observation enabled | Delta |
| --- | ---: | ---: | ---: |
| 1 | 5.018 s | 5.141 s | +123 ms / +2.4% |
| 2 | 4.882 s | 4.956 s | +74 ms / +1.5% |
| **Aggregate means** | **4.950 s** | **5.049 s** | **+99 ms / +2.0%** |

The single fresh pre-change comparator measured 5.008 s -> 5.149 s
(**+141 ms / +2.8%**). The post-change range overlaps the earlier post-zero-copy range,
and the differences are below the VM noise floor. No wall-time improvement is claimed.

### Observer Allocation Attribution

The earlier per-activity allocation telemetry was measured before zero-copy finalization.
Scopes are inclusive, overlap, and are not additive.

| Activity | Per evaluation |
| --- | ---: |
| Property lookup | 144 KB |
| Environment records | 112 KB |
| Task registration | 93 KB |
| Project-source records | 87 KB |
| Property-function records | 84 KB |
| Filesystem records | 75 KB |
| Report finalization | 44 KB |

Removing sorting reduced report-finalization allocation from 259 KB to 44 KB per
evaluation (-83%, -215 KB). Zero-copy finalization then removed the remaining collection
array projections.

The separate synthetic child-process benchmark compares observation disabled and enabled
across 50 evaluations:

| Scenario | Disabled allocation | Enabled allocation | Added per evaluation | Post-GC retained delta |
| --- | ---: | ---: | ---: | ---: |
| Typical | 17.67 MB | 18.47 MB | 15.7 KiB | 0.0 KiB/process |
| Glob-heavy | 78.95 MB | 79.66 MB | 14.0 KiB | 0.3 KiB/process |
| Ambient/SDK | 21.37 MB | 22.73 MB | 26.7 KiB | -0.3 KiB/process |

Per-evaluation deltas use the unrounded byte counters; this table is not directly
comparable with the earlier inclusive Orchard activity scopes.
Compared with the earlier run, Typical and Ambient/SDK decreased while Glob-heavy
increased. These unpaired differences and the near-zero retained deltas are treated as
run-to-run noise, not attributed to the request-snapshot change.

Reports are consumed and discarded in this benchmark. The small post-GC deltas confirm
bounded process retention and are consistent with the regression test that completed
sessions detach their populated collections.

See [evaluation-native-observation-timing-report.md](evaluation-native-observation-timing-report.md)
for per-activity CPU-time attribution, self-overhead controls, rejected noisy
subtractive measurements, and optimization priorities.

## Improvement Areas

- Reduce allocation in property lookup and environment records.
- Reuse normalized project-source identities and source records.
- Compact task-registration, property-function, and filesystem records.
- Materialize compact arrays only when a report is persisted in a cache entry.
- Lazily create request and category data.
- Define the complete SDK request key and cache lifetime.
