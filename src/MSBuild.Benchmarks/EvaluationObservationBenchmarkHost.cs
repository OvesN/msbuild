// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Execution;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationBenchmarkHost
{
    private const string HostSwitch = "--evaluation-observation-host";

    internal static bool TryRun(List<string> args, out int exitCode)
    {
        if (!args.Remove(HostSwitch))
        {
            exitCode = 0;
            return false;
        }

        string projectPath = TakeValue(args, "--project");
        int iterations = int.Parse(TakeValue(args, "--iterations"), CultureInfo.InvariantCulture);
        EvaluationObservationBenchmarkMode mode = (EvaluationObservationBenchmarkMode)Enum.Parse(
            typeof(EvaluationObservationBenchmarkMode),
            TakeValue(args, "--mode"),
            ignoreCase: false);
        string? resultFile = TryTakeValue(args, "--result-file");

        if (args.Count != 0)
        {
            throw new ArgumentException($"Unexpected benchmark host arguments: {string.Join(" ", args)}");
        }

        bool nativeEnabled = (mode & EvaluationObservationBenchmarkMode.Native) != 0;

        using (EvaluationObservationNativeBridge.Enable(nativeEnabled, metrics: null, collectPaths: false))
        {
            Evaluate(projectPath);
        }

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        long managedMemoryBefore = GC.GetTotalMemory(forceFullCollection: false);
        int gen0Before = GC.CollectionCount(0);
        int gen1Before = GC.CollectionCount(1);
        int gen2Before = GC.CollectionCount(2);
        EvaluationObservationNativeMetrics nativeMetrics = new();
        string projectDirectory = Path.GetDirectoryName(projectPath)!;

        _ = File.Exists(Path.Combine(projectDirectory, EvaluationObservationBenchmarkProtocol.MeasurementStartMarker));
        Stopwatch stopwatch = Stopwatch.StartNew();
        using (EvaluationObservationNativeBridge.Enable(
            nativeEnabled,
            nativeMetrics,
            collectPaths: (mode & EvaluationObservationBenchmarkMode.Detours) != 0))
        {
            for (int i = 0; i < iterations; i++)
            {
                Evaluate(projectPath);
            }
        }

        stopwatch.Stop();
        _ = File.Exists(Path.Combine(projectDirectory, EvaluationObservationBenchmarkProtocol.MeasurementStopMarker));

        long managedMemoryAfter = GC.GetTotalMemory(forceFullCollection: true);
        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        EvaluationObservationBenchmarkResult result = new()
        {
            EvaluationTicks = stopwatch.ElapsedTicks,
            RetainedManagedBytes = Math.Max(0, managedMemoryAfter - managedMemoryBefore),
            PrivateBytes = process.PrivateMemorySize64,
            PeakWorkingSetBytes = process.PeakWorkingSet64,
            Gen0Collections = GC.CollectionCount(0) - gen0Before,
            Gen1Collections = GC.CollectionCount(1) - gen1Before,
            Gen2Collections = GC.CollectionCount(2) - gen2Before,
            NativeReports = nativeMetrics.Reports,
            NativePathProbes = nativeMetrics.PathProbes,
            NativeEnumerations = nativeMetrics.Enumerations,
            NativeMetadataReads = nativeMetrics.MetadataReads,
            NativeFileReads = nativeMetrics.FileReads,
            NativeUniquePaths = nativeMetrics.UniquePathCount,
        };

        if (mode == EvaluationObservationBenchmarkMode.Baseline && result.NativeReports != 0)
        {
            throw new InvalidOperationException("The baseline benchmark unexpectedly produced native observation reports.");
        }

        string serializedResult = result.Serialize();
        Console.WriteLine(serializedResult);
        if (resultFile is not null)
        {
            File.WriteAllText(
                resultFile,
                string.Concat(serializedResult, Environment.NewLine, nativeMetrics.SerializePaths()));
        }

        exitCode = 0;
        return true;
    }

    private static void Evaluate(string projectPath)
    {
        using ProjectCollection collection = new();
        ProjectInstance project = ProjectInstance.FromFile(projectPath, new ProjectOptions
        {
            ProjectCollection = collection,
        });

        if (project.GetPropertyValue("RequestedProperty") != "ImportedValue" ||
            project.GetItems("Compile").Count == 0)
        {
            throw new InvalidOperationException("Evaluation benchmark project produced unexpected state.");
        }
    }

    private static string TakeValue(List<string> args, string name)
    {
        int index = args.IndexOf(name);
        if (index < 0 || index + 1 >= args.Count)
        {
            throw new ArgumentException($"Missing required benchmark host argument '{name}'.");
        }

        string value = args[index + 1];
        args.RemoveAt(index + 1);
        args.RemoveAt(index);
        return value;
    }

    private static string? TryTakeValue(List<string> args, string name)
    {
        int index = args.IndexOf(name);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Count)
        {
            throw new ArgumentException($"Missing value for benchmark host argument '{name}'.");
        }

        string value = args[index + 1];
        args.RemoveAt(index + 1);
        args.RemoveAt(index);
        return value;
    }
}
