// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Build.BackEnd.Components.Logging;
using Microsoft.Build.BackEnd.Logging;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

namespace MSBuild.Benchmarks;

/// <summary>
/// Measures what recording evaluation inputs (<c>MSBUILDRECORDEVALUATIONINPUTS=1</c>) adds to a project
/// evaluation, and what validating the recorded inputs costs compared to evaluating again.
/// </summary>
/// <remarks>
/// <para>
/// By default the benchmark evaluates a synthetic project that mirrors an SDK-style project without the SDK:
/// a <c>Directory.Build.props</c> found by upward search, wildcard imports, chained properties, file,
/// environment, and path property functions, and recursive item globs over a source tree with excluded
/// <c>obj</c> directories.
/// </para>
/// <para>
/// To measure real projects, set <c>MSBUILD_EVALUATION_INPUTS_BENCHMARK_PROJECTS</c> to a
/// <see cref="Path.PathSeparator"/>-separated list of restored project files. SDK-style projects also need
/// <c>MSBUILD_EXE_PATH</c> pointing at the bootstrap <c>MSBuild.dll</c>, <c>MSBuildSDKsPath</c> at its
/// <c>Sdks</c> directory (<c>artifacts\bin\bootstrap\core\sdk\&lt;version&gt;</c>), and
/// <c>DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR</c> at <c>artifacts\bin\bootstrap\core</c>. Pass those three to
/// the benchmark process through BenchmarkDotNet, for example
/// <c>Run-Benchmarks.ps1 -BenchmarkDotNetArguments '--envVars', 'MSBUILD_EXE_PATH:...', ...</c>; setting
/// them in the host environment redirects the build of the generated benchmark project instead.
/// A real project that is not cacheable fails setup with the reason, which makes the run double as an
/// admissibility check.
/// </para>
/// </remarks>
[MemoryDiagnoser]
public class EvaluationInputRecordingBenchmark
{
    private const string RecordingVariable = "MSBUILDRECORDEVALUATIONINPUTS";
    private const string ProjectsVariable = "MSBUILD_EVALUATION_INPUTS_BENCHMARK_PROJECTS";
    private const string SyntheticProject = "synthetic";
    private const int DirectoryCount = 32;
    private const int SourceFilesPerDirectory = 4;
    private const int ImportCount = 8;
    private const int ChainedPropertyCount = 100;

    private const string GlobMemberName = "evaluation-inputs-benchmark.tmp";
    private const int SubmissionId = 1;

    private TemporaryDirectory? _directory;
    private string _projectPath = null!;
    private EvaluationInputs _inputs = null!;
    private Dictionary<SdkReference, SdkResult> _sdkResults = null!;
    private Microsoft.Build.BackEnd.SdkResolution.CachingSdkResolverService _sdkResolverService = null!;
    private EvaluationLoggingContext _loggingContext = null!;
    private EvaluationContext _sharedContext = null!;
    private string? _importPath;
    private string? _globDirectory;
    private DateTime _projectWriteTime;
    private DateTime _importWriteTime;

    [ParamsSource(nameof(ProjectPaths))]
    public string ProjectPath { get; set; } = SyntheticProject;

    public static IEnumerable<string> ProjectPaths
    {
        get
        {
            yield return SyntheticProject;

            string? projects = Environment.GetEnvironmentVariable(ProjectsVariable);
            if (!string.IsNullOrEmpty(projects))
            {
                foreach (string project in projects.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
                {
                    yield return Path.GetFullPath(project);
                }
            }
        }
    }

    [GlobalSetup(Targets = [nameof(Evaluate), nameof(EvaluateSharedContext)])]
    public void SetupWithoutRecording() => Setup(record: false);

    [GlobalSetup(Targets = [nameof(EvaluateRecording), nameof(EvaluateRecordingSharedContext), nameof(ValidateUnchanged), nameof(ValidateResolvingSdks), nameof(ValidateStaleProjectFile), nameof(ValidateStaleImportedFile), nameof(ValidateStaleGlobMembership)])]
    public void SetupWithRecording() => Setup(record: true);

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        SetRecording(enabled: false);
        _directory?.Dispose();
    }

    [IterationSetup(Target = nameof(ValidateStaleProjectFile))]
    public void TouchProjectFile() => _projectWriteTime = Touch(_projectPath);

    [IterationCleanup(Target = nameof(ValidateStaleProjectFile))]
    public void RestoreProjectFile() => File.SetLastWriteTimeUtc(_projectPath, _projectWriteTime);

    [IterationSetup(Target = nameof(ValidateStaleImportedFile))]
    public void TouchImportedFile() => _importWriteTime = Touch(ImportPath);

    [IterationCleanup(Target = nameof(ValidateStaleImportedFile))]
    public void RestoreImportedFile() => File.SetLastWriteTimeUtc(ImportPath, _importWriteTime);

    [IterationSetup(Target = nameof(ValidateStaleGlobMembership))]
    public void AddGlobMember() => File.WriteAllText(GlobMemberPath, string.Empty);

    [IterationCleanup(Target = nameof(ValidateStaleGlobMembership))]
    public void RemoveGlobMember() => File.Delete(GlobMemberPath);

    [Benchmark(Baseline = true)]
    public int Evaluate() => EvaluateProject().GetItems("Compile").Count;

    [Benchmark]
    public int EvaluateRecording() => EvaluateProject().GetItems("Compile").Count;

    /// <summary>Evaluation through one shared <see cref="EvaluationContext"/>, whose glob and SDK caches stay warm across evaluations as in a graph build.</summary>
    [Benchmark]
    public int EvaluateSharedContext() => EvaluateProject(_sharedContext).GetItems("Compile").Count;

    /// <summary>The shared-context evaluation with recording: cached glob expansions are reused and the directories they depend on are replayed to the recorder.</summary>
    [Benchmark]
    public int EvaluateRecordingSharedContext() => EvaluateProject(_sharedContext).GetItems("Compile").Count;

    /// <summary>Validation with the SDK results compared against the recorded ones, the cost every project after the first pays in a build submission.</summary>
    [Benchmark]
    public bool ValidateUnchanged() => Validate();

    /// <summary>Validation that resolves every recorded SDK reference again through a cold resolver cache, the cost the first project in a build submission pays.</summary>
    [Benchmark]
    public bool ValidateResolvingSdks()
    {
        _sdkResolverService.ClearCache(SubmissionId);
        return EvaluationInputValidator.IsCurrent(_inputs, ResolveSdk, out _);
    }

    /// <summary>Validation after the project file's timestamp moved; the result is false.</summary>
    [Benchmark]
    public bool ValidateStaleProjectFile() => Validate();

    /// <summary>Validation after the timestamp of the recorded import nearest the project moved; the result is false.</summary>
    [Benchmark]
    public bool ValidateStaleImportedFile() => Validate();

    /// <summary>Validation after a file appeared in a directory a glob traversed, which moves the directory's timestamp; the result is false.</summary>
    [Benchmark]
    public bool ValidateStaleGlobMembership() => Validate();

    private bool Validate() => EvaluationInputValidator.IsCurrent(_inputs, reference => _sdkResults[reference], out _);

    private SdkResult? ResolveSdk(SdkReference reference) =>
        _sdkResolverService.ResolveSdk(SubmissionId, reference, _loggingContext, ElementLocation.EmptyLocation, solutionPath: null, _projectPath, interactive: false, isRunningInVisualStudio: false, failOnUnresolvedSdk: false);

    private string ImportPath =>
        _importPath ?? throw new NotSupportedException($"{_projectPath} records no imported .props or .targets file.");

    private string GlobMemberPath =>
        Path.Combine(_globDirectory ?? throw new NotSupportedException($"{_projectPath} records no directory under the project."), GlobMemberName);

    private void Setup(bool record)
    {
        if (ProjectPath == SyntheticProject)
        {
            _directory = new TemporaryDirectory(nameof(EvaluationInputRecordingBenchmark));
            CreateTree();
            _projectPath = _directory.WriteFile("evaluation.proj", CreateProjectXml());
        }
        else
        {
            _projectPath = ProjectPath;
        }

        _sharedContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
        SetRecording(enabled: true);
        _inputs = EvaluateProject().EvaluationInputs
            ?? throw new InvalidOperationException("Recording did not produce inputs.");
        if (!_inputs.IsCacheable)
        {
            throw new InvalidOperationException($"{_projectPath} is not cacheable: {_inputs.NonCacheable} {_inputs.NonCacheableDetail}");
        }

        _sdkResults = _inputs.SdkResolutions.ToDictionary(sdk => sdk.Reference, sdk => sdk.Result);
        _sdkResolverService = new Microsoft.Build.BackEnd.SdkResolution.CachingSdkResolverService();
        _loggingContext = new EvaluationLoggingContext(LoggingService.CreateLoggingService(LoggerMode.Synchronous, 1), new BuildEventContext(0, 0, 0, 0), _projectPath);
        foreach (SdkDependency sdk in _inputs.SdkResolutions)
        {
            if (!sdk.Result.Equals(ResolveSdk(sdk.Reference)))
            {
                Console.WriteLine($"// {_projectPath}: resolving {sdk.Reference.Name} again gives a different result, so ValidateResolvingSdks returns false");
            }
        }

        _importPath = FindNearestImport();
        _globDirectory = FindShallowestDirectoryUnderProject();
        Console.WriteLine($"// {_projectPath}: {_inputs.Files.Count} paths, {_inputs.EnvironmentReads.Count} environment reads, {_inputs.SdkResolutions.Length} SDK resolutions; stale import {_importPath}, glob directory {_globDirectory}");
        SetRecording(record);
    }

    /// <summary>
    /// A cold evaluation: the fresh collection keeps the project XML cache empty so every evaluation reads its files again.
    /// The collection costs the same with and without recording.
    /// </summary>
    private ProjectInstance EvaluateProject(EvaluationContext? context = null)
    {
        using ProjectCollection collection = new();
        return ProjectInstance.FromFile(_projectPath, new ProjectOptions { ProjectCollection = collection, EvaluationContext = context });
    }

    /// <summary>
    /// Moves a file's timestamp forward by two seconds and returns the original, so file systems with coarse timestamps see a change.
    /// </summary>
    private static DateTime Touch(string path)
    {
        DateTime original = File.GetLastWriteTimeUtc(path);
        File.SetLastWriteTimeUtc(path, original.AddSeconds(2));
        return original;
    }

    /// <summary>
    /// The recorded .props or .targets file sharing the longest path prefix with the project: the import a developer edits,
    /// not one inside the SDK.
    /// </summary>
    private string? FindNearestImport()
    {
        string? nearest = null;
        int longestShared = -1;
        foreach (KeyValuePair<string, FileDependency> file in _inputs.Files)
        {
            string extension = Path.GetExtension(file.Key);
            if (file.Value.Kind != PathKind.File
                || string.Equals(file.Key, _projectPath, StringComparison.OrdinalIgnoreCase)
                || !(extension.Equals(".props", StringComparison.OrdinalIgnoreCase) || extension.Equals(".targets", StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            int shared = 0;
            while (shared < file.Key.Length && shared < _projectPath.Length && char.ToUpperInvariant(file.Key[shared]) == char.ToUpperInvariant(_projectPath[shared]))
            {
                shared++;
            }

            if (shared > longestShared)
            {
                longestShared = shared;
                nearest = file.Key;
            }
        }

        return nearest;
    }

    /// <summary>
    /// The shallowest recorded directory at or below the project directory, which a glob traversed.
    /// </summary>
    private string? FindShallowestDirectoryUnderProject()
    {
        string projectDirectory = Path.GetDirectoryName(_projectPath)!;
        string? shallowest = null;
        foreach (KeyValuePair<string, FileDependency> file in _inputs.Files)
        {
            bool underProject = string.Equals(file.Key, projectDirectory, StringComparison.OrdinalIgnoreCase)
                || file.Key.StartsWith(projectDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            if (file.Value.Kind == PathKind.Directory && underProject && (shallowest is null || file.Key.Length < shallowest.Length))
            {
                shallowest = file.Key;
            }
        }

        return shallowest;
    }

    private static void SetRecording(bool enabled)
    {
        Environment.SetEnvironmentVariable(RecordingVariable, enabled ? "1" : null);
        Traits.UpdateFromEnvironment();
    }

    private void CreateTree()
    {
        _directory!.WriteFile("Directory.Build.props", "<Project><PropertyGroup><FromDirectoryBuildProps>true</FromDirectoryBuildProps></PropertyGroup></Project>");
        _directory.WriteFile("version.txt", "1.2.3");

        for (int importIndex = 0; importIndex < ImportCount; importIndex++)
        {
            StringBuilder import = new("<Project><PropertyGroup>");
            for (int propertyIndex = 0; propertyIndex < 10; propertyIndex++)
            {
                import.Append($"<Import{importIndex}Property{propertyIndex}>{propertyIndex}</Import{importIndex}Property{propertyIndex}>");
            }

            import.Append("</PropertyGroup></Project>");
            _directory.WriteFile(Path.Combine("imports", $"import{importIndex:D2}.props"), import.ToString());
        }

        for (int directoryIndex = 0; directoryIndex < DirectoryCount; directoryIndex++)
        {
            string directory = Path.Combine("src", $"group{directoryIndex:D3}");
            for (int fileIndex = 0; fileIndex < SourceFilesPerDirectory; fileIndex++)
            {
                _directory.WriteFile(Path.Combine(directory, $"file{fileIndex}.cs"), "class C {}");
            }

            _directory.WriteFile(Path.Combine(directory, "readme.txt"), "readme");
            _directory.WriteFile(Path.Combine(directory, "obj", "generated.cs"), "class G {}");
        }
    }

    private static string CreateProjectXml()
    {
        StringBuilder xml = new();
        xml.AppendLine("<Project>");
        xml.AppendLine("  <PropertyGroup>");
        xml.AppendLine("    <DirectoryBuildPropsPath>$([MSBuild]::GetPathOfFileAbove('Directory.Build.props'))</DirectoryBuildPropsPath>");
        xml.AppendLine("  </PropertyGroup>");
        xml.AppendLine("  <Import Project=\"$(DirectoryBuildPropsPath)\" Condition=\"'$(DirectoryBuildPropsPath)' != ''\" />");
        xml.AppendLine("  <PropertyGroup>");
        xml.AppendLine("    <Configuration Condition=\"'$(Configuration)' == ''\">Debug</Configuration>");
        xml.AppendLine("    <OutputPath>bin/$(Configuration)/</OutputPath>");
        xml.AppendLine("    <Version>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)version.txt').Trim())</Version>");
        xml.AppendLine("    <SourceRoot>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', 'src'))</SourceRoot>");
        xml.AppendLine("    <Stamp>$([System.Environment]::GetEnvironmentVariable('MSBUILD_BENCHMARK_STAMP'))</Stamp>");
        xml.AppendLine("    <HasGenerated>$([System.IO.Directory]::Exists('$(SourceRoot)generated'))</HasGenerated>");
        xml.AppendLine("    <Chain0>value</Chain0>");
        for (int i = 1; i < ChainedPropertyCount; i++)
        {
            xml.AppendLine($"    <Chain{i}>$(Chain{i - 1}).{i}</Chain{i}>");
        }

        xml.AppendLine("  </PropertyGroup>");
        xml.AppendLine("  <Import Project=\"imports/*.props\" />");
        xml.AppendLine("  <ItemGroup>");
        xml.AppendLine("    <Compile Include=\"src/**/*.cs\" Exclude=\"src/**/obj/**\" />");
        xml.AppendLine("    <None Include=\"**/*.txt\" />");
        xml.AppendLine("  </ItemGroup>");
        xml.AppendLine("</Project>");
        return xml.ToString();
    }
}
