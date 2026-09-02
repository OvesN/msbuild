// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Engines;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;

namespace MSBuild.Benchmarks;

/// <summary>
/// Measures timestamp-based evaluation-cache admission and invalidation against real,
/// restored OrchardCore projects.
/// </summary>
/// <remarks>
/// Set <c>MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_PROJECTS</c> to one or more project
/// paths separated by <see cref="Path.PathSeparator"/>, and set
/// <c>MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_SDK_ROOT</c> to the SDK directory containing
/// <c>MSBuild.dll</c> and <c>Sdks</c>. Optionally restrict stale cases with the comma-separated
/// <c>MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_MUTATIONS</c> variable. Use a disposable worktree:
/// stale-validation cases
/// temporarily mutate and then exactly restore a tracked project source, imported file,
/// or glob directory.
/// </remarks>
public abstract class OrchardCoreEvaluationFilesystemTimestampBenchmarkBase
{
    private const string ProjectsEnvironmentVariable = "MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_PROJECTS";
    private const string SdkRootEnvironmentVariable = "MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_SDK_ROOT";
    private const string UnconfiguredProjectPath = "<configure OrchardCore project paths>";

    private protected EvaluationFilesystemTimestampCaptureResult BaselineCapture { get; private set; }
    private protected EvaluationObservationReport BaselineReport { get; private set; } = null!;
    private EvaluationObservationReport? _latestReport;
    private protected EvaluationFilesystemTimestampSnapshot Snapshot { get; private set; } = null!;
    private IDisposable? _observationScope;
    private string? _previousMSBuildExePath;
    private string? _previousMSBuildSdksPath;
    private string? _previousSdkResolverCliDirectory;

    [ParamsSource(nameof(ProjectPaths))]
    public string ProjectPath { get; set; } = null!;

    public IEnumerable<string> ProjectPaths
    {
        get
        {
            string? configuredProjects =
                Environment.GetEnvironmentVariable(ProjectsEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configuredProjects))
            {
                yield return UnconfiguredProjectPath;
                yield break;
            }

            bool foundProject = false;
            foreach (string configuredProject in configuredProjects.Split(
                         [Path.PathSeparator],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                string projectPath = configuredProject.Trim();
                if (projectPath.Length == 0)
                {
                    continue;
                }

                foundProject = true;
                yield return Path.GetFullPath(projectPath);
            }

            if (!foundProject)
            {
                yield return UnconfiguredProjectPath;
            }
        }
    }

    protected void SetupCore()
    {
        if (ProjectPath == UnconfiguredProjectPath)
        {
            throw new InvalidOperationException(
                $"{ProjectsEnvironmentVariable} must contain at least one restored OrchardCore project path.");
        }

        ConfigureSdk();
        try
        {
            if (!File.Exists(ProjectPath))
            {
                throw new FileNotFoundException("The benchmark project was not found.", ProjectPath);
            }

            using (EvaluationObservationSession.TestOnlyConfigure(
                       enabled: true,
                       CaptureLatestReport,
                       retainDetails: false))
            {
                _ = Evaluate();
            }

            BaselineReport = _latestReport
                ?? throw new InvalidOperationException("The benchmark setup did not produce an evaluation observation report.");

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(BaselineReport);
            EnsureCaptureSucceeded(capture);
            BaselineCapture = capture;
            Snapshot = capture.Snapshot;
            EnsureValidationStatus(
                Snapshot.Validate(),
                EvaluationFilesystemTimestampValidationStatus.Valid);
        }
        catch
        {
            RestoreSdk();
            throw;
        }
    }

    protected void CleanupCore()
    {
        EndObservedIteration();
        RestoreSdk();
    }

    protected void BeginObservedIteration()
    {
        _latestReport = null;
        _observationScope = EvaluationObservationSession.TestOnlyConfigure(
            enabled: true,
            CaptureLatestReport,
            retainDetails: false);
    }

    protected void EndObservedIteration()
    {
        _observationScope?.Dispose();
        _observationScope = null;
    }

    protected int FreshEvaluationCore() => Evaluate();

    protected int ObservedEvaluationCore()
    {
        int result = Evaluate();
        return result + GetLatestReport().FilesystemTimestamps.Count;
    }

    protected int ObservedEvaluationAndSnapshotCaptureCore()
    {
        int result = Evaluate();
        EvaluationFilesystemTimestampCaptureResult capture =
            EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(GetLatestReport());
        EnsureCaptureSucceeded(capture);
        return result +
            capture.TimestampReadCount +
            capture.ReparsePointProbeCount;
    }

    protected int SnapshotCaptureCore()
    {
        EvaluationFilesystemTimestampCaptureResult capture =
            EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(BaselineReport);
        EnsureCaptureSucceeded(capture);
        return capture.TimestampReadCount +
            capture.ReparsePointProbeCount;
    }

    protected int ValidReparsePointValidationCore()
    {
        EvaluationFilesystemTimestampValidationResult validation =
            Snapshot.ValidateReparsePoints();
        EnsureValidationStatus(validation, EvaluationFilesystemTimestampValidationStatus.Valid);
        return validation.CheckedReparsePointCount;
    }

    protected int ValidTimestampValidationWithoutReparsePointCheckCore()
    {
        EvaluationFilesystemTimestampValidationResult validation =
            EvaluationFilesystemTimestampValidator
                .ValidateTimestampsWithoutReparsePointCheck(Snapshot);
        EnsureValidationStatus(validation, EvaluationFilesystemTimestampValidationStatus.Valid);
        return validation.CheckedTimestampCount;
    }

    protected int ValidValidationCore()
    {
        EvaluationFilesystemTimestampValidationResult validation = Snapshot.Validate();
        EnsureValidationStatus(validation, EvaluationFilesystemTimestampValidationStatus.Valid);
        return validation.CheckedTimestampCount;
    }

    protected int Evaluate()
    {
        using ProjectCollection collection = new();
        ProjectInstance project = ProjectInstance.FromFile(ProjectPath, new ProjectOptions
        {
            ProjectCollection = collection,
        });

        string evaluatedProjectPath = project.GetPropertyValue("MSBuildProjectFullPath");
        if (!string.Equals(evaluatedProjectPath, ProjectPath, PathComparison))
        {
            throw new InvalidOperationException(
                $"Evaluation returned '{evaluatedProjectPath}' instead of '{ProjectPath}'.");
        }

        return evaluatedProjectPath.Length + project.Items.Count;
    }

    private void CaptureLatestReport(EvaluationObservationReport report)
    {
        _latestReport = report;
    }

    private EvaluationObservationReport GetLatestReport() =>
        _latestReport
        ?? throw new InvalidOperationException("The observed benchmark did not produce an observation report.");

    private protected static void EnsureCaptureSucceeded(EvaluationFilesystemTimestampCaptureResult capture)
    {
        if (capture.Status != EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly ||
            capture.IsFilesystemSnapshotAdmissible)
        {
            throw new InvalidOperationException(
                $"Timestamp analysis capture returned {capture.Status}/{capture.Failure} for '{capture.Path}'.");
        }
    }

    private protected static void EnsureValidationStatus(
        EvaluationFilesystemTimestampValidationResult validation,
        EvaluationFilesystemTimestampValidationStatus expected)
    {
        if (validation.Status != expected)
        {
            throw new InvalidOperationException(
                $"Timestamp validation returned {validation.Status}/{validation.Failure}, expected {expected}, at '{validation.Path}'.");
        }
    }

    protected static string FindRepositoryRoot(string projectPath)
    {
        DirectoryInfo? directory = new(Path.GetDirectoryName(projectPath)!);
        while (directory is not null)
        {
            string gitPath = Path.Combine(directory.FullName, ".git");
            if (File.Exists(gitPath) || Directory.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Could not find the repository root containing '{projectPath}'.");
    }

    protected static StringComparison PathComparison =>
        Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private void ConfigureSdk()
    {
        string sdkRoot = Path.GetFullPath(
            Environment.GetEnvironmentVariable(SdkRootEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{SdkRootEnvironmentVariable} must reference the SDK directory used to evaluate OrchardCore."));
        string msbuildPath = Path.Combine(sdkRoot, "MSBuild.dll");
        string sdksPath = Path.Combine(sdkRoot, "Sdks");
        string nugetFrameworksPath = Path.Combine(sdkRoot, "NuGet.Frameworks.dll");
        if (!File.Exists(msbuildPath) ||
            !Directory.Exists(sdksPath) ||
            !File.Exists(nugetFrameworksPath) ||
            !File.Exists(Path.Combine(sdkRoot, "Current", "Microsoft.Common.props")))
        {
            throw new InvalidOperationException(
                $"{SdkRootEnvironmentVariable} does not reference a complete .NET SDK directory: '{sdkRoot}'.");
        }

        _previousMSBuildExePath = Environment.GetEnvironmentVariable("MSBUILD_EXE_PATH");
        _previousMSBuildSdksPath = Environment.GetEnvironmentVariable("MSBuildSDKsPath");
        _previousSdkResolverCliDirectory =
            Environment.GetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR");

        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", msbuildPath);
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", sdksPath);
        Environment.SetEnvironmentVariable("DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR", sdkRoot);
        Assembly.LoadFrom(nugetFrameworksPath);
    }

    private void RestoreSdk()
    {
        Environment.SetEnvironmentVariable("MSBUILD_EXE_PATH", _previousMSBuildExePath);
        Environment.SetEnvironmentVariable("MSBuildSDKsPath", _previousMSBuildSdksPath);
        Environment.SetEnvironmentVariable(
            "DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR",
            _previousSdkResolverCliDirectory);
    }

    public enum EvaluationFilesystemTimestampMutationKind
    {
        ProjectFile,
        ImportFile,
        GlobMembership,
    }

    private protected sealed class MutationTarget
    {
        private readonly byte[]? _originalContents;
        private readonly DateTime _originalLastWriteTimeUtc;
        private readonly bool _createsFile;
        private bool _applied;

        private MutationTarget(
            string path,
            byte[]? originalContents,
            DateTime originalLastWriteTimeUtc,
            bool createsFile)
        {
            Path = path;
            _originalContents = originalContents;
            _originalLastWriteTimeUtc = originalLastWriteTimeUtc;
            _createsFile = createsFile;
        }

        internal string Path { get; }

        internal static MutationTarget Create(
            EvaluationFilesystemTimestampMutationKind kind,
            string projectPath,
            string repositoryRoot,
            EvaluationObservationReport report,
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            return kind switch
            {
                EvaluationFilesystemTimestampMutationKind.ProjectFile =>
                    CreateFileMutation(projectPath),
                EvaluationFilesystemTimestampMutationKind.ImportFile =>
                    CreateFileMutation(FindImportedFile(repositoryRoot, report)),
                EvaluationFilesystemTimestampMutationKind.GlobMembership =>
                    CreateGlobMutation(projectPath, repositoryRoot, snapshot),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
        }

        internal void Apply()
        {
            if (_applied)
            {
                throw new InvalidOperationException($"The mutation for '{Path}' is already active.");
            }

            _applied = true;
            try
            {
                DateTime changedTimestamp = DateTime.UtcNow.AddMinutes(1);
                if (_createsFile)
                {
                    File.WriteAllText(Path, "// Evaluation timestamp benchmark mutation.");
                    File.SetLastWriteTimeUtc(Path, changedTimestamp);
                    Directory.SetLastWriteTimeUtc(System.IO.Path.GetDirectoryName(Path)!, changedTimestamp);
                }
                else
                {
                    using (FileStream stream = new(Path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        stream.WriteByte((byte)' ');
                    }

                    File.SetLastWriteTimeUtc(Path, changedTimestamp);
                }

                DateTime observedTimestamp = _createsFile
                    ? Directory.GetLastWriteTimeUtc(System.IO.Path.GetDirectoryName(Path)!)
                    : File.GetLastWriteTimeUtc(Path);
                if (observedTimestamp == _originalLastWriteTimeUtc)
                {
                    throw new InvalidOperationException($"The timestamp mutation for '{Path}' did not change the timestamp.");
                }
            }
            catch
            {
                Restore();
                throw;
            }
        }

        internal void Restore()
        {
            if (!_applied)
            {
                return;
            }

            if (_createsFile)
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }

                Directory.SetLastWriteTimeUtc(
                    System.IO.Path.GetDirectoryName(Path)!,
                    _originalLastWriteTimeUtc);
            }
            else
            {
                File.WriteAllBytes(Path, _originalContents!);
                File.SetLastWriteTimeUtc(Path, _originalLastWriteTimeUtc);
            }

            _applied = false;
        }

        private static MutationTarget CreateFileMutation(string path)
        {
            return new MutationTarget(
                path,
                File.ReadAllBytes(path),
                File.GetLastWriteTimeUtc(path),
                createsFile: false);
        }

        private static MutationTarget CreateGlobMutation(
            string projectPath,
            string repositoryRoot,
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            string projectDirectory = System.IO.Path.GetDirectoryName(projectPath)!;
            MutationTarget? target = TryCreateGlobMutation(
                projectDirectory,
                repositoryRoot,
                snapshot);
            if (target is not null)
            {
                return target;
            }

            target = TryCreateGlobMutation(repositoryRoot, repositoryRoot, snapshot);
            if (target is not null)
            {
                return target;
            }

            throw new InvalidOperationException(
                $"No observed glob directory under '{repositoryRoot}' can be mutated.");
        }

        private static MutationTarget? TryCreateGlobMutation(
            string preferredRoot,
            string repositoryRoot,
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            foreach (EvaluationFilesystemTimestampEntry entry in snapshot.Entries)
            {
                if ((entry.Sources & EvaluationFilesystemTimestampSource.Glob) == 0 ||
                    !Directory.Exists(entry.Path) ||
                    !IsPathWithin(preferredRoot, entry.Path) ||
                    !IsEligibleMutationPath(repositoryRoot, entry.Path))
                {
                    continue;
                }

                string mutationPath = System.IO.Path.Combine(
                    entry.Path,
                    $".msbuild-evaluation-timestamp-benchmark-{Guid.NewGuid():N}.cs");
                if (File.Exists(mutationPath))
                {
                    throw new InvalidOperationException(
                        $"The benchmark glob mutation path already exists: '{mutationPath}'.");
                }

                return new MutationTarget(
                    mutationPath,
                    originalContents: null,
                    Directory.GetLastWriteTimeUtc(entry.Path),
                    createsFile: true);
            }

            return null;
        }

        private static string FindImportedFile(
            string repositoryRoot,
            EvaluationObservationReport report)
        {
            foreach (EvaluationProjectSourceObservation source in report.ProjectSources)
            {
                if (source.Role == EvaluationProjectSourceRole.Import &&
                    File.Exists(source.Path) &&
                    IsEligibleMutationPath(repositoryRoot, source.Path))
                {
                    return source.Path;
                }
            }

            throw new InvalidOperationException(
                $"No observed imported file under '{repositoryRoot}' can be mutated.");
        }

        private static bool IsEligibleMutationPath(string repositoryRoot, string path) =>
            IsPathWithin(repositoryRoot, path) &&
            !ContainsDirectory(path, ".dotnet") &&
            !ContainsDirectory(path, ".git") &&
            !ContainsDirectory(path, "artifacts") &&
            !ContainsDirectory(path, "bin") &&
            !ContainsDirectory(path, "obj") &&
            !ContainsDirectory(path, "packages");

        private static bool IsPathWithin(string root, string path)
        {
            string normalizedRoot = root.TrimEnd(
                System.IO.Path.DirectorySeparatorChar,
                System.IO.Path.AltDirectorySeparatorChar);
            if (string.Equals(normalizedRoot, path, PathComparison))
            {
                return true;
            }

            string rootWithSeparator = string.Concat(
                normalizedRoot,
                System.IO.Path.DirectorySeparatorChar);
            return path.StartsWith(rootWithSeparator, PathComparison);
        }

        private static bool ContainsDirectory(string path, string directory)
        {
            string normalizedPath = System.IO.Path.DirectorySeparatorChar ==
                System.IO.Path.AltDirectorySeparatorChar
                ? path
                : path.Replace(
                    System.IO.Path.AltDirectorySeparatorChar,
                    System.IO.Path.DirectorySeparatorChar);
            string directoryWithSeparators = string.Concat(
                System.IO.Path.DirectorySeparatorChar,
                directory,
                System.IO.Path.DirectorySeparatorChar);
            string terminalDirectory = string.Concat(
                System.IO.Path.DirectorySeparatorChar,
                directory);
            return normalizedPath.IndexOf(directoryWithSeparators, PathComparison) >= 0 ||
                normalizedPath.EndsWith(terminalDirectory, PathComparison);
        }
    }
}

[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Monitoring,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 12,
    invocationCount: 1)]
[MinColumn]
[MaxColumn]
[MedianColumn]
[MarkdownExporter]
public class OrchardCoreEvaluationFilesystemTimestampBenchmark :
    OrchardCoreEvaluationFilesystemTimestampBenchmarkBase
{
    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupCore();
        Console.WriteLine(
            $"EVALUATION_TIMESTAMP_BENCHMARK|Project={ProjectPath}|" +
            $"TimestampCount={Snapshot.TimestampCount}|" +
            $"ReparsePointCheckCount={Snapshot.ReparsePointCheckCount}|" +
            $"CaptureReparsePointProbeCount={BaselineCapture.ReparsePointProbeCount}");
    }

    [GlobalCleanup]
    public void GlobalCleanup() => CleanupCore();

    [IterationSetup(Targets =
        [nameof(ObservedEvaluation), nameof(ObservedEvaluationAndSnapshotCapture)])]
    public void BeginObservedBenchmarkIteration() => BeginObservedIteration();

    [IterationCleanup(Targets =
        [nameof(ObservedEvaluation), nameof(ObservedEvaluationAndSnapshotCapture)])]
    public void EndObservedBenchmarkIteration() => EndObservedIteration();

    [Benchmark(Baseline = true)]
    public int FreshEvaluation() => FreshEvaluationCore();

    [Benchmark]
    public int ObservedEvaluation() => ObservedEvaluationCore();

    [Benchmark]
    public int ObservedEvaluationAndSnapshotCapture() =>
        ObservedEvaluationAndSnapshotCaptureCore();

    [Benchmark]
    public int SnapshotCapture() => SnapshotCaptureCore();

    [Benchmark]
    public int ValidReparsePointValidation() =>
        ValidReparsePointValidationCore();

    [Benchmark]
    public int ValidTimestampValidationWithoutReparsePointCheck() =>
        ValidTimestampValidationWithoutReparsePointCheckCore();

    [Benchmark]
    public int ValidValidation() => ValidValidationCore();
}

[MemoryDiagnoser]
[SimpleJob(
    RunStrategy.Monitoring,
    launchCount: 1,
    warmupCount: 3,
    iterationCount: 12,
    invocationCount: 1)]
[MinColumn]
[MaxColumn]
[MedianColumn]
[MarkdownExporter]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByParams)]
public class OrchardCoreEvaluationFilesystemTimestampStaleBenchmark :
    OrchardCoreEvaluationFilesystemTimestampBenchmarkBase
{
    private const string MutationsEnvironmentVariable =
        "MSBUILD_EVALUATION_TIMESTAMP_BENCHMARK_MUTATIONS";

    private MutationTarget _mutationTarget = null!;
    private bool _mutationActive;

    [ParamsSource(nameof(MutationKinds))]
    public EvaluationFilesystemTimestampMutationKind MutationKind { get; set; }

    public IEnumerable<EvaluationFilesystemTimestampMutationKind> MutationKinds
    {
        get
        {
            string? configuredMutations =
                Environment.GetEnvironmentVariable(MutationsEnvironmentVariable);
            if (string.IsNullOrWhiteSpace(configuredMutations))
            {
                yield return EvaluationFilesystemTimestampMutationKind.ProjectFile;
                yield return EvaluationFilesystemTimestampMutationKind.ImportFile;
                yield return EvaluationFilesystemTimestampMutationKind.GlobMembership;
                yield break;
            }

            foreach (string configuredMutation in configuredMutations.Split(
                         [','],
                         StringSplitOptions.RemoveEmptyEntries))
            {
                if (!Enum.TryParse(
                        configuredMutation.Trim(),
                        ignoreCase: true,
                        out EvaluationFilesystemTimestampMutationKind mutationKind))
                {
                    throw new InvalidOperationException(
                        $"{MutationsEnvironmentVariable} contains unknown mutation '{configuredMutation}'.");
                }

                yield return mutationKind;
            }
        }
    }

    [GlobalSetup]
    public void GlobalSetup()
    {
        SetupCore();
        try
        {
            _mutationTarget = MutationTarget.Create(
                MutationKind,
                ProjectPath,
                FindRepositoryRoot(ProjectPath),
                BaselineReport,
                Snapshot);
            Console.WriteLine(
                $"EVALUATION_TIMESTAMP_BENCHMARK|Project={ProjectPath}|Mutation={MutationKind}|" +
                $"MutationTarget={_mutationTarget.Path}|TimestampCount={Snapshot.TimestampCount}|" +
                $"ReparsePointCheckCount={Snapshot.ReparsePointCheckCount}|" +
                $"CaptureReparsePointProbeCount={BaselineCapture.ReparsePointProbeCount}");
        }
        catch
        {
            CleanupCore();
            throw;
        }
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        try
        {
            RestoreMutation();
        }
        finally
        {
            CleanupCore();
        }
    }

    [IterationSetup(Targets =
        [nameof(StaleValidation), nameof(StaleValidationAndFreshEvaluation)])]
    public void ApplyMutation()
    {
        _mutationTarget.Apply();
        _mutationActive = true;
    }

    [IterationCleanup(Targets =
        [nameof(StaleValidation), nameof(StaleValidationAndFreshEvaluation)])]
    public void RestoreMutation()
    {
        if (!_mutationActive)
        {
            return;
        }

        _mutationTarget.Restore();
        _mutationActive = false;
        EnsureValidationStatus(
            Snapshot.Validate(),
            EvaluationFilesystemTimestampValidationStatus.Valid);
    }

    [Benchmark(Baseline = true)]
    public int FreshEvaluation() => FreshEvaluationCore();

    [Benchmark]
    public int StaleValidation()
    {
        EvaluationFilesystemTimestampValidationResult validation = Snapshot.Validate();
        EnsureValidationStatus(validation, EvaluationFilesystemTimestampValidationStatus.Changed);
        return validation.CheckedTimestampCount;
    }

    [Benchmark]
    public int StaleValidationAndFreshEvaluation()
    {
        EvaluationFilesystemTimestampValidationResult validation = Snapshot.Validate();
        EnsureValidationStatus(validation, EvaluationFilesystemTimestampValidationStatus.Changed);
        return validation.CheckedTimestampCount + Evaluate();
    }
}
