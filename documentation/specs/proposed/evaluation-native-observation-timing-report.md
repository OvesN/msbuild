# Native Evaluation Observation Total Overhead Report

## Scope

This report measures only the total cost of enabling the native evaluation observation
layer. It does not attribute cost to individual observation categories. The synthetic
enabled cell also includes the benchmark callback that counts report records.

Measurements were collected on September 2, 2026 from PR head `89b4d9537b`, rebased
onto `main` at `c4d2a5f766`. Only report documentation changed after measurement.

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

The preflight includes an observation-enabled evaluation in both cells, so observation
code is JIT-compiled and initialized before timing. These are steady-state costs
amortized over 50 evaluations, not first-evaluation startup costs.

The `±` values are pooled standard deviations of the 54 child-process samples per cell.

| Scenario | Disabled mean ± SD, 50 evaluations | Enabled mean ± SD, 50 evaluations | Delta | Added allocation per evaluation |
| --- | ---: | ---: | ---: | ---: |
| Typical | 247.619 ± 5.683 ms | 266.671 ± 15.300 ms | +19.052 ms / **+7.7%** | ≈13 KiB / +3.6% |
| Glob-heavy | 428.452 ± 16.179 ms | 460.794 ± 19.860 ms | +32.342 ms / **+7.5%** | ≈17 KiB / +1.1% |
| Ambient/SDK | 339.768 ± 23.556 ms | 367.235 ± 20.861 ms | +27.467 ms / **+8.1%** | ≈32 KiB / +7.3% |

The corresponding BenchmarkDotNet method ratios were 1.02, 1.03, and 1.02. Relative to
the child-loop deltas, non-loop time shifted by approximately -7 ms, -1 ms, and -5 ms.
That variation is treated as process-level noise, not a decomposition of non-loop work.

The process-level ratios use measured iterations after BenchmarkDotNet outlier handling;
the child summaries include all 54 jitting, warmup, and measured invocations per cell.
The residuals are therefore approximate noise indicators, not a decomposition of
non-loop work. Statistical significance is not assessed because the cells also ran in
fixed order.

BenchmarkDotNet executed both Baseline launches before both Native launches for each
scenario. Fixed-order drift therefore remains a limitation for the synthetic comparison
as well.

The table is backed by the local, uncommitted
`synthetic-bdn-project\MSBuild.Benchmarks.EvaluationObservationBenchmark-20260902-020946.log`
and its exported results.

An immediately preceding same-host run on the pre-fix head measured 2.4%, 7.2%, and
12.0% for the same scenarios. Typical does not exercise the file-time fix, so its change
from 2.4% to 7.7% demonstrates run-to-run drift rather than a code effect. Treat the
synthetic results as an order-of-magnitude estimate, not a narrow 7.5-8.1% band.

Allocation uses `GC.GetTotalAllocatedBytes(precise: false)`. The same two runs varied
from approximately 13-16 KiB for Typical, 12-17 KiB for Glob-heavy, and remained about
32 KiB for Ambient/SDK.

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
| 1 | 5.769 s ± 0.095 s | 5.999 s ± 0.147 s | +230 ms / +4.0% |
| 2 | 5.735 s ± 0.101 s | 5.851 s ± 0.069 s | +116 ms / +2.0% |
| 3 | 6.020 s ± 0.771 s | 5.871 s ± 0.067 s | -149 ms / -2.5% |
| **Aggregate means** | **5.841 s** | **5.907 s** | **+66 ms / +1.1%** |

The three runs show substantial Hyper-V noise and fixed-order drift remains a confounder.
The individual deltas range from -2.5% to +4.0%, with run 3 carrying substantially wider
baseline uncertainty. The equal-sample aggregate is a descriptive central estimate, not
precise causal attribution or a statistically established across-run effect.

Monitoring mode retains outliers. Median-based deltas were +218 ms, +161 ms, and -13 ms.
The aggregate of the three cell medians was 5.772 s disabled and 5.894 s enabled:
+122 ms / +2.1%. Run 3's negative mean delta is driven by a single retained baseline
outlier: a 7.87 s sample.

The disabled run means span 5.567-6.020 s, a 453 ms range that is much larger than either
aggregate delta.

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

The expected output for this retained measurement is `11.0.100-rc.1.26420.103`.
OrchardCore's `global.json` requests `10.0.200` with `rollForward: latestMajor`, and the
bootstrap may contain multiple SDK-version directories, so verify the selected value
explicitly.

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
- The final synthetic run measured **+7.5% to +8.1% of evaluation-loop time**, while an
  immediately preceding same-host run measured **+2.4% to +12.0%**. Treat this as a
  rough single-digit-to-low-double-digit estimate.
- Orchard run mean deltas ranged from -149 ms to +230 ms. Their descriptive mean was
  +66 ms / +1.1% of total warm no-op build wall time; the median-based aggregate was
  +122 ms / +2.1%. Neither is treated as a statistically established across-run effect.
- Synthetic allocation increased by approximately **13-32 KiB per evaluation** in the
  final run, with material cross-run variation.
- These totals do not identify which observation category should be optimized.

The synthetic and Orchard percentages have different denominators and are not directly
comparable.

The synthetic `±` values are sample standard deviations, not standard errors or
confidence intervals for the cell means or deltas.

The measurements cover observation only. They exclude cache lookup, validation,
serialization, result materialization, concurrency effects, SDK resolver dependency
validation, and validation/materialization races.
