// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using System.Globalization;
using BenchmarkDotNet.Attributes;

namespace MSBuild.Benchmarks;

[MemoryDiagnoser]
public partial class EvaluationObservationBenchmark
{
    private const int TypicalFileCount = 200;
    private const int GlobHeavyFileCount = 2_000;

    [Params(EvaluationObservationBenchmarkScenario.Typical, EvaluationObservationBenchmarkScenario.GlobHeavy)]
    public EvaluationObservationBenchmarkScenario Scenario { get; set; }

    [Params(10)]
    public int EvaluationsPerProcess { get; set; }

    private string _root = null!;
    private string _projectPath = null!;
    private readonly Dictionary<EvaluationObservationBenchmarkMode, Aggregate> _aggregates = new();

    [GlobalSetup]
    public void GlobalSetup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"evaluation-observer-benchmark-{Guid.NewGuid():N}");
        string sourceDirectory = Path.Combine(_root, "src");
        Directory.CreateDirectory(sourceDirectory);

        int fileCount = Scenario == EvaluationObservationBenchmarkScenario.Typical
            ? TypicalFileCount
            : GlobHeavyFileCount;

        for (int i = 0; i < fileCount; i++)
        {
            string directory = Path.Combine(sourceDirectory, $"dir{i % 20}");
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, $"File{i}.cs"), string.Empty);
        }

        File.WriteAllText(Path.Combine(_root, "present.marker"), string.Empty);
        File.WriteAllText(
            Path.Combine(_root, "imported.props"),
            """
            <Project>
              <PropertyGroup>
                <ImportedProperty>ImportedValue</ImportedProperty>
              </PropertyGroup>
            </Project>
            """);

        StringBuilder project = new();
        project.AppendLine("<Project>");
        project.AppendLine("  <Import Project=\"imported.props\" />");
        project.AppendLine("  <PropertyGroup>");
        project.AppendLine("    <RequestedProperty>$(ImportedProperty)</RequestedProperty>");
        project.AppendLine("    <PresentMarker Condition=\"Exists('present.marker')\">true</PresentMarker>");
        project.AppendLine("    <MissingMarker Condition=\"Exists('missing.marker')\">true</MissingMarker>");
        project.AppendLine("  </PropertyGroup>");
        project.AppendLine("  <ItemGroup>");
        project.AppendLine("    <Compile Include=\"src/**/*.cs\" />");
        project.AppendLine("  </ItemGroup>");
        project.AppendLine("</Project>");

        _projectPath = Path.Combine(_root, "benchmark.proj");
        File.WriteAllText(_projectPath, project.ToString());
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        foreach (KeyValuePair<EvaluationObservationBenchmarkMode, Aggregate> entry in _aggregates)
        {
            Console.WriteLine(entry.Value.Format(entry.Key, Scenario));
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Benchmark(Baseline = true)]
    public long Baseline() => Run(EvaluationObservationBenchmarkMode.Baseline);

    private long Run(EvaluationObservationBenchmarkMode mode)
    {
        EvaluationObservationBenchmarkResult result = EvaluationObservationBenchmarkProcess.Run(
            mode,
            _projectPath,
            _root,
            EvaluationsPerProcess);

        if (!_aggregates.TryGetValue(mode, out Aggregate? aggregate))
        {
            aggregate = new Aggregate();
            _aggregates.Add(mode, aggregate);
        }

        aggregate.Add(result);
        return result.EvaluationTicks;
    }

    private sealed class Aggregate
    {
        private int _samples;
        private long _evaluationTicks;
        private long _retainedManagedBytes;
        private long _privateBytes;
        private long _peakWorkingSetBytes;
        private long _nativeReports;
        private long _nativePathProbes;
        private long _nativeEnumerations;
        private long _nativeMetadataReads;
        private long _nativeFileReads;
        private long _detoursAccesses;
        private long _detoursUniquePaths;

        internal void Add(EvaluationObservationBenchmarkResult result)
        {
            _samples++;
            _evaluationTicks += result.EvaluationTicks;
            _retainedManagedBytes += result.RetainedManagedBytes;
            _privateBytes += result.PrivateBytes;
            _peakWorkingSetBytes += result.PeakWorkingSetBytes;
            _nativeReports += result.NativeReports;
            _nativePathProbes += result.NativePathProbes;
            _nativeEnumerations += result.NativeEnumerations;
            _nativeMetadataReads += result.NativeMetadataReads;
            _nativeFileReads += result.NativeFileReads;
            _detoursAccesses += result.DetoursAccesses;
            _detoursUniquePaths += result.DetoursUniquePaths;
        }

        internal string Format(
            EvaluationObservationBenchmarkMode mode,
            EvaluationObservationBenchmarkScenario scenario)
        {
            return string.Join(
                "|",
                "EVALUATION_OBSERVATION_SUMMARY",
                $"Mode={mode}",
                $"Scenario={scenario}",
                Pair("Samples", _samples),
                Pair("EvaluationTicks", Average(_evaluationTicks)),
                Pair("RetainedManagedBytes", Average(_retainedManagedBytes)),
                Pair("PrivateBytes", Average(_privateBytes)),
                Pair("PeakWorkingSetBytes", Average(_peakWorkingSetBytes)),
                Pair("NativeReports", Average(_nativeReports)),
                Pair("NativePathProbes", Average(_nativePathProbes)),
                Pair("NativeEnumerations", Average(_nativeEnumerations)),
                Pair("NativeMetadataReads", Average(_nativeMetadataReads)),
                Pair("NativeFileReads", Average(_nativeFileReads)),
                Pair("DetoursAccesses", Average(_detoursAccesses)),
                Pair("DetoursUniquePaths", Average(_detoursUniquePaths)));
        }

        private long Average(long value) => _samples == 0 ? 0 : value / _samples;

        private static string Pair(string name, long value) =>
            string.Concat(name, "=", value.ToString(CultureInfo.InvariantCulture));
    }
}
