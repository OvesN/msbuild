// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Evaluation.Context
{
    internal enum EvaluationFilesystemTimestampCaptureStatus
    {
        Success,
        AnalysisOnly,
        Changed,
        Unsupported,
        Failed,
    }

    internal enum EvaluationFilesystemTimestampValidationStatus
    {
        Valid,
        Changed,
        Failed,
    }

    internal enum EvaluationFilesystemTimestampFailure
    {
        None,
        UnsuccessfulEvaluation,
        UnsupportedObservationVersion,
        BlockingObservation,
        IncompleteCategorySet,
        IncompleteReparsePointCheckSet,
        MalformedSnapshot,
        NonCanonicalPath,
        MissingRequestObservation,
        MissingRootProjectSourceObservation,
        IncompleteFilesystemObservation,
        ConflictingObservation,
        ReparsePointTraversal,
        ReparsePointStateUnknown,
        UnsupportedProvider,
        UnrootedPath,
        MissingProjectSourceTimestamp,
        UnstableProjectSourceTimestamp,
        IncompleteEnumeration,
        UnsupportedMetadata,
        FailedFilesystemObservation,
        MissingTimestampObservation,
        MissingExistenceObservation,
        ConflictingTimestamp,
        ExistenceChanged,
        FilesystemError,
    }

    internal readonly struct EvaluationFilesystemTimestampEntry
    {
        internal EvaluationFilesystemTimestampEntry(
            string path,
            long lastWriteTimeUtcTicks,
            EvaluationPathExistence existence,
            EvaluationFilesystemTimestampSource sources)
        {
            Path = path;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            Existence = existence;
            Sources = sources;
        }

        internal string Path { get; }
        internal long LastWriteTimeUtcTicks { get; }
        internal EvaluationPathExistence Existence { get; }
        internal EvaluationFilesystemTimestampSource Sources { get; }
    }

    internal sealed class EvaluationFilesystemTimestampSnapshot
    {
        private readonly EvaluationFilesystemTimestampEntry[] _entries;
        private readonly string[] _reparsePointCheckPaths;

        internal EvaluationFilesystemTimestampSnapshot(
            EvaluationFilesystemTimestampEntry[] entries,
            string[] reparsePointCheckPaths)
        {
            _entries = entries;
            _reparsePointCheckPaths = reparsePointCheckPaths;
        }

        internal int TimestampCount => _entries?.Length ?? 0;
        internal int ReparsePointCheckCount => _reparsePointCheckPaths?.Length ?? 0;
        internal IReadOnlyList<EvaluationFilesystemTimestampEntry> Entries => _entries;
        internal IReadOnlyList<string> ReparsePointCheckPaths => _reparsePointCheckPaths;

        internal EvaluationFilesystemTimestampValidationResult Validate() =>
            EvaluationFilesystemTimestampValidator.Validate(this);

        internal EvaluationFilesystemTimestampValidationResult ValidateReparsePoints() =>
            EvaluationFilesystemTimestampValidator.ValidateReparsePoints(this);
    }

    internal readonly struct EvaluationFilesystemTimestampCaptureResult
    {
        internal EvaluationFilesystemTimestampCaptureResult(
            EvaluationFilesystemTimestampCaptureStatus status,
            EvaluationFilesystemTimestampSnapshot snapshot,
            bool isFilesystemSnapshotAdmissible,
            EvaluationFilesystemTimestampFailure failure,
            string path,
            string exceptionType,
            int hResult,
            int reparsePointProbeCount,
            int timestampReadCount)
        {
            Status = status;
            Snapshot = snapshot;
            IsFilesystemSnapshotAdmissible = isFilesystemSnapshotAdmissible;
            Failure = failure;
            Path = path;
            ExceptionType = exceptionType;
            HResult = hResult;
            ReparsePointProbeCount = reparsePointProbeCount;
            TimestampReadCount = timestampReadCount;
        }

        internal EvaluationFilesystemTimestampCaptureStatus Status { get; }
        internal EvaluationFilesystemTimestampSnapshot Snapshot { get; }
        /// <summary>
        /// Whether strict report admission passed for this filesystem snapshot.
        /// Non-filesystem inputs still require cache-key fields or dependency contracts.
        /// </summary>
        internal bool IsFilesystemSnapshotAdmissible { get; }
        internal EvaluationFilesystemTimestampFailure Failure { get; }
        internal string Path { get; }
        internal string ExceptionType { get; }
        internal int HResult { get; }
        internal int ReparsePointProbeCount { get; }
        internal int TimestampReadCount { get; }
    }

    internal readonly struct EvaluationFilesystemTimestampValidationResult
    {
        internal EvaluationFilesystemTimestampValidationResult(
            EvaluationFilesystemTimestampValidationStatus status,
            EvaluationFilesystemTimestampFailure failure,
            int checkedReparsePointCount,
            int checkedTimestampCount,
            string path,
            long expectedLastWriteTimeUtcTicks,
            long actualLastWriteTimeUtcTicks,
            string exceptionType,
            int hResult)
        {
            Status = status;
            Failure = failure;
            CheckedReparsePointCount = checkedReparsePointCount;
            CheckedTimestampCount = checkedTimestampCount;
            Path = path;
            ExpectedLastWriteTimeUtcTicks = expectedLastWriteTimeUtcTicks;
            ActualLastWriteTimeUtcTicks = actualLastWriteTimeUtcTicks;
            ExceptionType = exceptionType;
            HResult = hResult;
        }

        internal EvaluationFilesystemTimestampValidationStatus Status { get; }
        internal EvaluationFilesystemTimestampFailure Failure { get; }
        internal int CheckedReparsePointCount { get; }
        internal int CheckedTimestampCount { get; }
        internal string Path { get; }
        internal long ExpectedLastWriteTimeUtcTicks { get; }
        internal long ActualLastWriteTimeUtcTicks { get; }
        internal string ExceptionType { get; }
        internal int HResult { get; }
    }

    /// <summary>
    /// Prototype filesystem invalidation based only on observed last-write timestamps.
    /// </summary>
    internal static class EvaluationFilesystemTimestampValidator
    {
        private enum ReparsePointProbeResult
        {
            Missing,
            NotReparsePoint,
            ReparsePoint,
            Failed,
        }

        private static readonly long s_missingTimestampUtcTicks =
            DateTime.FromFileTimeUtc(0).Ticks;

        private const EvaluationObservationReason UnsupportedFilesystemReasons =
            EvaluationObservationReason.AmbiguousNegativeProbe |
            EvaluationObservationReason.PartialEnumeration |
            EvaluationObservationReason.UnversionedSharedCache |
            EvaluationObservationReason.UnversionedFileExistenceCache |
            EvaluationObservationReason.UnversionedGlobCache |
            EvaluationObservationReason.UnversionedDirectoryCache |
            EvaluationObservationReason.ProjectXmlContentNotObserved |
            EvaluationObservationReason.UnversionedProjectRootElementCache |
            EvaluationObservationReason.IncompleteEvaluationStage |
            EvaluationObservationReason.UnrootedPath |
            EvaluationObservationReason.UnversionedCustomProvider |
            EvaluationObservationReason.ParserConfigurationProvenanceUnavailable |
            EvaluationObservationReason.ParsedProjectSourceOnly |
            EvaluationObservationReason.ObservationIncomplete |
            EvaluationObservationReason.UnversionedSourceProvider |
            EvaluationObservationReason.ProjectSourceChangedDuringRead;

        private static readonly string s_defaultFileSystemProvider =
            FileSystems.Default.GetType().AssemblyQualifiedName;
        private static readonly string s_cachingFileSystemProvider =
            typeof(CachingFileSystemWrapper).AssemblyQualifiedName;

        internal static EvaluationFilesystemTimestampCaptureResult Capture(
            EvaluationObservationReport report)
        {
            return Capture(report, requireCacheEligibleReport: true);
        }

        /// <summary>
        /// Captures the filesystem slice for prototype analysis even when unrelated
        /// observation categories prevent cache admission. This method must not be
        /// used for cache admission.
        /// </summary>
        internal static EvaluationFilesystemTimestampCaptureResult CaptureFilesystemSliceForAnalysis(
            EvaluationObservationReport report)
        {
            return Capture(report, requireCacheEligibleReport: false);
        }

        private static EvaluationFilesystemTimestampCaptureResult Capture(
            EvaluationObservationReport report,
            bool requireCacheEligibleReport)
        {
            if (report is null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var builder = new SnapshotBuilder(
                isFilesystemSnapshotAdmissible: requireCacheEligibleReport);
            if (!report.EvaluationSucceeded)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.UnsuccessfulEvaluation,
                    report.ProjectPath);
            }

            if (report.SchemaVersion != EvaluationObservationSession.ObservationSchemaVersion ||
                report.PropertyFunctionClassificationVersion !=
                    EvaluationObservationSession.PropertyFunctionClassificationVersion)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.UnsupportedObservationVersion,
                    report.ProjectPath);
            }

            if (!report.HasCompleteCategorySet)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.IncompleteCategorySet,
                    report.ProjectPath);
            }

            if (requireCacheEligibleReport && report.HasBlockingObservations)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.BlockingObservation,
                    report.ProjectPath);
            }

            if (requireCacheEligibleReport && !HasMatchingRequest(report))
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.MissingRequestObservation,
                    report.ProjectPath);
            }

            if (requireCacheEligibleReport && !HasRootProjectSource(report))
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.MissingRootProjectSourceObservation,
                    report.ProjectPath);
            }

            if ((report.Reasons & EvaluationObservationReason.ConflictingObservation) != 0)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.ConflictingObservation,
                    report.ProjectPath);
            }

            if ((report.Reasons & UnsupportedFilesystemReasons) != 0)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.IncompleteFilesystemObservation,
                    report.ProjectPath);
            }

            foreach (EvaluationCategoryObservation category in report.Categories)
            {
                if (IsFilesystemCategory(category.Category) &&
                    category.State is EvaluationObservationCategoryState.Incomplete or
                        EvaluationObservationCategoryState.Unsupported)
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.IncompleteFilesystemObservation,
                        report.ProjectPath);
                }
            }

            foreach (EvaluationOperationFailureObservation observation in report.OperationFailures)
            {
                if (IsFilesystemCategory(observation.Category))
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.FailedFilesystemObservation,
                        observation.Path);
                }
            }

            foreach (EvaluationProjectSourceObservation observation in report.ProjectSources)
            {
                if (!builder.TryAddProjectSource(observation))
                {
                    return builder.CreateFailureResult();
                }
            }

            foreach (EvaluationFilesystemTimestampObservation observation in report.FilesystemTimestamps)
            {
                if (!builder.TryAddExpected(
                        observation.Path,
                        observation.LastWriteTimeUtcTicks,
                        observation.Existence,
                        observation.Sources,
                        observation.Provider))
                {
                    return builder.CreateFailureResult();
                }
            }

            foreach (EvaluationMetadataObservation observation in report.MetadataReads)
            {
                if (observation.Kind == EvaluationMetadataKind.LastWriteTimeUtc)
                {
                    if (!builder.TryAddExpected(
                            observation.Path,
                            observation.Value,
                            EvaluationPathExistence.Create(
                                EvaluationPathKind.FileOrDirectory,
                                observation.Value != s_missingTimestampUtcTicks),
                            EvaluationFilesystemTimestampSource.Metadata,
                            observation.Provider))
                    {
                        return builder.CreateFailureResult();
                    }

                    continue;
                }

                if (IsPathOnlyMetadata(observation.Kind))
                {
                    continue;
                }

                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.UnsupportedMetadata,
                    observation.Path);
            }

            foreach (EvaluationFileReadObservation observation in report.FileReads)
            {
                if (!builder.TryRequire(
                        observation.Path,
                        observation.Provider,
                        EvaluationFilesystemTimestampSource.FileRead |
                            EvaluationFilesystemTimestampSource.ProjectSource,
                        EvaluationPathKind.File,
                        requiredExists: true))
                {
                    return builder.CreateFailureResult();
                }
            }

            foreach (EvaluationPathProbeObservation observation in report.PathProbes)
            {
                if (!builder.TryRequire(
                        observation.Path,
                        observation.Provider,
                        EvaluationFilesystemTimestampSource.PathProbe,
                        observation.Kind,
                        observation.Exists))
                {
                    return builder.CreateFailureResult();
                }
            }

            foreach (EvaluationDirectoryEnumerationObservation observation in report.DirectoryEnumerations)
            {
                if (observation.Completion != EvaluationEnumerationCompletion.Complete ||
                    observation.SearchOption == System.IO.SearchOption.AllDirectories)
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.IncompleteEnumeration,
                        observation.Path);
                }

                if (!builder.TryRequire(
                        observation.Path,
                        observation.Provider,
                        EvaluationFilesystemTimestampSource.DirectoryEnumeration,
                        EvaluationPathKind.Directory,
                        requiredExists: true))
                {
                    return builder.CreateFailureResult();
                }
            }

            foreach (EvaluationGlobObservation observation in report.Globs)
            {
                if (observation.Failure is not null)
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.IncompleteEnumeration,
                        observation.Directory);
                }

                if (observation.FilesystemTraversalExpected &&
                    observation.TraversedDirectories.Length == 0)
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.MissingTimestampObservation,
                        observation.Directory);
                }

                foreach (string directory in observation.TraversedDirectories)
                {
                    if (!builder.TryRequire(
                            directory,
                            provider: null,
                            EvaluationFilesystemTimestampSource.Glob,
                            EvaluationPathKind.Directory,
                            requiredExists: null))
                    {
                        return builder.CreateFailureResult();
                    }
                }
            }

            foreach (EvaluationSearchObservation observation in report.Searches)
            {
                if (!observation.Complete ||
                    observation.Candidates.Length != observation.CandidateCount ||
                    observation.SelectedPaths.Length != observation.SelectedPathCount)
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.IncompleteEnumeration,
                        observation.SelectedPaths.Length == 0
                            ? report.ProjectPath
                            : observation.SelectedPaths[0]);
                }

                foreach (string candidate in observation.Candidates)
                {
                    bool expectedExists = false;
                    foreach (string selectedPath in observation.SelectedPaths)
                    {
                        if (FileUtilities.PathsEqual(candidate, selectedPath))
                        {
                            expectedExists = true;
                            break;
                        }
                    }

                    if (!builder.TryRequire(
                            candidate,
                            provider: null,
                            EvaluationFilesystemTimestampSource.Search,
                            EvaluationPathKind.File,
                            expectedExists))
                    {
                        return builder.CreateFailureResult();
                    }
                }

                foreach (string selectedPath in observation.SelectedPaths)
                {
                    if (!builder.TryRequire(
                            selectedPath,
                            provider: null,
                            EvaluationFilesystemTimestampSource.Search,
                            EvaluationPathKind.File,
                            requiredExists: true))
                    {
                        return builder.CreateFailureResult();
                    }
                }
            }

            return builder.Capture();
        }

        private static bool HasMatchingRequest(EvaluationObservationReport report) =>
            report.Request is not null &&
            !string.IsNullOrEmpty(report.ProjectPath) &&
            FileUtilities.PathsEqual(report.Request.ProjectPath, report.ProjectPath);

        private static bool HasRootProjectSource(EvaluationObservationReport report)
        {
            if (string.IsNullOrEmpty(report.ProjectPath))
            {
                return false;
            }

            foreach (EvaluationProjectSourceObservation source in report.ProjectSources)
            {
                if (source.Role == EvaluationProjectSourceRole.Root &&
                    source.Outcome == EvaluationProjectSourceOutcome.Parsed &&
                    FileUtilities.PathsEqual(source.Path, report.ProjectPath))
                {
                    return true;
                }
            }

            return false;
        }

        internal static EvaluationFilesystemTimestampValidationResult Validate(
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            EvaluationFilesystemTimestampValidationResult reparsePointValidation =
                ValidateReparsePoints(snapshot);
            return reparsePointValidation.Status == EvaluationFilesystemTimestampValidationStatus.Valid
                ? ValidateTimestampsWithoutReparsePointCheck(
                    snapshot,
                    reparsePointValidation.CheckedReparsePointCount)
                : reparsePointValidation;
        }

        internal static EvaluationFilesystemTimestampValidationResult ValidateReparsePoints(
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            if (!TryValidateReparsePointCheckSet(
                    snapshot,
                    out EvaluationFilesystemTimestampFailure checkSetFailure,
                    out string incompletePath,
                    out Exception checkSetException))
            {
                return new EvaluationFilesystemTimestampValidationResult(
                    EvaluationFilesystemTimestampValidationStatus.Failed,
                    checkSetFailure,
                    checkedReparsePointCount: 0,
                    checkedTimestampCount: 0,
                    incompletePath,
                    expectedLastWriteTimeUtcTicks: 0,
                    actualLastWriteTimeUtcTicks: 0,
                    checkSetException?.GetType().FullName,
                    checkSetException?.HResult ?? 0);
            }

            IReadOnlyList<string> reparsePointCheckPaths =
                snapshot.ReparsePointCheckPaths;
            for (int i = 0; i < reparsePointCheckPaths.Count; i++)
            {
                string path = reparsePointCheckPaths[i];
                ReparsePointProbeResult probeResult =
                    ProbeReparsePoint(path, out Exception exception);
                if (probeResult == ReparsePointProbeResult.ReparsePoint)
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Changed,
                        EvaluationFilesystemTimestampFailure.ReparsePointTraversal,
                        checkedReparsePointCount: i + 1,
                        checkedTimestampCount: 0,
                        path,
                        expectedLastWriteTimeUtcTicks: 0,
                        actualLastWriteTimeUtcTicks: 0,
                        exceptionType: null,
                        hResult: 0);
                }

                if (probeResult == ReparsePointProbeResult.Failed)
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Failed,
                        EvaluationFilesystemTimestampFailure.ReparsePointStateUnknown,
                        checkedReparsePointCount: i + 1,
                        checkedTimestampCount: 0,
                        path,
                        expectedLastWriteTimeUtcTicks: 0,
                        actualLastWriteTimeUtcTicks: 0,
                        exception.GetType().FullName,
                        exception.HResult);
                }
            }

            return new EvaluationFilesystemTimestampValidationResult(
                EvaluationFilesystemTimestampValidationStatus.Valid,
                EvaluationFilesystemTimestampFailure.None,
                reparsePointCheckPaths.Count,
                checkedTimestampCount: 0,
                path: null,
                expectedLastWriteTimeUtcTicks: 0,
                actualLastWriteTimeUtcTicks: 0,
                exceptionType: null,
                hResult: 0);
        }

        /// <summary>
        /// Benchmark-only timestamp scan that omits the required reparse-point check.
        /// Do not use this result for snapshot reuse.
        /// </summary>
        internal static EvaluationFilesystemTimestampValidationResult ValidateTimestampsWithoutReparsePointCheck(
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            return ValidateTimestampsWithoutReparsePointCheck(
                snapshot,
                checkedReparsePointCount: 0);
        }

        private static EvaluationFilesystemTimestampValidationResult ValidateTimestampsWithoutReparsePointCheck(
            EvaluationFilesystemTimestampSnapshot snapshot,
            int checkedReparsePointCount)
        {
            IReadOnlyList<EvaluationFilesystemTimestampEntry> entries = snapshot.Entries;
            if (entries is null)
            {
                return new EvaluationFilesystemTimestampValidationResult(
                    EvaluationFilesystemTimestampValidationStatus.Failed,
                    EvaluationFilesystemTimestampFailure.MalformedSnapshot,
                    checkedReparsePointCount,
                    checkedTimestampCount: 0,
                    path: null,
                    expectedLastWriteTimeUtcTicks: 0,
                    actualLastWriteTimeUtcTicks: 0,
                    exceptionType: null,
                    hResult: 0);
            }

            for (int i = 0; i < entries.Count; i++)
            {
                EvaluationFilesystemTimestampEntry entry = entries[i];
                long actualTimestamp;
                try
                {
                    actualTimestamp =
                        FileSystems.Default.GetLastWriteTimeUtc(entry.Path).Ticks;
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Failed,
                        EvaluationFilesystemTimestampFailure.FilesystemError,
                        checkedReparsePointCount,
                        i + 1,
                        entry.Path,
                        entry.LastWriteTimeUtcTicks,
                        actualLastWriteTimeUtcTicks: 0,
                        ex.GetType().FullName,
                        ex.HResult);
                }

                if (actualTimestamp != entry.LastWriteTimeUtcTicks)
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Changed,
                        EvaluationFilesystemTimestampFailure.ConflictingTimestamp,
                        checkedReparsePointCount,
                        i + 1,
                        entry.Path,
                        entry.LastWriteTimeUtcTicks,
                        actualTimestamp,
                        exceptionType: null,
                        hResult: 0);
                }

                bool existenceChanged;
                try
                {
                    existenceChanged =
                        HasExistenceChanged(
                            entry.Path,
                            entry.Existence,
                            actualTimestamp != s_missingTimestampUtcTicks);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Failed,
                        EvaluationFilesystemTimestampFailure.FilesystemError,
                        checkedReparsePointCount,
                        i + 1,
                        entry.Path,
                        entry.LastWriteTimeUtcTicks,
                        actualLastWriteTimeUtcTicks: actualTimestamp,
                        ex.GetType().FullName,
                        ex.HResult);
                }

                if (existenceChanged)
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Changed,
                        EvaluationFilesystemTimestampFailure.ExistenceChanged,
                        checkedReparsePointCount,
                        i + 1,
                        entry.Path,
                        entry.LastWriteTimeUtcTicks,
                        actualTimestamp,
                        exceptionType: null,
                        hResult: 0);
                }
            }

            return new EvaluationFilesystemTimestampValidationResult(
                EvaluationFilesystemTimestampValidationStatus.Valid,
                EvaluationFilesystemTimestampFailure.None,
                checkedReparsePointCount,
                entries.Count,
                path: null,
                expectedLastWriteTimeUtcTicks: 0,
                actualLastWriteTimeUtcTicks: 0,
                exceptionType: null,
                hResult: 0);
        }

        private static ReparsePointProbeResult ProbeReparsePoint(
            string path,
            out Exception exception)
        {
            exception = null;
            try
            {
                return (FileSystems.Default.GetAttributes(path) &
                    FileAttributes.ReparsePoint) != 0
                    ? ReparsePointProbeResult.ReparsePoint
                    : ReparsePointProbeResult.NotReparsePoint;
            }
            catch (FileNotFoundException)
            {
                return ReparsePointProbeResult.Missing;
            }
            catch (DirectoryNotFoundException)
            {
                return ReparsePointProbeResult.Missing;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                exception = ex;
                return ReparsePointProbeResult.Failed;
            }
        }

        private static bool TryValidateReparsePointCheckSet(
            EvaluationFilesystemTimestampSnapshot snapshot,
            out EvaluationFilesystemTimestampFailure failure,
            out string incompletePath,
            out Exception exception)
        {
            failure = EvaluationFilesystemTimestampFailure.None;
            incompletePath = null;
            exception = null;
            IReadOnlyList<string> paths = snapshot.ReparsePointCheckPaths;
            IReadOnlyList<EvaluationFilesystemTimestampEntry> entries = snapshot.Entries;
            if (paths is null ||
                entries is null)
            {
                failure = EvaluationFilesystemTimestampFailure.MalformedSnapshot;
                return false;
            }

            var pathSet = new HashSet<string>(FileUtilities.PathComparer);
            for (int i = 0; i < paths.Count; i++)
            {
                string path = paths[i];
                if (string.IsNullOrEmpty(path) ||
                    !pathSet.Add(path))
                {
                    failure =
                        EvaluationFilesystemTimestampFailure.IncompleteReparsePointCheckSet;
                    incompletePath = path;
                    return false;
                }

                if (!IsCanonicalPath(path))
                {
                    failure = EvaluationFilesystemTimestampFailure.NonCanonicalPath;
                    incompletePath = path;
                    return false;
                }
            }

            foreach (EvaluationFilesystemTimestampEntry entry in entries)
            {
                if (!IsCanonicalPath(entry.Path))
                {
                    failure = EvaluationFilesystemTimestampFailure.NonCanonicalPath;
                    incompletePath = entry.Path;
                    return false;
                }

                if (!entry.Existence.HasAny)
                {
                    failure = EvaluationFilesystemTimestampFailure.MalformedSnapshot;
                    incompletePath = entry.Path;
                    return false;
                }

                string current = entry.Path;
                while (!string.IsNullOrEmpty(current))
                {
                    if (!pathSet.Contains(current))
                    {
                        failure =
                            EvaluationFilesystemTimestampFailure.IncompleteReparsePointCheckSet;
                        incompletePath = current;
                        return false;
                    }

                    try
                    {
                        string parent = Path.GetDirectoryName(current);
                        if (string.IsNullOrEmpty(parent) ||
                            FileUtilities.PathsEqual(parent, current))
                        {
                            break;
                        }

                        current = parent;
                    }
                    catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                    {
                        failure = EvaluationFilesystemTimestampFailure.NonCanonicalPath;
                        incompletePath = current;
                        exception = ex;
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsCanonicalPath(string path)
        {
            try
            {
                return string.Equals(
                    path,
                    FileUtilities.NormalizePathForObservation(
                        FileUtilities.NormalizePath(path)),
                    FileUtilities.PathComparison);
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                return false;
            }
        }

        private static bool IsDiskProvider(string provider) =>
            string.IsNullOrEmpty(provider) ||
            string.Equals(provider, "Disk", StringComparison.Ordinal) ||
            string.Equals(provider, s_defaultFileSystemProvider, StringComparison.Ordinal) ||
            string.Equals(provider, s_cachingFileSystemProvider, StringComparison.Ordinal);

        private static bool IsFilesystemCategory(EvaluationObservationCategory category) =>
            category is EvaluationObservationCategory.ProjectSource or
                EvaluationObservationCategory.FileContent or
                EvaluationObservationCategory.PathProbe or
                EvaluationObservationCategory.FileMetadata or
                EvaluationObservationCategory.DirectoryEnumeration or
                EvaluationObservationCategory.Glob or
                EvaluationObservationCategory.Search or
                EvaluationObservationCategory.CustomProvider;

        private static bool IsPathOnlyMetadata(EvaluationMetadataKind kind) =>
            kind is EvaluationMetadataKind.ItemFullPath or
                EvaluationMetadataKind.ItemRootDirectory or
                EvaluationMetadataKind.ItemRelativeDirectory or
                EvaluationMetadataKind.ItemDirectory;

        private sealed class SnapshotBuilder
        {
            private readonly Dictionary<string, PendingTimestamp> _timestamps =
                new(FileUtilities.PathComparer);
            private readonly HashSet<string> _reparsePointCheckedPaths =
                new(FileUtilities.PathComparer);
            private readonly bool _isFilesystemSnapshotAdmissible;

            private EvaluationFilesystemTimestampCaptureStatus _failureStatus;
            private EvaluationFilesystemTimestampFailure _failure;
            private string _failurePath;
            private string _failureExceptionType;
            private int _failureHResult;
            private int _reparsePointProbeCount;

            internal SnapshotBuilder(bool isFilesystemSnapshotAdmissible)
            {
                _isFilesystemSnapshotAdmissible = isFilesystemSnapshotAdmissible;
            }

            internal bool TryAddProjectSource(
                EvaluationProjectSourceObservation observation)
            {
                if (!TryValidatePathAndProvider(observation.Path, observation.Provider))
                {
                    return false;
                }

                if (!observation.HasLastWriteTimeUtc)
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.MissingProjectSourceTimestamp,
                        observation.Path);
                    return false;
                }

                if (!observation.TimestampWasStableDuringRead)
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Changed,
                        EvaluationFilesystemTimestampFailure.UnstableProjectSourceTimestamp,
                        observation.Path);
                    return false;
                }

                return TryAddExpected(
                    observation.Path,
                    observation.LastWriteTimeUtcTicks,
                    EvaluationPathExistence.Create(
                        EvaluationPathKind.File,
                        exists: true),
                    EvaluationFilesystemTimestampSource.ProjectSource,
                    observation.Provider);
            }

            internal bool TryAddExpected(
                string path,
                long timestampUtcTicks,
                EvaluationPathExistence existence,
                EvaluationFilesystemTimestampSource sources,
                string provider)
            {
                if (!TryValidatePathAndProvider(path, provider))
                {
                    return false;
                }

                if (!TryRejectReparsePointTraversal(path))
                {
                    return false;
                }

                if (!existence.HasAny)
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.MissingExistenceObservation,
                        path);
                    return false;
                }

                if (!existence.IsConsistentWithTimestamp(
                        timestampUtcTicks != s_missingTimestampUtcTicks))
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.ConflictingObservation,
                        path);
                    return false;
                }

                if (_timestamps.TryGetValue(path, out PendingTimestamp pending))
                {
                    if (pending.TimestampUtcTicks != timestampUtcTicks)
                    {
                        SetFailure(
                            EvaluationFilesystemTimestampCaptureStatus.Changed,
                            EvaluationFilesystemTimestampFailure.ConflictingTimestamp,
                            path);
                        return false;
                    }

                    if (!pending.Existence.TryMerge(
                            existence,
                            out EvaluationPathExistence combinedExistence))
                    {
                        SetFailure(
                            EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                            EvaluationFilesystemTimestampFailure.ConflictingObservation,
                            path);
                        return false;
                    }

                    if (!combinedExistence.IsConsistentWithTimestamp(
                            timestampUtcTicks != s_missingTimestampUtcTicks))
                    {
                        SetFailure(
                            EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                            EvaluationFilesystemTimestampFailure.ConflictingObservation,
                            path);
                        return false;
                    }

                    pending.Sources |= sources;
                    pending.Existence = combinedExistence;
                    return true;
                }

                _timestamps.Add(
                    path,
                    new PendingTimestamp(
                        path,
                        timestampUtcTicks,
                        existence,
                        sources));
                return true;
            }

            private bool TryRejectReparsePointTraversal(string path)
            {
                if (_reparsePointCheckedPaths.Contains(path))
                {
                    return true;
                }

                string parent;
                try
                {
                    parent = Path.GetDirectoryName(path);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Failed,
                        EvaluationFilesystemTimestampFailure.ReparsePointStateUnknown,
                        path,
                        ex);
                    return false;
                }

                if (!string.IsNullOrEmpty(parent) &&
                    !FileUtilities.PathsEqual(parent, path) &&
                    !TryRejectReparsePointTraversal(parent))
                {
                    return false;
                }

                _reparsePointProbeCount++;
                ReparsePointProbeResult probeResult =
                    ProbeReparsePoint(path, out Exception exception);
                if (probeResult == ReparsePointProbeResult.ReparsePoint)
                {
                    // A link already present during capture makes the candidate unsupported.
                    // A link found by the final or reuse validation is reported as changed.
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.ReparsePointTraversal,
                        path);
                    return false;
                }

                if (probeResult == ReparsePointProbeResult.Failed)
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Failed,
                        EvaluationFilesystemTimestampFailure.ReparsePointStateUnknown,
                        path,
                        exception);
                    return false;
                }

                _reparsePointCheckedPaths.Add(path);
                return true;
            }

            internal bool TryRequire(
                string path,
                string provider,
                EvaluationFilesystemTimestampSource requiredSources,
                EvaluationPathKind? requiredKind = null,
                bool? requiredExists = null)
            {
                if (!TryValidatePathAndProvider(path, provider))
                {
                    return false;
                }

                if (!_timestamps.TryGetValue(path, out PendingTimestamp pending) ||
                    (pending.Sources & requiredSources) == 0)
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.MissingTimestampObservation,
                        path);
                    return false;
                }

                if (requiredKind.HasValue)
                {
                    if (!pending.Existence.TryGet(
                            requiredKind.GetValueOrDefault(),
                            out bool observedExists))
                    {
                        SetFailure(
                            EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                            EvaluationFilesystemTimestampFailure.MissingExistenceObservation,
                            path);
                        return false;
                    }

                    if (requiredExists.HasValue &&
                        observedExists != requiredExists.GetValueOrDefault())
                    {
                        SetFailure(
                            EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                            EvaluationFilesystemTimestampFailure.ConflictingObservation,
                            path);
                        return false;
                    }
                }

                return true;
            }

            internal EvaluationFilesystemTimestampCaptureResult Capture()
            {
                if (HasFailure)
                {
                    return CreateFailureResult();
                }

                var entries = new EvaluationFilesystemTimestampEntry[_timestamps.Count];
                int index = 0;
                foreach (PendingTimestamp pending in _timestamps.Values)
                {
                    entries[index++] = new EvaluationFilesystemTimestampEntry(
                        pending.Path,
                        pending.TimestampUtcTicks,
                        pending.Existence,
                        pending.Sources);
                }

                var reparsePointCheckPaths =
                    new string[_reparsePointCheckedPaths.Count];
                _reparsePointCheckedPaths.CopyTo(reparsePointCheckPaths);
                Array.Sort(reparsePointCheckPaths, FileUtilities.PathComparer);

                var snapshot = new EvaluationFilesystemTimestampSnapshot(
                    entries,
                    reparsePointCheckPaths);
                EvaluationFilesystemTimestampValidationResult validation =
                    Validate(snapshot);
                if (validation.Status == EvaluationFilesystemTimestampValidationStatus.Valid)
                {
                    return new EvaluationFilesystemTimestampCaptureResult(
                        _isFilesystemSnapshotAdmissible
                            ? EvaluationFilesystemTimestampCaptureStatus.Success
                            : EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly,
                        snapshot,
                        _isFilesystemSnapshotAdmissible,
                        EvaluationFilesystemTimestampFailure.None,
                        path: null,
                        exceptionType: null,
                        hResult: 0,
                        reparsePointProbeCount: _reparsePointProbeCount +
                            validation.CheckedReparsePointCount,
                        timestampReadCount: validation.CheckedTimestampCount);
                }

                if (validation.Status == EvaluationFilesystemTimestampValidationStatus.Changed)
                {
                    return new EvaluationFilesystemTimestampCaptureResult(
                        EvaluationFilesystemTimestampCaptureStatus.Changed,
                        snapshot: null,
                        isFilesystemSnapshotAdmissible: false,
                        validation.Failure,
                        validation.Path,
                        exceptionType: null,
                        hResult: 0,
                        reparsePointProbeCount: _reparsePointProbeCount +
                            validation.CheckedReparsePointCount,
                        timestampReadCount: validation.CheckedTimestampCount);
                }

                return new EvaluationFilesystemTimestampCaptureResult(
                    EvaluationFilesystemTimestampCaptureStatus.Failed,
                    snapshot: null,
                    isFilesystemSnapshotAdmissible: false,
                    validation.Failure,
                    validation.Path,
                    validation.ExceptionType,
                    validation.HResult,
                    reparsePointProbeCount: _reparsePointProbeCount +
                        validation.CheckedReparsePointCount,
                    timestampReadCount: validation.CheckedTimestampCount);
            }

            internal EvaluationFilesystemTimestampCaptureResult Fail(
                EvaluationFilesystemTimestampCaptureStatus status,
                EvaluationFilesystemTimestampFailure failure,
                string path)
            {
                SetFailure(status, failure, path);
                return CreateFailureResult();
            }

            internal EvaluationFilesystemTimestampCaptureResult CreateFailureResult() =>
                new(
                    _failureStatus,
                    snapshot: null,
                    isFilesystemSnapshotAdmissible: false,
                    _failure,
                    _failurePath,
                    _failureExceptionType,
                    _failureHResult,
                    reparsePointProbeCount: _reparsePointProbeCount,
                    timestampReadCount: 0);

            private bool TryValidatePathAndProvider(string path, string provider)
            {
                if (!IsDiskProvider(provider))
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.UnsupportedProvider,
                        path);
                    return false;
                }

                if (string.IsNullOrEmpty(path) ||
                    !FileUtilities.IsPathFullyQualifiedNoThrow(path))
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.UnrootedPath,
                        path);
                    return false;
                }

                if (!IsCanonicalPath(path))
                {
                    SetFailure(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.NonCanonicalPath,
                        path);
                    return false;
                }

                return true;
            }

            private void SetFailure(
                EvaluationFilesystemTimestampCaptureStatus status,
                EvaluationFilesystemTimestampFailure failure,
                string path,
                Exception exception = null)
            {
                if (HasFailure)
                {
                    return;
                }

                _failureStatus = status;
                _failure = failure;
                _failurePath = path;
                _failureExceptionType = exception?.GetType().FullName;
                _failureHResult = exception?.HResult ?? 0;
            }

            private bool HasFailure =>
                _failureStatus != EvaluationFilesystemTimestampCaptureStatus.Success;
        }

        private sealed class PendingTimestamp
        {
            internal PendingTimestamp(
                string path,
                long timestampUtcTicks,
                EvaluationPathExistence existence,
                EvaluationFilesystemTimestampSource sources)
            {
                Path = path;
                TimestampUtcTicks = timestampUtcTicks;
                Existence = existence;
                Sources = sources;
            }

            internal string Path { get; }
            internal long TimestampUtcTicks { get; }
            internal EvaluationPathExistence Existence { get; set; }
            internal EvaluationFilesystemTimestampSource Sources { get; set; }
        }

        private static bool HasExistenceChanged(
            string path,
            EvaluationPathExistence existence,
            bool timestampIndicatesExistence)
        {
            return
                (existence.FileExists.HasValue &&
                 FileSystems.Default.FileExists(path) !=
                    existence.FileExists.GetValueOrDefault()) ||
                (existence.DirectoryExists.HasValue &&
                 FileSystems.Default.DirectoryExists(path) !=
                    existence.DirectoryExists.GetValueOrDefault()) ||
                (existence.FileOrDirectoryExists.HasValue &&
                 !(timestampIndicatesExistence &&
                   existence.FileOrDirectoryExists == true) &&
                 FileSystems.Default.FileOrDirectoryExists(path) !=
                    existence.FileOrDirectoryExists.GetValueOrDefault());
        }
    }
}
