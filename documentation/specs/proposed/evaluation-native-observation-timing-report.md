# Native Evaluation Observation Total Overhead Report

## Scope

This report measures only the total cost of enabling the native evaluation observation
layer. It does not attribute cost to individual observation categories. The synthetic
enabled cell also includes the benchmark callback that counts report records.

Measurements were collected on September 1, 2026 from PR head `77a2590fd4` with:

- Windows 11 `10.0.26200.9106` under Hyper-V;
- AMD EPYC 7763 virtual CPU, 8 cores and 16 logical processors;
- x64 .NET `11.0.0`;
- SDK `11.0.100-preview.7.26360.111`.

The synthetic benchmark toggles observation through its test-only
`EvaluationObservationNativeBridge`. The Orchard benchmark toggles only
`MSBUILDPROTOTYPEEVALUATIONOBSERVATION`.

## Synthetic Evaluation Benchmark

`EvaluationObservationBenchmark` runs a semantic-equivalence preflight, then times 50
independent evaluations in a child process. BenchmarkDotNet used `MediumRun`:

- two launches;
- ten warmup iterations;
- fifteen measured iterations.

The benchmark's child-metric accumulator includes every method invocation in each
launch: two jitting, ten warmup, and fifteen measured invocations, for 27 child-process
samples per launch. Every invocation starts a fresh child and times 50 evaluations. The
table averages the two launch summaries. It excludes child-process startup and semantic
preflight while retaining the complete evaluation and observation work.

The `±` values are pooled standard deviations of the 54 child-process samples per cell.

| Scenario | Disabled mean ± SD, 50 evaluations | Enabled mean ± SD, 50 evaluations | Delta | Added allocation per evaluation |
| --- | ---: | ---: | ---: | ---: |
| Typical | 256.473 ± 12.751 ms | 271.104 ± 13.129 ms | +14.632 ms / **+5.7%** | 15.0 KiB / +4.1% |
| Glob-heavy | 497.000 ± 36.151 ms | 518.490 ± 33.917 ms | +21.490 ms / **+4.3%** | 15.4 KiB / +1.0% |
| Ambient/SDK | 343.631 ± 21.307 ms | 375.711 ± 15.468 ms | +32.080 ms / **+9.3%** | 34.0 KiB / +7.7% |

The corresponding BenchmarkDotNet method ratios were 1.02, 1.03, and 1.04. Those ratios
include fixed child-process startup and preflight, so they are only a process-level
cross-check rather than the primary observation-overhead result.

BenchmarkDotNet executed both Baseline launches before both Native launches for each
scenario. Fixed-order drift therefore remains a limitation for the synthetic comparison
as well.

The table is backed by the local, uncommitted
`synthetic-bdn-project\MSBuild.Benchmarks.EvaluationObservationBenchmark-20260901-183841.log`
and its exported results. An earlier direct-DLL attempt under `synthetic-bdn` executed
zero benchmarks and is excluded.

## Orchard Core Warm No-Op Build

`OrchardCoreNoOpBuildBenchmark` measured:

- repository: `OrchardCMS/OrchardCore`;
- commit: `e3f8acb327a95f1dec6e75cefccaef2ad5eefb45`;
- project: `src/OrchardCore/OrchardCore/OrchardCore.csproj`;
- target framework: `net10.0`;
- configuration: `Release`;
- command under measurement: external `dotnet build --no-restore`;
- MSBuild Server and node reuse: disabled.

Both cells used the same locally built Release bootstrap SDK. The disabled cell removed
`MSBUILDPROTOTYPEEVALUATIONOBSERVATION`; the enabled cell set it to `1`. Each independent
run used one launch, three warmups, and twelve measured iterations. BenchmarkDotNet
executed all disabled iterations before all enabled iterations in each run, so the cells
are unpaired and susceptible to time-dependent VM drift.

The `±` values below are BenchmarkDotNet `Error` values: half-widths of its 99.9%
confidence intervals, not standard deviations.

| Run | Disabled mean ± error | Enabled mean ± error | Delta |
| --- | ---: | ---: | ---: |
| 1 | 5.218 s ± 0.171 s | 5.245 s ± 0.486 s | +27 ms / +0.5% |
| 2 | 4.970 s ± 0.122 s | 5.179 s ± 0.195 s | +209 ms / +4.2% |
| 3 | 5.004 s ± 0.119 s | 5.177 s ± 0.223 s | +173 ms / +3.5% |
| **Aggregate means** | **5.064 s** | **5.200 s** | **+136 ms / +2.7%** |

The three runs show substantial Hyper-V noise and fixed-order drift remains a confounder.
The individual deltas range from +0.5% to +4.2%, with run 1 carrying substantially wider
uncertainty. The equal-sample aggregate is a descriptive central estimate, not precise
causal attribution or a statistically established across-run effect.

## Reproduction

From a clean MSBuild clone, build the repository bootstrap as described in
[Bootstrap](../../wiki/Bootstrap.md):

```powershell
.\build.cmd -configuration Release -msbuildEngine dotnet -v quiet
```

The bootstrap directory is not configuration-scoped. Run this from a clean clone or
ensure the complete Release build finishes; a prior Debug build can otherwise leave a
mixed bootstrap.

Both Orchard cells must use:

```text
<msbuild-root>\artifacts\bin\bootstrap\core\dotnet.exe
```

Build the benchmark project:

```powershell
.\.dotnet\dotnet.exe msbuild .\src\MSBuild.Benchmarks\MSBuild.Benchmarks.csproj `
  -restore -v:q -p:Configuration=Release -p:TargetFramework=net11.0
```

Run the synthetic benchmark:

```powershell
.\.dotnet\dotnet.exe run -c Release -f net11.0 --no-build `
  --project .\src\MSBuild.Benchmarks\MSBuild.Benchmarks.csproj -- `
  --filter "*EvaluationObservationBenchmark*" --job medium `
  --artifacts "C:\benchmarks\observer-total\synthetic-bdn-project"
```

Create the Orchard configuration described in
[`OrchardCoreNoOpBuildBenchmark.md`](../../../src/MSBuild.Benchmarks/OrchardCoreNoOpBuildBenchmark.md),
using the same bootstrap `dotnet.exe` in both cells, then run:

```powershell
$env:MSBUILD_ORCHARD_NOOP_BUILD_CONFIG = "C:\benchmarks\orchard-noop-build.json"
$artifactNames = "orchard-release-bdn", "orchard-release-bdn-run2", "orchard-release-bdn-run3"
foreach ($artifactName in $artifactNames) {
  .\.dotnet\dotnet.exe run -c Release -f net11.0 --no-build `
    --project .\src\MSBuild.Benchmarks\MSBuild.Benchmarks.csproj -- `
    --filter "*OrchardCoreNoOpBuildBenchmark*" `
    --artifacts "C:\benchmarks\observer-total\$artifactName"
}
```

The local, uncommitted backing result directories for this report are
`orchard-release-bdn`, `orchard-release-bdn-run2`, and
`orchard-release-bdn-run3`.

## Interpretation

- Total observation overhead is scenario-dependent.
- Isolated synthetic evaluation loops measured **+4.3% to +9.3% of evaluation-loop
  time**.
- Orchard run deltas ranged from +27 ms to +209 ms. Their descriptive mean was +136 ms
  / +2.7% of total warm no-op build wall time, but it is not treated as a statistically
  established across-run effect.
- Synthetic allocation increased by approximately **15-34 KiB per evaluation**.
- These totals do not identify which observation category should be optimized.

The synthetic and Orchard percentages have different denominators and are not directly
comparable.

The measurements cover observation only. They exclude cache lookup, validation,
serialization, result materialization, concurrency effects, SDK resolver dependency
validation, and validation/materialization races.
