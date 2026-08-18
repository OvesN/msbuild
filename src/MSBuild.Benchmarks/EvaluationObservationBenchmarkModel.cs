// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationBenchmarkProtocol
{
    internal const string MeasurementStartMarker = ".evaluation-observer-measure-start";
    internal const string MeasurementStopMarker = ".evaluation-observer-measure-stop";
}

[Flags]
internal enum EvaluationObservationBenchmarkMode
{
    Baseline = 0,
    Native = 1,
    Detours = 1 << 1,
    NativeAndDetours = Native | Detours,
}

public enum EvaluationObservationBenchmarkScenario
{
    Typical,
    GlobHeavy,
}

internal sealed class EvaluationObservationBenchmarkResult
{
    private const string Prefix = "EVALUATION_OBSERVATION_BENCHMARK";

    internal long EvaluationTicks { get; init; }
    internal long RetainedManagedBytes { get; init; }
    internal long PrivateBytes { get; init; }
    internal long PeakWorkingSetBytes { get; init; }
    internal int Gen0Collections { get; init; }
    internal int Gen1Collections { get; init; }
    internal int Gen2Collections { get; init; }
    internal int NativeReports { get; init; }
    internal int NativePathProbes { get; init; }
    internal int NativeEnumerations { get; init; }
    internal int NativeMetadataReads { get; init; }
    internal int NativeFileReads { get; init; }
    internal int DetoursAccesses { get; init; }
    internal int DetoursUniquePaths { get; init; }

    internal string Serialize()
    {
        return string.Join(
            "|",
            Prefix,
            Pair(nameof(EvaluationTicks), EvaluationTicks),
            Pair(nameof(RetainedManagedBytes), RetainedManagedBytes),
            Pair(nameof(PrivateBytes), PrivateBytes),
            Pair(nameof(PeakWorkingSetBytes), PeakWorkingSetBytes),
            Pair(nameof(Gen0Collections), Gen0Collections),
            Pair(nameof(Gen1Collections), Gen1Collections),
            Pair(nameof(Gen2Collections), Gen2Collections),
            Pair(nameof(NativeReports), NativeReports),
            Pair(nameof(NativePathProbes), NativePathProbes),
            Pair(nameof(NativeEnumerations), NativeEnumerations),
            Pair(nameof(NativeMetadataReads), NativeMetadataReads),
            Pair(nameof(NativeFileReads), NativeFileReads),
            Pair(nameof(DetoursAccesses), DetoursAccesses),
            Pair(nameof(DetoursUniquePaths), DetoursUniquePaths));
    }

    internal static EvaluationObservationBenchmarkResult Parse(string output)
    {
        string? line = null;
        using (StringReader reader = new(output))
        {
            while (reader.ReadLine() is { } candidate)
            {
                if (candidate.StartsWith(Prefix, StringComparison.Ordinal))
                {
                    line = candidate;
                }
            }
        }

        if (line is null)
        {
            throw new InvalidOperationException($"Benchmark host did not return a {Prefix} result.{Environment.NewLine}{output}");
        }

        Dictionary<string, long> values = new(StringComparer.Ordinal);
        string[] fields = line.Split('|');
        for (int i = 1; i < fields.Length; i++)
        {
            int separator = fields[i].IndexOf('=');
            if (separator <= 0 ||
                !long.TryParse(fields[i].Substring(separator + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out long value))
            {
                throw new InvalidOperationException($"Invalid benchmark result field '{fields[i]}'.");
            }

            values.Add(fields[i].Substring(0, separator), value);
        }

        return new EvaluationObservationBenchmarkResult
        {
            EvaluationTicks = Get(nameof(EvaluationTicks)),
            RetainedManagedBytes = Get(nameof(RetainedManagedBytes)),
            PrivateBytes = Get(nameof(PrivateBytes)),
            PeakWorkingSetBytes = Get(nameof(PeakWorkingSetBytes)),
            Gen0Collections = checked((int)Get(nameof(Gen0Collections))),
            Gen1Collections = checked((int)Get(nameof(Gen1Collections))),
            Gen2Collections = checked((int)Get(nameof(Gen2Collections))),
            NativeReports = checked((int)Get(nameof(NativeReports))),
            NativePathProbes = checked((int)Get(nameof(NativePathProbes))),
            NativeEnumerations = checked((int)Get(nameof(NativeEnumerations))),
            NativeMetadataReads = checked((int)Get(nameof(NativeMetadataReads))),
            NativeFileReads = checked((int)Get(nameof(NativeFileReads))),
            DetoursAccesses = checked((int)Get(nameof(DetoursAccesses))),
            DetoursUniquePaths = checked((int)Get(nameof(DetoursUniquePaths))),
        };

        long Get(string name) =>
            values.TryGetValue(name, out long value)
                ? value
                : throw new InvalidOperationException($"Benchmark result did not contain '{name}'.");
    }

    private static string Pair(string name, long value) =>
        string.Concat(name, "=", value.ToString(CultureInfo.InvariantCulture));
}

internal sealed class EvaluationObservationNativeMetrics
{
    internal int Reports = 0;
    internal int PathProbes = 0;
    internal int Enumerations = 0;
    internal int MetadataReads = 0;
    internal int FileReads = 0;
}
