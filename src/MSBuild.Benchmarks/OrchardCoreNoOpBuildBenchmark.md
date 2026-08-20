# OrchardCore No-Op Build Benchmark

`OrchardCoreNoOpBuildBenchmark` compares two external SDK configurations by repeatedly
running a warm no-op `dotnet build`.

Each cell restores and builds the configured OrchardCore project or solution before
measurement. Measured commands use `--no-restore` and preserve the prepared outputs.
MSBuild Server and node reuse are disabled so both cells measure equivalent external
processes.

## Configuration

Set `MSBUILD_ORCHARD_NOOP_BUILD_CONFIG` to a JSON file:

```json
{
  "orchardCoreRoot": "C:\\OrchardCore",
  "buildPath": "src\\OrchardCore\\OrchardCore\\OrchardCore.csproj",
  "configuration": "Release",
  "targetFramework": "net10.0",
  "before": {
    "dotnetPath": "C:\\sdk\\dotnet.exe",
    "environmentVariables": {
      "MSBUILDPROTOTYPEEVALUATIONOBSERVATION": null
    },
    "restoreArguments": [
      "-p:RestoreUseStaticGraphEvaluation=false",
      "--ignore-failed-sources"
    ],
    "buildArguments": [
      "-p:NuGetAudit=false"
    ]
  },
  "after": {
    "dotnetPath": "C:\\sdk\\dotnet.exe",
    "environmentVariables": {
      "MSBUILDPROTOTYPEEVALUATIONOBSERVATION": "1"
    },
    "restoreArguments": [
      "-p:RestoreUseStaticGraphEvaluation=false",
      "--ignore-failed-sources"
    ],
    "buildArguments": [
      "-p:NuGetAudit=false"
    ]
  }
}
```

Use the same `dotnetPath` for both cells when measuring an environment-controlled feature.

## Run

```powershell
$env:MSBUILD_ORCHARD_NOOP_BUILD_CONFIG = "C:\benchmarks\orchard-noop-build.json"
dotnet run -c Release -f net11.0 --project src\MSBuild.Benchmarks -- `
  --filter "*OrchardCoreNoOpBuildBenchmark*"
```
