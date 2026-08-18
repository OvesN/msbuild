// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationDetoursRunner
{
    internal static EvaluationObservationBenchmarkResult Run(
        string executable,
        string arguments,
        string scenarioRoot)
    {
        throw new NotSupportedException("The Detours observer is not available in this benchmark variant.");
    }
}

