// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NETFRAMEWORK
using System.Collections;
using System.Text;
using BuildXL.Processes;
using BuildXL.Utilities.Core;
using static BuildXL.Processes.FileAccessManifest;
using BuildXLFileAccessData = BuildXL.Processes.IDetoursEventListener.FileAccessData;
using BuildXLProcessData = BuildXL.Processes.IDetoursEventListener.ProcessData;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationDetoursHost
{
    internal const string HostSwitch = "--evaluation-observation-detours-host";
    internal const string DetoursOnlyPathPrefix = "EVALUATION_OBSERVATION_DETOURS_ONLY_PATH|";

    internal static bool TryRun(List<string> args, out int exitCode)
    {
        if (!args.Remove(HostSwitch))
        {
            exitCode = 0;
            return false;
        }

        string targetExecutable = Decode(TakeValue(args, "--target-executable"));
        string targetArguments = Decode(TakeValue(args, "--target-arguments"));
        string scenarioRoot = Decode(TakeValue(args, "--scenario-root"));
        string resultFile = Decode(TakeValue(args, "--result-file"));

        if (args.Count != 0)
        {
            throw new ArgumentException($"Unexpected Detours host arguments: {string.Join(" ", args)}");
        }

        exitCode = Run(targetExecutable, targetArguments, scenarioRoot, resultFile);
        return true;
    }

    private static int Run(
        string targetExecutable,
        string targetArguments,
        string scenarioRoot,
        string resultFile)
    {
        string hostResultFile = Path.GetTempFileName();
        try
        {
            File.Delete(hostResultFile);
            var listener = new DetoursEventListener(scenarioRoot);
            listener.SetMessageHandlingFlags(
                MessageHandlingFlags.DebugMessageNotify |
                MessageHandlingFlags.FileAccessNotify |
                MessageHandlingFlags.ProcessDataNotify |
                MessageHandlingFlags.ProcessDetoursStatusNotify);

            SandboxedProcessInfo info = CreateProcessInfo(
                targetExecutable,
                string.Concat(
                    targetArguments,
                    " --result-file ",
                    EvaluationObservationBenchmarkProcess.Quote(hostResultFile)),
                listener);

            using ISandboxedProcess sandboxedProcess =
                SandboxedProcessFactory.StartAsync(info, forceSandboxing: false).GetAwaiter().GetResult();
            SandboxedProcessResult processResult = sandboxedProcess.GetResultAsync().GetAwaiter().GetResult();

            ValidateResult(processResult, listener);
            if (!File.Exists(hostResultFile))
            {
                throw new InvalidOperationException("The detoured evaluation host did not produce a result file.");
            }

            string resultContent = File.ReadAllText(hostResultFile);
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

            EvaluationObservationBenchmarkResult result = new()
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

            StringBuilder serializedResult = new(result.Serialize());
            if (nativePaths.Count != 0)
            {
                foreach (string path in detoursPaths.OrderBy(static path => path, StringComparer.OrdinalIgnoreCase))
                {
                    if (nativePaths.Contains(path))
                    {
                        continue;
                    }

                    serializedResult.AppendLine();
                    serializedResult.Append(DetoursOnlyPathPrefix);
                    serializedResult.Append(EvaluationObservationDetoursRunner.Encode(path));
                }
            }

            File.WriteAllText(resultFile, serializedResult.ToString());
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
        finally
        {
            if (File.Exists(hostResultFile))
            {
                File.Delete(hostResultFile);
            }
        }
    }

    private static void ValidateResult(
        SandboxedProcessResult result,
        DetoursEventListener listener)
    {
        if (result.ExitCode != 0 ||
            result.Killed ||
            result.TimedOut ||
            result.HasDetoursInjectionFailures ||
            result.MessageProcessingFailure is not null ||
            !listener.StartMarkerObserved ||
            !listener.StopMarkerObserved ||
            listener.AccessCount == 0)
        {
            string standardError = result.StandardError?.ReadValueAsync().GetAwaiter().GetResult() ?? string.Empty;
            throw new InvalidOperationException(
                $"Detours observation failed. ExitCode={result.ExitCode}, Killed={result.Killed}, " +
                $"TimedOut={result.TimedOut}, InjectionFailures={result.HasDetoursInjectionFailures}, " +
                $"MessageFailure={result.MessageProcessingFailure is not null}, " +
                $"StartMarker={listener.StartMarkerObserved}, StopMarker={listener.StopMarkerObserved}, " +
                $"Accesses={listener.AccessCount}.{Environment.NewLine}{standardError}");
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

    private static string Decode(string value) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(value));

    private static string TakeValue(List<string> args, string name)
    {
        int index = args.IndexOf(name);
        if (index < 0 || index + 1 >= args.Count)
        {
            throw new ArgumentException($"Missing required Detours host argument '{name}'.");
        }

        string value = args[index + 1];
        args.RemoveAt(index + 1);
        args.RemoveAt(index);
        return value;
    }

    private sealed class DetoursEventListener : IDetoursEventListener
    {
        private readonly string _scenarioRoot;
        private readonly string _startMarker;
        private readonly string _stopMarker;
        private readonly HashSet<string> _uniquePaths = new(StringComparer.OrdinalIgnoreCase);
        private int _accessCount;
        private int _counting;
        private int _startMarkerObserved;
        private int _stopMarkerObserved;

        internal DetoursEventListener(string scenarioRoot)
        {
            string fullRoot = Path.GetFullPath(scenarioRoot);
            _scenarioRoot = EnsureTrailingDirectorySeparator(fullRoot);
            _startMarker = Path.Combine(fullRoot, EvaluationObservationBenchmarkProtocol.MeasurementStartMarker);
            _stopMarker = Path.Combine(fullRoot, EvaluationObservationBenchmarkProtocol.MeasurementStopMarker);
        }

        internal int AccessCount => Volatile.Read(ref _accessCount);
        internal bool StartMarkerObserved => Volatile.Read(ref _startMarkerObserved) != 0;
        internal bool StopMarkerObserved => Volatile.Read(ref _stopMarkerObserved) != 0;

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
                Volatile.Write(ref _startMarkerObserved, 1);
                Volatile.Write(ref _counting, 1);
                return;
            }

            if (string.Equals(fullPath, _stopMarker, StringComparison.OrdinalIgnoreCase))
            {
                Volatile.Write(ref _stopMarkerObserved, 1);
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

        private bool IsUnderScenarioRoot(string fullPath) =>
            fullPath.StartsWith(_scenarioRoot, StringComparison.OrdinalIgnoreCase);

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

internal static class EvaluationObservationDetoursHost
{
    internal const string HostSwitch = "--evaluation-observation-detours-host";

    internal static bool TryRun(List<string> args, out int exitCode)
    {
        exitCode = 0;
        return false;
    }
}
#endif
