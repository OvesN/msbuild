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

Measured with the existing [Orchard Core command-line evaluation benchmark](https://github.com/dotnet/msbuild/pull/14634), normalized per evaluation.

| Query | Baseline | Observer | Time overhead | Allocation |
| --- | ---: | ---: | ---: | ---: |
| `GetProperty` | 55.08 ms | 60.67 ms | +10.1% | 3.99 MB -> 4.32 MB |
| `GetItems` | 64.19 ms | 67.83 ms | +5.7% | 4.56 MB -> 4.89 MB |

### Observer Allocation Attribution

Inclusive scopes; rows overlap and are not additive.

| Activity | `GetProperty` | `GetItems` |
| --- | ---: | ---: |
| Report finalization | 103 KB | 105 KB |
| Project-source records | 70 KB | 74 KB |
| Environment records | 62 KB | 64 KB |
| Filesystem records | 31 KB | 40 KB |
| Initial request snapshot | 11 KB | 13 KB |
| Property-function records | 7.6 KB | 7.7 KB |

## Improvement Areas

- Defer report array creation and sorting until a cache entry is stored.
- Reuse normalized project-source identities and source records.
- Compact environment and filesystem records.
- Lazily create request and category data.
- Define the complete SDK request key and cache lifetime.
