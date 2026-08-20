# Native Evaluation Observation Report

## Coverage and Tracking

- **Request configuration**
  - Tracks global properties, load settings, runtime/OS/culture, feature switches, escape hatches, and provider identities.
  - Captured once when evaluation starts.

- **Project and import content**
  - Tracks root, imported, linked, generated, and in-memory project sources.
  - Disk XML is SHA-256 hashed while read; PRE/link versions are reused when authoritative.

- **Filesystem**
  - Tracks positive and negative probes, file reads, timestamps, lengths, and providers.
  - Recorded through the filesystem wrapper and direct hooks for paths that bypass it.

- **Globs, enumerations, and searches**
  - Tracks patterns, excludes, ordered results/candidates, selected paths, misses, and failures.
  - Recorded at `FileMatcher`, `EngineFileUtilities`, and `FileUtilities` semantic seams using counts and 128-bit fingerprints.

- **Environment**
  - Tracks imported variables, missing imported variables, SDK-injected variables, and live process reads.
  - Property reads are captured by `PropertyTrackingEvaluatorDataWrapper`; calls such as `Environment.GetEnvironmentVariable` are captured by property-function interception.

- **Registry**
  - Tracks registry expressions, intrinsic registry functions, property-function calls, requests, results, and failures.
  - Recorded in the evaluator expansion and intrinsic-function paths.

- **Property functions**
  - Tracks filesystem, environment, Registry, ambient, volatile, and side-effecting calls.
  - Recorded after execution in `Expander`; pure calls are omitted and unknown/unsafe calls fail closed.

- **SDK resolution**
  - Tracks the complete SDK request record, cache hit/miss, and returned `SdkResult`.
  - Resolver dependencies are not observed; the SDK result cache owns reuse until it is cleared.

- **Toolsets, tasks, providers, and caches**
  - Tracks selected toolsets, effective `UsingTask` registrations, provider identity, and cache mode.
  - Recorded at evaluator initialization, task registration, and cache/provider boundaries.

- **Failures and side effects**
  - Tracks partial operations, exceptions, conflicting results, volatile values, and mutations.
  - Missing, opaque, custom, or unversioned inputs are marked incomplete or unsupported.

## Reuse

The implementation reuses PRE versions/cache data, filesystem abstractions, `FileMatcher`, property tracking, and SDK resolver/cache seams. It does not yet implement persistent evaluated-result storage or invalidation.

SDK resolver dependencies are not observed. The current SDK cache returns the stored `SdkResult` for the same SDK name within its cache scope until that cache is cleared.

## Orchard Core Measurements

Measured with `OrchardCoreNoOpBuildBenchmark` against
`src/OrchardCore/OrchardCore/OrchardCore.csproj` (`net10.0`). Restore and the initial
Release build run before measurement; each sample is an external
`dotnet build --no-restore` with unchanged inputs.

Three independent 12-iteration runs measured **+3.7%, +3.8%, and +5.2%**. Their
aggregate means are 4.910 s baseline and 5.107 s with observation:
**approximately +4.0% / +197 ms**. Per-run median deltas ranged from 180 ms to 251 ms.

Removing report sorting reduced allocation substantially, but its wall-time effect was
within VM noise.

### Observer Allocation Attribution

Measured with temporary child-process counters across 6 MSBuild processes and 13 project
evaluations. Scopes are inclusive, overlap, and are not additive.

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
evaluation (-83%, -215 KB).

See [evaluation-native-observation-timing-report.md](evaluation-native-observation-timing-report.md)
for per-activity CPU-time attribution and optimization priorities.

## Improvement Areas

- Reduce allocation in property lookup and environment records.
- Reuse normalized project-source identities and source records.
- Compact task-registration, property-function, and filesystem records.
- Defer report array creation until a cache entry is stored.
- Lazily create request and category data.
- Define the complete SDK request key and cache lifetime.
