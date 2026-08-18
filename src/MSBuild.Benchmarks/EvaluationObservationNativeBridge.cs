// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.Build.Evaluation.Context;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationNativeBridge
{
    internal static IDisposable Enable(
        bool enabled,
        EvaluationObservationNativeMetrics? metrics,
        bool collectPaths)
    {
        return EvaluationObservationSession.TestOnlyConfigure(
            enabled,
            metrics is null
                ? null
                : report =>
                {
                    metrics.Reports++;
                    metrics.PathProbes += report.PathProbes.Length;
                    metrics.Enumerations += report.DirectoryEnumerations.Length;
                    metrics.MetadataReads += report.MetadataReads.Length;
                    metrics.FileReads += report.FileReads.Length;

                    if (collectPaths)
                    {
                        foreach (EvaluationPathProbeObservation observation in report.PathProbes)
                        {
                            metrics.AddPath(observation.Path);
                        }

                        foreach (EvaluationDirectoryEnumerationObservation observation in report.DirectoryEnumerations)
                        {
                            metrics.AddPath(observation.Path);
                            foreach (string entry in observation.Entries)
                            {
                                metrics.AddPath(entry);
                            }
                        }

                        foreach (EvaluationMetadataObservation observation in report.MetadataReads)
                        {
                            metrics.AddPath(observation.Path);
                        }

                        foreach (EvaluationFileReadObservation observation in report.FileReads)
                        {
                            metrics.AddPath(observation.Path);
                        }
                    }
                });
    }
}
