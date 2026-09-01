# Native Evaluation Observation Total Overhead Report

## Scope

This report measures only the total cost of enabling the native evaluation observation
layer. It does not attribute cost to individual observation categories. The synthetic
enabled cell also includes the benchmark callback that counts report records.

Measurements were collected on September 1, 2026 after rebasing the PR onto `main` at
`c4d2a5f766`. Only report documentation changed after measurement.

- Windows 11 `10.0.26200.9106` under Hyper-V;
- AMD EPYC 7763 virtual CPU, 8 cores and 16 logical processors;
- x64 .NET `11.0.0`;
- SDK `11.0.100-rc.1.26420.103`;
- BenchmarkDotNet `0.16.0-preview.1`.

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
| Typical | 251.891 ± 13.198 ms | 258.035 ± 9.156 ms | +6.144 ms / **+2.4%** | 15.9 KiB / +4.4% |
| Glob-heavy | 409.882 ± 14.486 ms | 439.238 ± 18.084 ms | +29.355 ms / **+7.2%** | 12.2 KiB / +0.8% |
| Ambient/SDK | 313.047 ± 11.691 ms | 350.710 ± 17.192 ms | +37.663 ms / **+12.0%** | 32.0 KiB / +7.3% |

The corresponding BenchmarkDotNet method ratios were 0.99, 1.03, and 1.06. Relative to
the child-loop deltas, non-loop time shifted by approximately -14 ms, -2 ms, and +20 ms.
That variation is treated as process-level noise, not corroboration. In particular, the
Typical process-level comparison is slightly negative and does not corroborate its
+2.4% child-loop delta.

The process-level ratios use measured iterations after BenchmarkDotNet outlier handling;
the child summaries include all 54 jitting, warmup, and measured invocations per cell.
The residuals are therefore approximate noise indicators, not a decomposition of
non-loop work. Statistical significance is not assessed because the cells also ran in
fixed order.

BenchmarkDotNet executed both Baseline launches before both Native launches for each
scenario. Fixed-order drift therefore remains a limitation for the synthetic comparison
as well.

The table is backed by the local, uncommitted
`synthetic-bdn-project\MSBuild.Benchmarks.EvaluationObservationBenchmark-20260901-210841.log`
and its exported results.

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
| 1 | 5.567 s ± 0.063 s | 5.791 s ± 0.176 s | +224 ms / +4.0% |
| 2 | 5.619 s ± 0.073 s | 5.781 s ± 0.082 s | +162 ms / +2.9% |
| 3 | 5.791 s ± 0.655 s | 5.761 s ± 0.070 s | -30 ms / -0.5% |
| **Aggregate means** | **5.659 s** | **5.778 s** | **+119 ms / +2.1%** |

The three runs show substantial Hyper-V noise and fixed-order drift remains a confounder.
The individual deltas range from -0.5% to +4.0%, with run 3 carrying substantially wider
baseline uncertainty. The equal-sample aggregate is a descriptive central estimate, not
precise causal attribution or a statistically established across-run effect.

Monitoring mode retains outliers. Median-based deltas were +202 ms, +173 ms, and
+130 ms across the three runs, all positive. Run 3's negative mean delta is caused by two
retained baseline stalls at 6.18 s and 7.31 s; its median delta is +130 ms.

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

From the OrchardCore root, verify that this bootstrap selects the freshly built SDK:

```powershell
<msbuild-root>\artifacts\bin\bootstrap\core\dotnet.exe --version
```

The expected output for this measurement is `11.0.100-rc.1.26420.103`, matching the
MSBuild repository's `global.json`.

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

Create the Orchard configuration below, using the same bootstrap `dotnet.exe` in both
cells:

```powershell
git -C C:\OrchardCore switch --detach e3f8acb327a95f1dec6e75cefccaef2ad5eefb45
```

```json
{
  "orchardCoreRoot": "C:\\OrchardCore",
  "buildPath": "src\\OrchardCore\\OrchardCore\\OrchardCore.csproj",
  "configuration": "Release",
  "targetFramework": "net10.0",
  "before": {
    "dotnetPath": "C:\\path\\to\\msbuild\\artifacts\\bin\\bootstrap\\core\\dotnet.exe",
    "workingDirectory": "C:\\OrchardCore",
    "environmentVariables": {
      "MSBUILDPROTOTYPEEVALUATIONOBSERVATION": null
    },
    "restoreArguments": [
      "-p:RestoreUseStaticGraphEvaluation=false",
      "--ignore-failed-sources"
    ],
    "buildArguments": [
      "-p:NuGetAudit=false"
    ],
    "timeoutMinutes": 30
  },
  "after": {
    "dotnetPath": "C:\\path\\to\\msbuild\\artifacts\\bin\\bootstrap\\core\\dotnet.exe",
    "workingDirectory": "C:\\OrchardCore",
    "environmentVariables": {
      "MSBUILDPROTOTYPEEVALUATIONOBSERVATION": "1"
    },
    "restoreArguments": [
      "-p:RestoreUseStaticGraphEvaluation=false",
      "--ignore-failed-sources"
    ],
    "buildArguments": [
      "-p:NuGetAudit=false"
    ],
    "timeoutMinutes": 30
  }
}
```

The schema is also documented in
[`OrchardCoreNoOpBuildBenchmark.md`](../../../src/MSBuild.Benchmarks/OrchardCoreNoOpBuildBenchmark.md).
Then run:

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
- Isolated synthetic evaluation loops measured **+2.4% to +12.0% of evaluation-loop
  time**.
- Orchard run deltas ranged from -30 ms to +224 ms. Their descriptive mean was +119 ms
  / +2.1% of total warm no-op build wall time, but it is not treated as a statistically
  established across-run effect.
- Synthetic allocation increased by approximately **12-32 KiB per evaluation**.
- These totals do not identify which observation category should be optimized.

The synthetic and Orchard percentages have different denominators and are not directly
comparable.

The synthetic `±` values are sample standard deviations, not standard errors or
confidence intervals for the cell means or deltas.

The measurements cover observation only. They exclude cache lookup, validation,
serialization, result materialization, concurrency effects, SDK resolver dependency
validation, and validation/materialization races.
