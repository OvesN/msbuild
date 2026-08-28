// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared.FileSystem;

#nullable disable

namespace Microsoft.Build.Evaluation.Context
{
    internal enum EvaluationFilesystemTimestampCaptureStatus
    {
        Success,
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
        IncompleteFilesystemObservation,
        UnsupportedProvider,
        UnrootedPath,
        MissingProjectSourceTimestamp,
        UnstableProjectSourceTimestamp,
        IncompleteEnumeration,
        UnsupportedMetadata,
        FailedFilesystemObservation,
        MissingTimestampObservation,
        ConflictingTimestamp,
        FilesystemError,
    }

    internal readonly struct EvaluationFilesystemTimestampEntry
    {
        internal EvaluationFilesystemTimestampEntry(
            string path,
            long lastWriteTimeUtcTicks,
            bool exists,
            EvaluationPathKind kind,
            EvaluationFilesystemTimestampSource sources)
        {
            Path = path;
            LastWriteTimeUtcTicks = lastWriteTimeUtcTicks;
            Exists = exists;
            Kind = kind;
            Sources = sources;
        }

        internal string Path { get; }
        internal long LastWriteTimeUtcTicks { get; }
        internal bool Exists { get; }
        internal EvaluationPathKind Kind { get; }
        internal EvaluationFilesystemTimestampSource Sources { get; }
    }

    internal sealed class EvaluationFilesystemTimestampSnapshot
    {
        private readonly EvaluationFilesystemTimestampEntry[] _entries;

        internal EvaluationFilesystemTimestampSnapshot(
            EvaluationFilesystemTimestampEntry[] entries)
        {
            _entries = entries;
        }

        internal int TimestampCount => _entries.Length;
        internal IReadOnlyList<EvaluationFilesystemTimestampEntry> Entries => _entries;

        internal EvaluationFilesystemTimestampValidationResult Validate() =>
            EvaluationFilesystemTimestampValidator.Validate(this);
    }

    internal readonly struct EvaluationFilesystemTimestampCaptureResult
    {
        internal EvaluationFilesystemTimestampCaptureResult(
            EvaluationFilesystemTimestampCaptureStatus status,
            EvaluationFilesystemTimestampSnapshot snapshot,
            EvaluationFilesystemTimestampFailure failure,
            string path,
            string exceptionType,
            int hResult,
            int timestampReadCount)
        {
            Status = status;
            Snapshot = snapshot;
            Failure = failure;
            Path = path;
            ExceptionType = exceptionType;
            HResult = hResult;
            TimestampReadCount = timestampReadCount;
        }

        internal EvaluationFilesystemTimestampCaptureStatus Status { get; }
        internal EvaluationFilesystemTimestampSnapshot Snapshot { get; }
        internal EvaluationFilesystemTimestampFailure Failure { get; }
        internal string Path { get; }
        internal string ExceptionType { get; }
        internal int HResult { get; }
        internal int TimestampReadCount { get; }
    }

    internal readonly struct EvaluationFilesystemTimestampValidationResult
    {
        internal EvaluationFilesystemTimestampValidationResult(
            EvaluationFilesystemTimestampValidationStatus status,
            int checkedTimestampCount,
            string path,
            long expectedLastWriteTimeUtcTicks,
            long actualLastWriteTimeUtcTicks,
            string exceptionType,
            int hResult)
        {
            Status = status;
            CheckedTimestampCount = checkedTimestampCount;
            Path = path;
            ExpectedLastWriteTimeUtcTicks = expectedLastWriteTimeUtcTicks;
            ActualLastWriteTimeUtcTicks = actualLastWriteTimeUtcTicks;
            ExceptionType = exceptionType;
            HResult = hResult;
        }

        internal EvaluationFilesystemTimestampValidationStatus Status { get; }
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
            if (report is null)
            {
                throw new ArgumentNullException(nameof(report));
            }

            var builder = new SnapshotBuilder();
            if (!report.EvaluationSucceeded)
            {
                return builder.Fail(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    EvaluationFilesystemTimestampFailure.UnsuccessfulEvaluation,
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
                        observation.Exists,
                        observation.Kind,
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
                            observation.Value != s_missingTimestampUtcTicks,
                            EvaluationPathKind.FileOrDirectory,
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
                            EvaluationFilesystemTimestampSource.ProjectSource))
                {
                    return builder.CreateFailureResult();
                }
            }

            foreach (EvaluationPathProbeObservation observation in report.PathProbes)
            {
                if (!builder.TryRequire(
                        observation.Path,
                        observation.Provider,
                        EvaluationFilesystemTimestampSource.PathProbe))
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
                        EvaluationFilesystemTimestampSource.DirectoryEnumeration))
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
                            EvaluationFilesystemTimestampSource.Glob))
                    {
                        return builder.CreateFailureResult();
                    }
                }
            }

            foreach (EvaluationSearchObservation observation in report.Searches)
            {
                if (!observation.Complete ||
                    observation.Candidates.Length != observation.CandidateCount)
                {
                    return builder.Fail(
                        EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                        EvaluationFilesystemTimestampFailure.IncompleteEnumeration,
                        observation.Selected ?? report.ProjectPath);
                }

                foreach (string candidate in observation.Candidates)
                {
                    if (!builder.TryRequire(
                            candidate,
                            provider: null,
                            EvaluationFilesystemTimestampSource.Search))
                    {
                        return builder.CreateFailureResult();
                    }
                }

                if (!string.IsNullOrEmpty(observation.Selected) &&
                    !builder.TryRequire(
                        observation.Selected,
                        provider: null,
                        EvaluationFilesystemTimestampSource.Search))
                {
                    return builder.CreateFailureResult();
                }
            }

            return builder.Capture();
        }

        internal static EvaluationFilesystemTimestampValidationResult Validate(
            EvaluationFilesystemTimestampSnapshot snapshot)
        {
            if (snapshot is null)
            {
                throw new ArgumentNullException(nameof(snapshot));
            }

            IReadOnlyList<EvaluationFilesystemTimestampEntry> entries = snapshot.Entries;
            for (int i = 0; i < entries.Count; i++)
            {
                EvaluationFilesystemTimestampEntry entry = entries[i];
                long actualTimestamp;
                bool actualExists;
                try
                {
                    actualTimestamp =
                        FileSystems.Default.GetLastWriteTimeUtc(entry.Path).Ticks;
                    actualExists =
                        actualTimestamp != s_missingTimestampUtcTicks ||
                        PathExists(entry.Path, entry.Kind);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Failed,
                        i,
                        entry.Path,
                        entry.LastWriteTimeUtcTicks,
                        actualLastWriteTimeUtcTicks: 0,
                        ex.GetType().FullName,
                        ex.HResult);
                }

                if (actualTimestamp != entry.LastWriteTimeUtcTicks ||
                    actualExists != entry.Exists)
                {
                    return new EvaluationFilesystemTimestampValidationResult(
                        EvaluationFilesystemTimestampValidationStatus.Changed,
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
                entries.Count,
                path: null,
                expectedLastWriteTimeUtcTicks: 0,
                actualLastWriteTimeUtcTicks: 0,
                exceptionType: null,
                hResult: 0);
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

            private EvaluationFilesystemTimestampCaptureStatus _failureStatus;
            private EvaluationFilesystemTimestampFailure _failure;
            private string _failurePath;

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
                    true,
                    EvaluationPathKind.File,
                    EvaluationFilesystemTimestampSource.ProjectSource,
                    observation.Provider);
            }

            internal bool TryAddExpected(
                string path,
                long timestampUtcTicks,
                bool exists,
                EvaluationPathKind kind,
                EvaluationFilesystemTimestampSource sources,
                string provider)
            {
                if (!TryValidatePathAndProvider(path, provider))
                {
                    return false;
                }

                if (_timestamps.TryGetValue(path, out PendingTimestamp pending))
                {
                    if (pending.TimestampUtcTicks != timestampUtcTicks ||
                        pending.Exists != exists)
                    {
                        SetFailure(
                            EvaluationFilesystemTimestampCaptureStatus.Changed,
                            EvaluationFilesystemTimestampFailure.ConflictingTimestamp,
                            path);
                        return false;
                    }

                    pending.Sources |= sources;
                    pending.Kind = CombinePathKinds(pending.Kind, kind);
                    return true;
                }

                _timestamps.Add(
                    path,
                    new PendingTimestamp(
                        path,
                        timestampUtcTicks,
                        exists,
                        kind,
                        sources));
                return true;
            }

            internal bool TryRequire(
                string path,
                string provider,
                EvaluationFilesystemTimestampSource requiredSources)
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
                        pending.Exists,
                        pending.Kind,
                        pending.Sources);
                }

                var snapshot = new EvaluationFilesystemTimestampSnapshot(entries);
                EvaluationFilesystemTimestampValidationResult validation =
                    Validate(snapshot);
                if (validation.Status == EvaluationFilesystemTimestampValidationStatus.Valid)
                {
                    return new EvaluationFilesystemTimestampCaptureResult(
                        EvaluationFilesystemTimestampCaptureStatus.Success,
                        snapshot,
                        EvaluationFilesystemTimestampFailure.None,
                        path: null,
                        exceptionType: null,
                        hResult: 0,
                        validation.CheckedTimestampCount);
                }

                if (validation.Status == EvaluationFilesystemTimestampValidationStatus.Changed)
                {
                    return new EvaluationFilesystemTimestampCaptureResult(
                        EvaluationFilesystemTimestampCaptureStatus.Changed,
                        snapshot: null,
                        EvaluationFilesystemTimestampFailure.ConflictingTimestamp,
                        validation.Path,
                        exceptionType: null,
                        hResult: 0,
                        validation.CheckedTimestampCount);
                }

                return new EvaluationFilesystemTimestampCaptureResult(
                    EvaluationFilesystemTimestampCaptureStatus.Failed,
                    snapshot: null,
                    EvaluationFilesystemTimestampFailure.FilesystemError,
                    validation.Path,
                    validation.ExceptionType,
                    validation.HResult,
                    validation.CheckedTimestampCount);
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
                    _failure,
                    _failurePath,
                    exceptionType: null,
                    hResult: 0,
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

                return true;
            }

            private void SetFailure(
                EvaluationFilesystemTimestampCaptureStatus status,
                EvaluationFilesystemTimestampFailure failure,
                string path)
            {
                if (HasFailure)
                {
                    return;
                }

                _failureStatus = status;
                _failure = failure;
                _failurePath = path;
            }

            private bool HasFailure =>
                _failureStatus != EvaluationFilesystemTimestampCaptureStatus.Success;
        }

        private sealed class PendingTimestamp
        {
            internal PendingTimestamp(
                string path,
                long timestampUtcTicks,
                bool exists,
                EvaluationPathKind kind,
                EvaluationFilesystemTimestampSource sources)
            {
                Path = path;
                TimestampUtcTicks = timestampUtcTicks;
                Exists = exists;
                Kind = kind;
                Sources = sources;
            }

            internal string Path { get; }
            internal long TimestampUtcTicks { get; }
            internal bool Exists { get; }
            internal EvaluationPathKind Kind { get; set; }
            internal EvaluationFilesystemTimestampSource Sources { get; set; }
        }

        private static EvaluationPathKind CombinePathKinds(
            EvaluationPathKind first,
            EvaluationPathKind second) =>
            first == second
                ? first
                : EvaluationPathKind.FileOrDirectory;

        private static bool PathExists(
            string path,
            EvaluationPathKind kind)
        {
            return kind switch
            {
                EvaluationPathKind.File => FileSystems.Default.FileExists(path),
                EvaluationPathKind.Directory => FileSystems.Default.DirectoryExists(path),
                _ => FileSystems.Default.FileExists(path) ||
                    FileSystems.Default.DirectoryExists(path),
            };
        }
    }
}
