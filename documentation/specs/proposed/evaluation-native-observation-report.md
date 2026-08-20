# Native Evaluation Observation Report

## Coverage

- Request options and global properties
- Project and import content
- File probes, reads, metadata, globs, and searches
- Environment, Registry, and property functions
- SDK request keys/results, toolset resolution, and task registration
- Provider/cache identity, failures, and side effects

## Mechanism

- Evaluator-native hooks produce one typed report per evaluation.
- File content uses SHA-256; ordered results use schema-versioned 128-bit fingerprints.
- Missing, opaque, custom, or unversioned inputs are marked incomplete or unsupported.

## Reuse

The implementation reuses PRE versions/cache data, filesystem abstractions, `FileMatcher`, property tracking, and SDK resolver/cache seams. It does not yet implement persistent evaluated-result storage or invalidation.

SDK resolver dependencies are not observed. The SDK cache returns the stored `SdkResult` for the same complete resolver request key until that cache is cleared.

## Measurements

| Scenario | Time overhead | Allocation |
| --- | ---: | ---: |
| Typical | +4.39% / 0.21 ms | +22.4 KB |
| Glob-heavy | +4.04% / 0.30 ms | +22.7 KB |
| Ambient/SDK | +7.74% / 0.47 ms | +40.1 KB |

## Improvement Areas

- Lazily create request, report, and category data.
- Reuse static request values and existing source hashes.
- Define the complete SDK request key and cache lifetime.
- Reduce property-function payload serialization.
- Prioritize the Ambient/SDK scenario.
