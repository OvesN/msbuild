// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using Microsoft.Build.Collections;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationBuildXLProjectComparison
{
    private const string ComparisonSwitch = "--evaluation-observation-buildxl-project";

    internal static bool TryRun(List<string> args, out int exitCode)
    {
        if (!args.Remove(ComparisonSwitch))
        {
            exitCode = 0;
            return false;
        }

        string projectPath = Path.GetFullPath(TakeValue(args, "--project"));
        List<string> ignoredComparisonRoots = TakeValues(args, "--root");

        int iterations = int.Parse(
            TryTakeValue(args, "--iterations") ?? "1",
            CultureInfo.InvariantCulture);
        bool includeNativeOnlyPaths = bool.Parse(
            TryTakeValue(args, "--include-native-only-paths") ?? bool.TrueString);
        Dictionary<string, string> globalProperties = TakeGlobalProperties(args);

        if (args.Count != 0)
        {
            throw new ArgumentException(
                $"Unexpected BuildXL comparison arguments: {string.Join(" ", args)}");
        }

        if (!File.Exists(projectPath))
        {
            throw new FileNotFoundException("The comparison project was not found.", projectPath);
        }

        if (iterations <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iterations),
                iterations,
                "The iteration count must be positive.");
        }

        Console.WriteLine("EVALUATION_OBSERVATION_COMPARISON_SCOPE|AllFilesystemPaths");
        foreach (string ignoredComparisonRoot in ignoredComparisonRoots)
        {
            Console.WriteLine(
                $"EVALUATION_OBSERVATION_IGNORED_COMPARISON_ROOT|{ignoredComparisonRoot}");
        }

        EvaluationObservationBenchmarkResult result =
            EvaluationObservationBenchmarkProcess.Run(
                EvaluationObservationBenchmarkMode.NativeAndDetours,
                EvaluationObservationBenchmarkScenario.ExternalProject,
                projectPath,
                [],
                iterations,
                globalProperties,
                Path.GetDirectoryName(projectPath)!,
                includeNativeOnlyPaths);
        Console.WriteLine(result.Serialize());

        exitCode = 0;
        return true;
    }

    private static Dictionary<string, string> TakeGlobalProperties(List<string> args)
    {
        Dictionary<string, string> properties = new(MSBuildNameIgnoreCaseComparer.Default);
        int index;
        while ((index = args.IndexOf("--global-property")) >= 0)
        {
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException("Missing value for '--global-property'.");
            }

            string assignment = args[index + 1];
            args.RemoveAt(index + 1);
            args.RemoveAt(index);
            int separator = assignment.IndexOf('=');
            if (separator <= 0)
            {
                throw new ArgumentException(
                    $"Global property '{assignment}' must use the form Name=Value.");
            }

            properties.Add(
                assignment.Substring(0, separator),
                assignment.Substring(separator + 1));
        }

        return properties;
    }

    private static string TakeValue(List<string> args, string name) =>
        TryTakeValue(args, name) ??
        throw new ArgumentException($"Missing required comparison argument '{name}'.");

    private static List<string> TakeValues(List<string> args, string name)
    {
        List<string> values = [];
        int index;
        while ((index = args.IndexOf(name)) >= 0)
        {
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException(
                    $"Missing value for comparison argument '{name}'.");
            }

            values.Add(args[index + 1]);
            args.RemoveAt(index + 1);
            args.RemoveAt(index);
        }

        return values;
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
            throw new ArgumentException($"Missing value for comparison argument '{name}'.");
        }

        string value = args[index + 1];
        args.RemoveAt(index + 1);
        args.RemoveAt(index);
        return value;
    }
}
