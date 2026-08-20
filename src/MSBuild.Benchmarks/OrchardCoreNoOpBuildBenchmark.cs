// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET

using System.Diagnostics;
using System.Text.Json;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;

namespace MSBuild.Benchmarks;

/// <summary>
/// Measures a warm no-op <c>dotnet build</c> on OrchardCore while comparing two externally
/// supplied .NET SDK configurations.
/// </summary>
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
public class OrchardCoreNoOpBuildBenchmark
{
    private const string ConfigurationEnvironmentVariable = "MSBUILD_ORCHARD_NOOP_BUILD_CONFIG";

    private Settings _settings = null!;
    private string _buildPath = null!;

    [GlobalSetup(Target = nameof(Before))]
    public Task GlobalSetupBefore() => GlobalSetupAsync(useAfterCell: false);

    [GlobalSetup(Target = nameof(After))]
    public Task GlobalSetupAfter() => GlobalSetupAsync(useAfterCell: true);

    [GlobalCleanup]
    public async Task GlobalCleanup()
    {
        if (_settings is null)
        {
            return;
        }

        await ShutdownBuildServersAsync(_settings.Before).ConfigureAwait(false);
        if (!string.Equals(
                _settings.Before.DotNetPath,
                _settings.After.DotNetPath,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            await ShutdownBuildServersAsync(_settings.After).ConfigureAwait(false);
        }
    }

    #endif

    [Benchmark(Baseline = true)]
    public Task Before() => ExecuteNoOpBuildAsync(_settings.Before);

    [Benchmark]
    public Task After() => ExecuteNoOpBuildAsync(_settings.After);

    private async Task GlobalSetupAsync(bool useAfterCell)
    {
        string configurationPath = Environment.GetEnvironmentVariable(ConfigurationEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{ConfigurationEnvironmentVariable} must point to an OrchardCore benchmark JSON configuration.");

        configurationPath = Path.GetFullPath(configurationPath);
        _settings = JsonSerializer.Deserialize<Settings>(
            File.ReadAllText(configurationPath),
            Settings.SerializerOptions)
            ?? throw new InvalidDataException($"Could not deserialize '{configurationPath}'.");
        _settings.Validate(configurationPath);

        string orchardCoreRoot = Path.GetFullPath(_settings.OrchardCoreRoot);
        _buildPath = Path.GetFullPath(_settings.BuildPath, orchardCoreRoot);
        if (!File.Exists(_buildPath))
        {
            throw new FileNotFoundException("The OrchardCore build project or solution was not found.", _buildPath);
        }

        Cell cell = useAfterCell ? _settings.After : _settings.Before;

        List<string> restoreArguments =
        [
            "restore",
            _buildPath,
            "-m",
            "-nodeReuse:false",
            "-v:q",
            .. cell.RestoreArguments ?? [],
        ];
        await RunDotNetAsync(cell, restoreArguments, throwOnFailure: true).ConfigureAwait(false);

        await RunDotNetAsync(cell, CreateBuildArguments(cell), throwOnFailure: true).ConfigureAwait(false);
        await ShutdownBuildServersAsync(cell).ConfigureAwait(false);
    }

    private Task ExecuteNoOpBuildAsync(Cell cell) =>
        RunDotNetAsync(cell, CreateBuildArguments(cell), throwOnFailure: true);

    private List<string> CreateBuildArguments(Cell cell)
    {
        List<string> arguments =
        [
            "build",
            _buildPath,
            "-c",
            _settings.Configuration,
            "--no-restore",
            "-m",
            "-nodeReuse:false",
            "-v:q",
        ];

        if (!string.IsNullOrWhiteSpace(_settings.TargetFramework))
        {
            arguments.Add("-f");
            arguments.Add(_settings.TargetFramework);
        }

        arguments.AddRange(cell.BuildArguments ?? []);
        return arguments;
    }

    private static Task ShutdownBuildServersAsync(Cell cell) =>
        RunDotNetAsync(
            cell,
            ["build-server", "shutdown"],
            throwOnFailure: false,
            timeout: TimeSpan.FromMinutes(2));

    private static async Task RunDotNetAsync(
        Cell cell,
        IReadOnlyCollection<string> arguments,
        bool throwOnFailure,
        TimeSpan? timeout = null)
    {
        ProcessStartInfo startInfo = new(Path.GetFullPath(cell.DotNetPath))
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = cell.ResolvedWorkingDirectory,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        startInfo.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment.Remove("DOTNET_STARTUP_HOOKS");

        if (cell.EnvironmentVariables is not null)
        {
            foreach ((string name, string? value) in cell.EnvironmentVariables)
            {
                if (value is null)
                {
                    startInfo.Environment.Remove(name);
                }
                else
                {
                    startInfo.Environment[name] = value;
                }
            }
        }

        using Process process = new() { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{startInfo.FileName}'.");
        }

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task waitForExitTask = process.WaitForExitAsync();
        TimeSpan processTimeout = timeout ?? TimeSpan.FromMinutes(cell.TimeoutMinutes);
        bool timedOut = await Task.WhenAny(waitForExitTask, Task.Delay(processTimeout)).ConfigureAwait(false)
            != waitForExitTask;
        if (timedOut)
        {
            process.Kill(entireProcessTree: true);
        }

        await waitForExitTask.ConfigureAwait(false);
        string stdout = await stdoutTask.ConfigureAwait(false);
        string stderr = await stderrTask.ConfigureAwait(false);

        if (timedOut && throwOnFailure)
        {
            throw new TimeoutException(
                $"'{startInfo.FileName} {string.Join(' ', arguments)}' did not exit within {processTimeout}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{TakeTail(stdout)}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{TakeTail(stderr)}");
        }

        if (throwOnFailure && process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{startInfo.FileName} {string.Join(' ', arguments)}' exited with {process.ExitCode}.{Environment.NewLine}" +
                $"stdout:{Environment.NewLine}{TakeTail(stdout)}{Environment.NewLine}" +
                $"stderr:{Environment.NewLine}{TakeTail(stderr)}");
        }
    }

    private static string TakeTail(string value)
    {
        const int MaximumLength = 4_000;
        return value.Length <= MaximumLength ? value : value[^MaximumLength..];
    }

    internal sealed class Settings
    {
        internal static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public string OrchardCoreRoot { get; init; } = string.Empty;
        public string BuildPath { get; init; } = "OrchardCore.slnx";
        public string Configuration { get; init; } = "Release";
        public string? TargetFramework { get; init; }
        public Cell Before { get; init; } = new();
        public Cell After { get; init; } = new();

        internal void Validate(string configurationPath)
        {
            if (string.IsNullOrWhiteSpace(OrchardCoreRoot))
            {
                throw new InvalidDataException($"OrchardCoreRoot is missing from '{configurationPath}'.");
            }

            if (string.IsNullOrWhiteSpace(BuildPath))
            {
                throw new InvalidDataException($"BuildPath is missing from '{configurationPath}'.");
            }

            if (string.IsNullOrWhiteSpace(Configuration))
            {
                throw new InvalidDataException($"Configuration is missing from '{configurationPath}'.");
            }

            Before.Validate(configurationPath, nameof(Before), OrchardCoreRoot);
            After.Validate(configurationPath, nameof(After), OrchardCoreRoot);
        }
    }

    internal sealed class Cell
    {
        public string DotNetPath { get; init; } = string.Empty;
        public string WorkingDirectory { get; init; } = string.Empty;
        public Dictionary<string, string?>? EnvironmentVariables { get; init; }
        public string[]? RestoreArguments { get; init; }
        public string[]? BuildArguments { get; init; }
        public int TimeoutMinutes { get; init; } = 30;
        internal string ResolvedWorkingDirectory { get; private set; } = string.Empty;

        internal void Validate(string configurationPath, string name, string orchardCoreRoot)
        {
            if (string.IsNullOrWhiteSpace(DotNetPath) || !File.Exists(Path.GetFullPath(DotNetPath)))
            {
                throw new InvalidDataException(
                    $"{name}.DotNetPath in '{configurationPath}' does not reference an existing file.");
            }

            ResolvedWorkingDirectory = Path.GetFullPath(
                string.IsNullOrWhiteSpace(WorkingDirectory) ? orchardCoreRoot : WorkingDirectory);
            if (!Directory.Exists(ResolvedWorkingDirectory))
            {
                throw new InvalidDataException(
                    $"{name}.WorkingDirectory in '{configurationPath}' does not reference an existing directory.");
            }

            if (TimeoutMinutes <= 0)
            {
                throw new InvalidDataException($"{name}.TimeoutMinutes in '{configurationPath}' must be positive.");
            }
        }
    }
}
