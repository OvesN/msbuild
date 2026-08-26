// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Framework;
using Microsoft.Build.Framework.Profiler;

namespace Microsoft.Build.TelemetryInfra;

internal static class EvaluationMetrics
{
    internal const string MeterName = "Microsoft.Build";
    internal const string ProjectEvaluationCountName = "msbuild.project.evaluations";
    internal const string ProjectEvaluationDurationName = "msbuild.project.evaluation.duration";
    internal const string ProjectEvaluationPassDurationName = "msbuild.project.evaluation.pass.duration";
    internal const string ItemGlobRequestCountName = "msbuild.project.evaluation.item.glob.requests";
    internal const string ItemGlobDurationName = "msbuild.project.evaluation.item.glob.duration";
    internal const string ItemGlobFileCountName = "msbuild.project.evaluation.item.glob.files";
    internal const string ItemGlobExcludeCountName = "msbuild.project.evaluation.item.glob.excludes";
    internal const string ItemGlobConcurrencyName = "msbuild.project.evaluation.item.glob.concurrency";

    internal const string StageTagName = "msbuild.project.evaluation.stage";
    internal const string PassTagName = "msbuild.project.evaluation.pass";
    internal const string OriginTagName = "msbuild.project.evaluation.origin";
    internal const string SucceededTagName = "msbuild.project.evaluation.succeeded";
    internal const string RecursiveTagName = "msbuild.project.evaluation.item.glob.recursive";
    internal const string SubmissionIdTagName = "msbuild.build.submission.id";
    internal const string IncludeSubmissionIdEnvironmentVariable = "MSBUILD_EVALUATION_METRICS_INCLUDE_SUBMISSION_ID";

    internal const string BuildSubmissionOrigin = "build_submission";
    internal const string OutsideBuildSubmissionOrigin = "outside_build_submission";

    private static int s_disabled;
    private static int s_activeItemGlobs;
    private static int s_itemGlobDisabled;
    private static int s_includeSubmissionId = -1;
    internal static bool? IncludeSubmissionIdOverrideForTests;

    internal static bool IsSubmissionIdEnabled => IncludeSubmissionId();

    internal readonly struct ItemGlobMetricState
    {
        internal ItemGlobMetricState(long startTimestamp, int activeCount, bool concurrencyTracked)
        {
            IsEnabled = true;
            StartTimestamp = startTimestamp;
            ActiveCount = activeCount;
            ConcurrencyTracked = concurrencyTracked;
        }

        internal bool IsEnabled { get; }

        internal long StartTimestamp { get; }

        internal int ActiveCount { get; }

        internal bool ConcurrencyTracked { get; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long EvaluateStart()
    {
        if (Volatile.Read(ref s_disabled) != 0)
        {
            return 0;
        }

        try
        {
            return Instruments.ProjectEvaluationDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            Disable(ex);
            return 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void EvaluateStop(
        long startTimestamp,
        ProjectEvaluationStage stage,
        int submissionId,
        bool succeeded)
    {
        if (Volatile.Read(ref s_disabled) != 0)
        {
            return;
        }

        try
        {
            long endTimestamp = startTimestamp != 0 ? Stopwatch.GetTimestamp() : 0;
            bool countEnabled = Instruments.ProjectEvaluationCount.Enabled;
            bool durationEnabled = startTimestamp != 0 && Instruments.ProjectEvaluationDuration.Enabled;
            if (!countEnabled && !durationEnabled)
            {
                return;
            }

            TagList tags = default;
            AddEvaluationIdentityTags(ref tags, stage, submissionId);
            tags.Add(SucceededTagName, succeeded);

            if (countEnabled)
            {
                Instruments.ProjectEvaluationCount.Add(1, in tags);
            }

            if (durationEnabled)
            {
                double elapsedSeconds = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
                Instruments.ProjectEvaluationDuration.Record(elapsedSeconds, in tags);
            }
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            Disable(ex);
        }
    }

    internal static void EvaluatePass0Stop(long startTimestamp, ProjectEvaluationStage stage, int submissionId) =>
        EvaluatePassStop(startTimestamp, EvaluationPass.InitialProperties, stage, submissionId);

    internal static void EvaluatePass1Stop(long startTimestamp, ProjectEvaluationStage stage, int submissionId) =>
        EvaluatePassStop(startTimestamp, EvaluationPass.Properties, stage, submissionId);

    internal static void EvaluatePass2Stop(long startTimestamp, ProjectEvaluationStage stage, int submissionId) =>
        EvaluatePassStop(startTimestamp, EvaluationPass.ItemDefinitionGroups, stage, submissionId);

    internal static void EvaluatePass3Stop(long startTimestamp, ProjectEvaluationStage stage, int submissionId) =>
        EvaluatePassStop(startTimestamp, EvaluationPass.Items, stage, submissionId);

    internal static void EvaluatePass4Stop(long startTimestamp, ProjectEvaluationStage stage, int submissionId) =>
        EvaluatePassStop(startTimestamp, EvaluationPass.UsingTasks, stage, submissionId);

    internal static void EvaluatePass5Stop(long startTimestamp, ProjectEvaluationStage stage, int submissionId) =>
        EvaluatePassStop(startTimestamp, EvaluationPass.Targets, stage, submissionId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static long EvaluatePassStart()
    {
        if (Volatile.Read(ref s_disabled) != 0)
        {
            return 0;
        }

        try
        {
            return Instruments.ProjectEvaluationPassDuration.Enabled ? Stopwatch.GetTimestamp() : 0;
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            Disable(ex);
            return 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static ItemGlobMetricState ItemGlobStart()
    {
        if (Volatile.Read(ref s_disabled) != 0 || Volatile.Read(ref s_itemGlobDisabled) != 0)
        {
            return default;
        }

        bool concurrencyTracked = false;
        try
        {
            bool durationEnabled = Instruments.ItemGlobDuration.Enabled;
            if (!durationEnabled &&
                !Instruments.ItemGlobRequestCount.Enabled &&
                !Instruments.ItemGlobFileCount.Enabled &&
                !Instruments.ItemGlobExcludeCount.Enabled &&
                !Instruments.ItemGlobConcurrency.Enabled)
            {
                return default;
            }

            concurrencyTracked = Instruments.ItemGlobConcurrency.Enabled;
            int activeCount = concurrencyTracked ? Interlocked.Increment(ref s_activeItemGlobs) : 0;

            return new ItemGlobMetricState(
                durationEnabled ? Stopwatch.GetTimestamp() : 0,
                activeCount,
                concurrencyTracked);
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            if (concurrencyTracked)
            {
                Interlocked.Decrement(ref s_activeItemGlobs);
            }

            DisableItemGlob(ex);
            return default;
        }
    }

    internal static long ItemGlobComplete(ItemGlobMetricState state)
    {
        if (state.StartTimestamp == 0 ||
            Volatile.Read(ref s_disabled) != 0 ||
            Volatile.Read(ref s_itemGlobDisabled) != 0)
        {
            return 0;
        }

        try
        {
            return Stopwatch.GetTimestamp();
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            DisableItemGlob(ex);
            return 0;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ItemGlobStop(
        ItemGlobMetricState state,
        ProjectEvaluationStage stage,
        int submissionId,
        bool recursive,
        int excludeCount,
        int fileCount) =>
        ItemGlobStop(
            state,
            ItemGlobComplete(state),
            stage,
            submissionId,
            recursive,
            excludeCount,
            fileCount);

    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void ItemGlobStop(
        ItemGlobMetricState state,
        long completionTimestamp,
        ProjectEvaluationStage stage,
        int submissionId,
        bool recursive,
        int excludeCount,
        int fileCount)
    {
        if (!state.IsEnabled)
        {
            return;
        }

        try
        {
            if (Volatile.Read(ref s_disabled) != 0 || Volatile.Read(ref s_itemGlobDisabled) != 0)
            {
                return;
            }

            bool countEnabled = Instruments.ItemGlobRequestCount.Enabled;
            bool durationEnabled = completionTimestamp != 0 && Instruments.ItemGlobDuration.Enabled;
            bool fileCountEnabled = Instruments.ItemGlobFileCount.Enabled;
            bool excludeCountEnabled = Instruments.ItemGlobExcludeCount.Enabled;
            bool concurrencyEnabled = state.ConcurrencyTracked && Instruments.ItemGlobConcurrency.Enabled;
            if (!countEnabled && !durationEnabled && !fileCountEnabled && !excludeCountEnabled && !concurrencyEnabled)
            {
                return;
            }

            TagList tags = default;
            AddEvaluationIdentityTags(ref tags, stage, submissionId);
            tags.Add(RecursiveTagName, recursive);

            if (countEnabled)
            {
                Instruments.ItemGlobRequestCount.Add(1, in tags);
            }

            if (durationEnabled)
            {
                double elapsedSeconds = (completionTimestamp - state.StartTimestamp) / (double)Stopwatch.Frequency;
                Instruments.ItemGlobDuration.Record(elapsedSeconds, in tags);
            }

            if (fileCountEnabled)
            {
                Instruments.ItemGlobFileCount.Record(fileCount, in tags);
            }

            if (excludeCountEnabled)
            {
                Instruments.ItemGlobExcludeCount.Record(excludeCount, in tags);
            }

            if (concurrencyEnabled)
            {
                Instruments.ItemGlobConcurrency.Record(state.ActiveCount, in tags);
            }
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            DisableItemGlob(ex);
        }
        finally
        {
            if (state.ConcurrencyTracked)
            {
                Interlocked.Decrement(ref s_activeItemGlobs);
            }
        }
    }

    internal static void ItemGlobCancel(ItemGlobMetricState state)
    {
        if (state.ConcurrencyTracked)
        {
            Interlocked.Decrement(ref s_activeItemGlobs);
        }
    }

    internal static void ResetForTests()
    {
        Volatile.Write(ref s_disabled, 0);
        Volatile.Write(ref s_activeItemGlobs, 0);
        Volatile.Write(ref s_itemGlobDisabled, 0);
        Volatile.Write(ref s_includeSubmissionId, -1);
        IncludeSubmissionIdOverrideForTests = null;
    }

    private static void Disable(Exception ex)
    {
        Volatile.Write(ref s_disabled, 1);
        Debug.WriteLine($"MSBuild evaluation metrics disabled after an instrumentation failure: {ex}");
    }

    private static void DisableItemGlob(Exception ex)
    {
        Volatile.Write(ref s_itemGlobDisabled, 1);
        Debug.WriteLine($"MSBuild item glob evaluation metrics disabled after an instrumentation failure: {ex}");
    }

    private static string GetStageName(ProjectEvaluationStage stage) => stage switch
    {
        ProjectEvaluationStage.Properties => "properties",
        ProjectEvaluationStage.ItemDefinitions => "item_definitions",
        ProjectEvaluationStage.Items => "items",
        ProjectEvaluationStage.UsingTasks => "using_tasks",
        ProjectEvaluationStage.Full => "full",
        _ => "unknown",
    };

    private static string GetPassName(EvaluationPass pass) => pass switch
    {
        EvaluationPass.InitialProperties => "initial_properties",
        EvaluationPass.Properties => "properties",
        EvaluationPass.ItemDefinitionGroups => "item_definitions",
        EvaluationPass.Items => "items",
        EvaluationPass.UsingTasks => "using_tasks",
        EvaluationPass.Targets => "targets",
        _ => "unknown",
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void EvaluatePassStop(
        long startTimestamp,
        EvaluationPass pass,
        ProjectEvaluationStage stage,
        int submissionId)
    {
        if (startTimestamp == 0 || Volatile.Read(ref s_disabled) != 0)
        {
            return;
        }

        try
        {
            long endTimestamp = Stopwatch.GetTimestamp();
            if (!Instruments.ProjectEvaluationPassDuration.Enabled)
            {
                return;
            }

            TagList tags = default;
            AddEvaluationIdentityTags(ref tags, stage, submissionId);
            tags.Add(PassTagName, GetPassName(pass));

            double elapsedSeconds = (endTimestamp - startTimestamp) / (double)Stopwatch.Frequency;
            Instruments.ProjectEvaluationPassDuration.Record(elapsedSeconds, in tags);
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            Disable(ex);
        }
    }

    private static void AddEvaluationIdentityTags(
        ref TagList tags,
        ProjectEvaluationStage stage,
        int submissionId)
    {
        tags.Add(StageTagName, GetStageName(stage));
        tags.Add(
            OriginTagName,
            submissionId != BuildEventContext.InvalidSubmissionId ? BuildSubmissionOrigin : OutsideBuildSubmissionOrigin);
        if (IncludeSubmissionId())
        {
            tags.Add(SubmissionIdTagName, submissionId);
        }
    }

    private static bool IncludeSubmissionId()
    {
        if (IncludeSubmissionIdOverrideForTests.HasValue)
        {
            return IncludeSubmissionIdOverrideForTests.Value;
        }

        int includeSubmissionId = Volatile.Read(ref s_includeSubmissionId);
        if (includeSubmissionId < 0)
        {
            includeSubmissionId = string.Equals(
                Environment.GetEnvironmentVariable(IncludeSubmissionIdEnvironmentVariable),
                "1",
                StringComparison.Ordinal) ? 1 : 0;
            Volatile.Write(ref s_includeSubmissionId, includeSubmissionId);
        }
        return includeSubmissionId != 0;
    }

    private static class Instruments
    {
        private static readonly Meter s_meter = new(MeterName);

        internal static readonly Counter<long> ProjectEvaluationCount = s_meter.CreateCounter<long>(
            ProjectEvaluationCountName,
            unit: "{evaluation}",
            description: "Number of MSBuild project evaluations.");

        internal static readonly Histogram<double> ProjectEvaluationDuration = s_meter.CreateHistogram<double>(
            ProjectEvaluationDurationName,
            unit: "s",
            description: "Duration of MSBuild project evaluations.");

        internal static readonly Histogram<double> ProjectEvaluationPassDuration = s_meter.CreateHistogram<double>(
            ProjectEvaluationPassDurationName,
            unit: "s",
            description: "Duration of MSBuild project evaluation passes.");

        internal static readonly Counter<long> ItemGlobRequestCount = s_meter.CreateCounter<long>(
            ItemGlobRequestCountName,
            unit: "{request}",
            description: "Number of wildcard item include requests during MSBuild project evaluation, including cache hits.");

        internal static readonly Histogram<double> ItemGlobDuration = s_meter.CreateHistogram<double>(
            ItemGlobDurationName,
            unit: "s",
            description: "End-to-end duration of individual wildcard item include requests, including cache lookup and lock wait time.");

        internal static readonly Histogram<long> ItemGlobFileCount = s_meter.CreateHistogram<long>(
            ItemGlobFileCountName,
            unit: "{file}",
            description: "Number of entries returned by individual wildcard item include requests; one entry may be an unexpanded filespec when a search is skipped or fails without throwing.");

        internal static readonly Histogram<long> ItemGlobExcludeCount = s_meter.CreateHistogram<long>(
            ItemGlobExcludeCountName,
            unit: "{pattern}",
            description: "Number of deduplicated project and engine-supplied exclude patterns applied by individual wildcard item include requests.");

        internal static readonly Histogram<long> ItemGlobConcurrency = s_meter.CreateHistogram<long>(
            ItemGlobConcurrencyName,
            unit: "{request}",
            description: "Process-wide number of overlapping wildcard item include requests across all evaluating projects and submissions, observed when each request starts, including requests waiting on cache-key locks.");
    }
}
