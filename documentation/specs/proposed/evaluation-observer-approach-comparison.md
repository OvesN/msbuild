# Evaluation observer approach comparison

Status: prototype measurement

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
| Baseline | Evaluation with no observer. |
| Native | Per-evaluation `EvaluationObservationSession` and outer `RecordingFileSystem`. |
| Detours | The same evaluation host launched under BuildXL Detours, with native observation disabled. |
| Native + Detours | Both observers enabled for coverage comparison. |

Detours wraps the benchmark evaluation host itself. The benchmark does not run targets,
restore, compilation, or an MSBuild worker node.

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
independent runs, each containing nine host samples.

The primary timing is reported by the evaluation host around the 50 evaluation loop.
BenchmarkDotNet process-launch time is not used to estimate evaluator overhead.

## Results

### Evaluation time

| Scenario | Baseline | Native | Native overhead | Detours | Detours overhead | Native + Detours | Combined overhead |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| Typical | 178.1 ms | 185.1 ms | **+4.0%** | 225.6 ms | **+26.7%** | 235.2 ms | **+32.1%** |
| GlobHeavy | 333.4 ms | 340.3 ms | **+2.1%** | 383.5 ms | **+15.0%** | 399.7 ms | **+19.9%** |

These are batch times for 50 evaluations.

Across the two individual runs:

- native overhead was 2.9-5.1% for Typical and 1.3-2.9% for GlobHeavy;
- Detours overhead was 24.2-29.3% for Typical and 12.5-17.6% for GlobHeavy;
- combined overhead was 28.1-36.3% for Typical and 18.0-21.8% for GlobHeavy.

### Peak working set

| Scenario | Baseline | Native | Detours | Native + Detours |
| --- | ---: | ---: | ---: | ---: |
| Typical | 49.2 MiB | 49.4 MiB | 52.8 MiB | 53.1 MiB |
| GlobHeavy | 52.2 MiB | 53.4 MiB | 55.9 MiB | 57.2 MiB |

Approximate deltas:

- native: +0.4% Typical, +2.4% GlobHeavy;
- Detours: +7.4% Typical, +7.1% GlobHeavy;
- combined: +7.9% Typical, +9.5% GlobHeavy.

The hybrid run retains an additional diagnostic native path set to calculate overlap.
Its retained managed memory is therefore not a production-native manifest estimate.

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
| Unique-path overlap | 23 | 23 |
| Native-only semantic paths | 201 | 2,001 |
| Detours-only paths | 2 | 2 |

The difference is expected:

- native observation records semantic enumeration members;
- Detours records the lower-level directory and file calls used to obtain those members;
- Detours-only paths expose native interception gaps;
- native-only paths are not automatically Detours misses because a directory enumeration
  does not require one OS access per returned member.

## Interpretation

The prototype supports the selected architecture:

- evaluator-native observation has substantially lower measured overhead;
- native records carry semantic information needed by a future invalidator;
- Detours finds accesses outside native interception and is useful as a coverage oracle;
- raw Detours events are not a substitute for glob, search, provider, or in-memory source
  semantics.

The measurements do not establish production readiness. The native prototype currently
misses root/import content and other categories documented in
`evaluation-observation-layer-design.md`.

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

Use the `EVALUATION_OBSERVATION_SUMMARY` lines for internal evaluation and process-memory
metrics.
