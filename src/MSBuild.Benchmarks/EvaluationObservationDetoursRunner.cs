// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NETFRAMEWORK
using System.Collections;
using BuildXL.Processes;
using BuildXL.Utilities.Core;
using static BuildXL.Processes.FileAccessManifest;
using BuildXLFileAccessData = BuildXL.Processes.IDetoursEventListener.FileAccessData;
using BuildXLProcessData = BuildXL.Processes.IDetoursEventListener.ProcessData;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationDetoursRunner
{
    internal static EvaluationObservationBenchmarkResult Run(
        string executable,
        string arguments,
        string scenarioRoot)
    {
        string resultFile = Path.GetTempFileName();
        try
        {
            File.Delete(resultFile);
            var listener = new DetoursEventListener(scenarioRoot);
            listener.SetMessageHandlingFlags(
                MessageHandlingFlags.DebugMessageNotify |
                MessageHandlingFlags.FileAccessNotify |
                MessageHandlingFlags.ProcessDataNotify |
                MessageHandlingFlags.ProcessDetoursStatusNotify);
            SandboxedProcessInfo info = CreateProcessInfo(
                executable,
                string.Concat(arguments, " --result-file ", EvaluationObservationBenchmarkProcess.Quote(resultFile)),
                listener);

            using ISandboxedProcess sandboxedProcess =
                SandboxedProcessFactory.StartAsync(info, forceSandboxing: false).GetAwaiter().GetResult();
            _ = sandboxedProcess.GetResultAsync().GetAwaiter().GetResult();

            if (!File.Exists(resultFile))
            {
                throw new InvalidOperationException("The detoured benchmark host did not produce a result file.");
            }

            string resultContent = File.ReadAllText(resultFile);
            EvaluationObservationBenchmarkResult hostResult =
                EvaluationObservationBenchmarkResult.Parse(resultContent);
            HashSet<string> nativePaths = EvaluationObservationNativeMetrics.ParsePaths(resultContent);
            HashSet<string> detoursPaths = listener.GetUniquePaths();

            int overlap = 0;
            foreach (string path in nativePaths)
            {
                if (detoursPaths.Contains(path))
                {
                    overlap++;
                }
            }

            return new EvaluationObservationBenchmarkResult
            {
                EvaluationTicks = hostResult.EvaluationTicks,
                RetainedManagedBytes = hostResult.RetainedManagedBytes,
                PrivateBytes = hostResult.PrivateBytes,
                PeakWorkingSetBytes = hostResult.PeakWorkingSetBytes,
                Gen0Collections = hostResult.Gen0Collections,
                Gen1Collections = hostResult.Gen1Collections,
                Gen2Collections = hostResult.Gen2Collections,
                NativeReports = hostResult.NativeReports,
                NativePathProbes = hostResult.NativePathProbes,
                NativeEnumerations = hostResult.NativeEnumerations,
                NativeMetadataReads = hostResult.NativeMetadataReads,
                NativeFileReads = hostResult.NativeFileReads,
                NativeUniquePaths = nativePaths.Count,
                DetoursAccesses = listener.AccessCount,
                DetoursUniquePaths = detoursPaths.Count,
                NativeDetoursOverlap = overlap,
                NativeOnlyPaths = nativePaths.Count - overlap,
                DetoursOnlyPaths = detoursPaths.Count - overlap,
            };
        }
        finally
        {
            if (File.Exists(resultFile))
            {
                File.Delete(resultFile);
            }
        }
    }

    private static SandboxedProcessInfo CreateProcessInfo(
        string executable,
        string arguments,
        DetoursEventListener listener)
    {
        SandboxedProcessInfo info = new(
            fileStorage: null,
            fileName: executable,
            disableConHostSharing: false,
            detoursEventListener: listener,
            createJobObjectForCurrentProcess: false)
        {
            SandboxKind = SandboxKind.Default,
            PipDescription = "MSBuild evaluation observation benchmark",
            PipSemiStableHash = 0,
            Arguments = arguments,
            EnvironmentVariables = CreateEnvironmentVariables(),
            MaxLengthInMemory = 0,
        };

        info.FileAccessManifest.AddScope(
            AbsolutePath.Invalid,
            FileAccessPolicy.MaskNothing,
            FileAccessPolicy.AllowAll | FileAccessPolicy.ReportAccess);
        info.FileAccessManifest.MonitorChildProcesses = true;
        info.FileAccessManifest.IgnoreReparsePoints = true;
        info.FileAccessManifest.UseExtraThreadToDrainNtClose = false;
        info.FileAccessManifest.UseLargeNtClosePreallocatedList = true;
        info.FileAccessManifest.LogProcessData = true;
        info.FileAccessManifest.ReportProcessArgs = true;
        info.FileAccessManifest.NormalizeReadTimestamps = false;
        info.NestedProcessTerminationTimeout = TimeSpan.Zero;

        return info;
    }

    private static BuildParameters.IBuildParameters CreateEnvironmentVariables()
    {
        Dictionary<string, string> variables = new(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry variable in Environment.GetEnvironmentVariables())
        {
            variables[(string)variable.Key] = (string)variable.Value;
        }

        return BuildParameters.GetFactory().PopulateFromDictionary(variables);
    }

    private sealed class DetoursEventListener : IDetoursEventListener
    {
        private readonly string _scenarioRoot;
        private readonly string _startMarker;
        private readonly string _stopMarker;
        private readonly HashSet<string> _uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        private int _accessCount;
        private int _counting;

        internal DetoursEventListener(string scenarioRoot)
        {
            string fullRoot = Path.GetFullPath(scenarioRoot);
            _scenarioRoot = EnsureTrailingDirectorySeparator(fullRoot);
            _startMarker = Path.Combine(fullRoot, EvaluationObservationBenchmarkProtocol.MeasurementStartMarker);
            _stopMarker = Path.Combine(fullRoot, EvaluationObservationBenchmarkProtocol.MeasurementStopMarker);
        }

        internal int AccessCount => Volatile.Read(ref _accessCount);

        internal HashSet<string> GetUniquePaths()
        {
            lock (_uniquePaths)
            {
                return new HashSet<string>(_uniquePaths, StringComparer.OrdinalIgnoreCase);
            }
        }

        public override void HandleDebugMessage(DebugData debugData)
        {
        }

        public override void HandleFileAccess(BuildXLFileAccessData fileAccessData)
        {
            string? fullPath = GetFullPath(fileAccessData.Path);
            if (fullPath is null)
            {
                return;
            }

            if (string.Equals(fullPath, _startMarker, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _counting, 1);
                return;
            }

            if (string.Equals(fullPath, _stopMarker, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _counting, 0);
                return;
            }

            if (Volatile.Read(ref _counting) == 0 || !IsUnderScenarioRoot(fullPath))
            {
                return;
            }

            Interlocked.Increment(ref _accessCount);
            lock (_uniquePaths)
            {
                _uniquePaths.Add(fullPath);
            }
        }

        public override void HandleProcessData(BuildXLProcessData processData)
        {
        }

        public override void HandleProcessDetouringStatus(ProcessDetouringStatusData data)
        {
        }

        private bool IsUnderScenarioRoot(string fullPath)
        {
            return fullPath.StartsWith(_scenarioRoot, StringComparison.OrdinalIgnoreCase);
        }

        private static string? GetFullPath(string path)
        {
            try
            {
                return Path.GetFullPath(path);
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string EnsureTrailingDirectorySeparator(string path)
        {
            return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? path
                : string.Concat(path, Path.DirectorySeparatorChar);
        }
    }
}
#else
namespace MSBuild.Benchmarks;

internal static class EvaluationObservationDetoursRunner
{
    internal static EvaluationObservationBenchmarkResult Run(
        string executable,
        string arguments,
        string scenarioRoot)
    {
        throw new PlatformNotSupportedException("The Detours observer benchmark requires .NET Framework on Windows.");
    }
}
#endif
