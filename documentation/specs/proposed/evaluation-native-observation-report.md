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

## Measurements

| Scenario | Time overhead | Allocation |
| --- | ---: | ---: |
| Typical | +4.39% / 0.21 ms | +22.4 KB |
| Glob-heavy | +4.04% / 0.30 ms | +22.7 KB |
| Ambient/SDK | +5.93% / 0.37 ms | +40.5 KB |

An identical 15x1000 A/B run reduced Ambient/SDK from +7.58% / 0.47 ms / 42.1 KB to +5.93% / 0.37 ms / 40.5 KB.
Typical and Glob-heavy use the earlier paired 9x500 run.

## Improvement Areas

- Lazily create request, report, and category data.
- Reuse static request values and existing source hashes.
- Define the complete SDK request key and cache lifetime.
- Reduce property-function payload serialization.
- Prioritize the Ambient/SDK scenario.
