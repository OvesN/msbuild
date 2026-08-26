# Evaluation observer approach comparison

Status: preliminary prototype measurement

## Scope

This comparison measures dependency observation during project evaluation only.

It does not implement or measure:

- cache lookup or reuse;
- dependency validation;
- invalidation;
- watchers or journals;
- a reverse dependency index;
- persistence.

## Compared variants

| Variant | Description |
| --- | --- |
| Baseline | The hybrid binary with native observation explicitly disabled and no Detours. |
| Native | Per-evaluation `EvaluationObservationSession` and outer `RecordingFileSystem`. |
| Detours | The same evaluation host launched under BuildXL Detours, with native observation disabled. |
| Native + Detours | Both observers enabled simultaneously for diagnostic comparison. |

Detours wraps the benchmark evaluation host itself. The benchmark does not run targets,
restore, compilation, or an MSBuild worker node.

Detours is configured to report process-wide file access, matching the existing
experimental MSBuild integration. Event counts are filtered to the synthetic scenario
root, but CPU overhead includes interception of all process file access. The Detours
timing is therefore an upper-bound configuration, not a minimal root-scoped sandbox.

## Benchmark

The benchmark is implemented in `src/MSBuild.Benchmarks`.

Each isolated x64 .NET Framework host performs:

1. one unmeasured warm-up evaluation;
2. 50 measured evaluations using a new `ProjectCollection` each time;
3. internal evaluation timing and process-memory reporting.

Two synthetic scenarios are used:

| Scenario | Inputs |
| --- | --- |
| Typical | Imported props, positive and negative `Exists` probes, and a recursive glob over 200 files. |
| GlobHeavy | The same project shape with 2,000 glob members. |

The comparison used BenchmarkDotNet `Short` jobs. Each result below is the average of two
independent runs. The custom summary includes eight host invocations spanning BenchmarkDotNet
jitting, warm-up, and actual phases; it is not an actual-iterations-only confidence
interval.

The primary timing is reported by the evaluation host around the 50 evaluation loop.
BenchmarkDotNet process-launch time is not used to estimate evaluator overhead.

## Results

### Evaluation time

| Scenario | Baseline | Current native facade | Native overhead | Detours upper bound | Detours overhead | Simultaneous observers | Simultaneous overhead |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Typical | 177.2 ms | 182.3 ms | **+2.9%** | 220.2 ms | **+24.3%** | 227.2 ms | **+28.2%** |
| GlobHeavy | 318.2 ms | 322.1 ms | **+1.2%** | 368.3 ms | **+15.1%** | 387.2 ms | **+21.7%** |

These are batch times for 50 evaluations.

Across the two individual runs:

- native overhead was 1.2-4.7% for Typical and 1.1-1.4% for GlobHeavy;
- Detours overhead was 21.7-27.0% for Typical and 14.6-17.0% for GlobHeavy;
- simultaneous overhead was 21.5-35.4% for Typical and 19.2-24.2% for GlobHeavy.

Within-run standard deviations ranged from approximately 2.6 ms to 18 ms. The small
native deltas, especially GlobHeavy, are near the noise floor and should be treated as a
directional range rather than a precise budget.

The measured native implementation recorded probes and directory enumeration only:

```text
NativeFileReads = 0
NativeMetadataReads = 0
```

Root/import content does not yet flow through the native recorder. Native percentages are
therefore a lower bound for the current filesystem-facade prototype, not an estimate for
the complete observation design.

### Evaluation-host peak working set

| Scenario | Baseline | Native | Detoured host | Native + Detours host |
| --- | ---: | ---: | ---: | ---: |
| Typical | 49.2 MiB | 49.3 MiB | 52.7 MiB | 53.0 MiB |
| GlobHeavy | 52.2 MiB | 53.3 MiB | 55.9 MiB | 57.0 MiB |

Approximate deltas:

- native: +0.2% Typical, +2.1% GlobHeavy;
- Detours: +7.0% Typical, +7.1% GlobHeavy;
- simultaneous host: +7.7% Typical, +9.2% GlobHeavy.

The hybrid run retains an additional diagnostic native path set to calculate overlap.
Its retained managed memory is therefore not a production-native manifest estimate.

These values measure only the evaluation host process. They exclude the Detours broker,
listener, and BenchmarkDotNet process, so they must not be interpreted as total
approach-level memory.

### Coverage shape

For each 50-evaluation batch:

| Metric | Typical | GlobHeavy |
| --- | ---: | ---: |
| Native reports | 50 | 50 |
| Native path probes | 150 | 150 |
| Native enumeration records | 2,100 | 2,100 |
| Native semantic unique paths, sampled from one report | 224 | 2,024 |
| Detours raw accesses under the scenario root | 2,400 | 2,400 |
| Detours unique paths | 25 | 25 |
| Raw unique-path intersection | 23 | 23 |
| Native-only semantic paths | 201 | 2,001 |
| Detours-only paths | 2 | 2 |

The two Detours-only paths were:

- `benchmark.proj`;
- `imported.props`.

They confirm the known native gap: root and imported XML currently bypass
`RecordingFileSystem`.

The difference is expected:

- native observation records semantic enumeration members;
- Detours records the lower-level directory and file calls used to obtain those members;
- these Detours-only source paths are native interception gaps;
- native-only paths are not automatically Detours misses because a directory enumeration
  does not require one OS access per returned member.

The raw path intersection is not a completeness percentage. Native and Detours use
different dependency vocabularies.

## Interpretation

The prototype supports continuing the evaluator-native experiment:

- the current native filesystem facade has lower measured overhead in these synthetic
  scenarios;
- native records carry semantic information needed by a future invalidator;
- Detours finds accesses outside native interception and is useful as a coverage oracle;
- raw Detours events are not a substitute for glob, search, provider, or in-memory source
  semantics.

The measurements do not establish production readiness. The native prototype currently
misses root/import content and other categories documented in
`evaluation-observation-layer-design.md`.

## What this does not prove

- It does not measure a complete native observer.
- It does not measure content hashing, environment, Registry, SDK/toolset, or host inputs.
- It does not measure shared-`EvaluationContext` concurrency, where creating a
  per-evaluation context and `FileMatcher` may duplicate glob expansion work.
- It does not satisfy the design's real-workload acceptance gate.
- It does not establish a production overhead budget.
- It does not show that 23 of 25 raw paths means 92% semantic coverage.
- It does not measure total Detours memory.
- It does not compare cache validation or cache-hit behavior.

The scenarios are synthetic and intentionally narrow. A later performance gate requires
an SDK-style project, a property-function-heavy project, a large real solution,
concurrent graph evaluation, and repeated MSBuild Server requests.

## Measurement environment

- Windows 11 under Hyper-V;
- AMD EPYC 7763, x64;
- .NET Framework 4.8.1;
- BenchmarkDotNet 0.13.12;
- 50 evaluations per isolated host sample.

## Reproduction

Build the hybrid branch for x64 .NET Framework:

```powershell
C:\msbuild\.dotnet\dotnet.exe build `
  src\MSBuild.Benchmarks\MSBuild.Benchmarks.csproj `
  -c Release `
  -p:RuntimeOutputTargetFrameworks=net472 `
  -p:PlatformTarget=x64 `
  -p:Prefer32Bit=false `
  -p:CreateBootstrap=false
```

Run:

```powershell
artifacts\bin\MSBuild.Benchmarks\Release\net472\MSBuild.Benchmarks.exe `
  --filter "*EvaluationObservationBenchmark*" `
  --job Short `
  --platform X64
```

Use the `EVALUATION_OBSERVATION_SUMMARY` lines for internal evaluation and host-process
memory metrics. Ignore BenchmarkDotNet `Allocated` columns for observer memory; those
describe the benchmark orchestration process.

`NativeDetoursOverlap`, `NativeOnlyPaths`, and `DetoursOnlyPaths` are meaningful only for
the `NativeAndDetours` mode. In Detours-only mode the native set is absent by definition.
