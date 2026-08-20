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
                    metrics.PathProbes += report.PathProbes.Count;
                    metrics.Enumerations += report.DirectoryEnumerations.Count;
                    metrics.MetadataReads += report.MetadataReads.Count;
                    metrics.FileReads += report.FileReads.Count;
                    metrics.SemanticObservations +=
                        (report.Request is null ? 0 : 1) +
                        report.ProjectSources.Count +
                        report.Globs.Count +
                        report.Searches.Count +
                        report.Environment.Count +
                        report.ExternalInputs.Count +
                        report.PropertyFunctions.Count +
                        report.SdkResolutions.Count +
                        report.TaskRegistrations.Count +
                        report.SideEffects.Count;
                    if (collectPaths && metrics.TryBeginPathSample())
                    {
                        foreach (EvaluationPathProbeObservation observation in report.PathProbes)
                        {
                            metrics.AddPath(observation.Path);
                        }

                        foreach (EvaluationDirectoryEnumerationObservation observation in report.DirectoryEnumerations)
                        {
                            metrics.AddEnumeration(observation);
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

                        foreach (EvaluationProjectSourceObservation observation in report.ProjectSources)
                        {
                            metrics.AddPath(observation.Path);
                        }

                        foreach (EvaluationGlobObservation observation in report.Globs)
                        {
                            metrics.AddPath(observation.Directory);
                            foreach (string result in observation.Results)
                            {
                                metrics.AddPath(result);
                            }
                        }

                        foreach (EvaluationSearchObservation observation in report.Searches)
                        {
                            foreach (string candidate in observation.Candidates)
                            {
                                metrics.AddPath(candidate);
                            }

                            metrics.AddPath(observation.Selected);
                        }

                        foreach (EvaluationSdkResolutionObservation observation in report.SdkResolutions)
                        {
                            metrics.AddPath(observation.Path);
                        }

                        foreach (EvaluationTaskRegistrationObservation observation in report.TaskRegistrations)
                        {
                            metrics.AddPath(observation.AssemblyFile);
                        }
                    }
                },
            retainDetails: collectPaths);
    }
}
