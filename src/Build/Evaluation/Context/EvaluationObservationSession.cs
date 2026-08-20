// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Build.Construction;
using Microsoft.Build.Framework;
using Microsoft.Build.ObjectModelRemoting;
using Microsoft.Build.Shared.FileSystem;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable disable

namespace Microsoft.Build.Evaluation.Context
{
    internal sealed class EvaluationObservationSession : IEvaluationInputObserver
    {
        private const string ObservationEnvironmentVariable = "MSBUILDPROTOTYPEEVALUATIONOBSERVATION";
        private const int ObservationSchemaVersion = 5;
        private const int PropertyFunctionClassificationVersion = 1;

        [ThreadStatic]
        private static EvaluationObservationSession s_current;

        private static readonly bool s_enabled =
            Environment.GetEnvironmentVariable(ObservationEnvironmentVariable) == "1";
        private static readonly ConditionalWeakTable<ProjectRootElement, ProjectSourceHashCache> s_projectSourceHashes = new();
        private static readonly string s_defaultFileSystemProvider =
            FileSystems.Default.GetType().AssemblyQualifiedName;
        private static readonly HashSet<string> s_knownPureIntrinsicMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Add",
            "Subtract",
            "Multiply",
            "Divide",
            "Modulo",
            "Escape",
            "Unescape",
            "BitwiseOr",
            "BitwiseAnd",
            "BitwiseXor",
            "BitwiseNot",
            "LeftShift",
            "RightShift",
            "RightShiftUnsigned",
            "MakeRelative",
            "ValueOrDefault",
            "ConvertToBase64",
            "ConvertFromBase64",
            "StableStringHash",
            "EnsureTrailingSlash",
            "VersionEquals",
            "VersionNotEquals",
            "VersionGreaterThan",
            "VersionGreaterThanOrEquals",
            "VersionLessThan",
            "VersionLessThanOrEquals",
            "GetTargetFrameworkIdentifier",
            "GetTargetFrameworkVersion",
            "IsTargetFrameworkCompatible",
            "GetTargetPlatformIdentifier",
            "GetTargetPlatformVersion",
            "FilterTargetFrameworks",
            "SubstringByAsciiChars",
        };
        private static readonly HashSet<string> s_knownPurePathMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "ChangeExtension",
            "Combine",
            "EndsInDirectorySeparator",
            "GetDirectoryName",
            "GetExtension",
            "GetFileName",
            "GetFileNameWithoutExtension",
            "GetInvalidFileNameChars",
            "GetInvalidPathChars",
            "GetPathRoot",
            "HasExtension",
            "IsPathFullyQualified",
            "IsPathRooted",
            "Join",
            "TrimEndingDirectorySeparator",
        };
        private static readonly HashSet<string> s_fileMetadataMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "GetAttributes",
            "GetCreationTime",
            "GetCreationTimeUtc",
            "GetLastAccessTime",
            "GetLastAccessTimeUtc",
            "GetLastWriteTime",
            "GetLastWriteTimeUtc",
        };
        private static readonly HashSet<string> s_directoryMetadataMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "GetLastAccessTime",
            "GetLastAccessTimeUtc",
            "GetLastWriteTime",
            "GetLastWriteTimeUtc",
            "GetParent",
        };
        private static readonly HashSet<string> s_fileSystemInfoMetadataMembers = new(StringComparer.OrdinalIgnoreCase)
        {
            "Attributes",
            "CreationTime",
            "CreationTimeUtc",
            "Directory",
            "DirectoryName",
            "Exists",
            "Extension",
            "FullName",
            "LastAccessTime",
            "LastAccessTimeUtc",
            "LastWriteTime",
            "LastWriteTimeUtc",
            "Length",
            "LinkTarget",
            "Name",
            "Parent",
            "Root",
        };

        private static readonly object s_testLock = new();
        private static TestConfiguration s_testConfiguration;

        private readonly int _evaluationId;
        private readonly string _projectPath;
        private readonly bool _allPropertyFunctionsEnabled;
        private readonly bool _retainDetails;
        private readonly ConcurrentDictionary<PathProbeKey, bool> _pathProbes = new();
        private readonly ConcurrentDictionary<EnumerationKey, EvaluationDirectoryEnumerationObservation> _directoryEnumerations = new();
        private readonly ConcurrentDictionary<MetadataKey, EvaluationMetadataObservation> _metadataReads = new();
        private readonly ConcurrentDictionary<FileReadKey, EvaluationFileReadObservation> _fileReads = new();
        private EvaluationRequestObservation _request;
        private readonly Dictionary<string, EvaluationProjectSourceObservation> _projectSources = new(FileUtilities.PathComparer);
        private readonly Dictionary<string, EvaluationGlobObservation> _globs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvaluationSearchObservation> _searches = new(StringComparer.Ordinal);
        private readonly Dictionary<EnvironmentKey, EvaluationEnvironmentObservation> _environment = new();
        private readonly Dictionary<string, EvaluationExternalInputObservation> _externalInputs = new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvaluationPropertyFunctionObservation> _propertyFunctions = new(StringComparer.Ordinal);
        private readonly List<EvaluationSdkResolutionObservation> _sdkResolutions = [];
        private readonly Dictionary<string, EvaluationTaskRegistrationObservation> _taskRegistrations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, EvaluationSideEffectObservation> _sideEffects = new(StringComparer.Ordinal);
        private readonly object _observationLock = new();

        private long _reasons;
        private long _observedCategories;
        private long _incompleteCategories;
        private long _unsupportedCategories;
        private int _completed;
        private int _propertyFunctionInvocationId;
        private int _suppressDirectoryEnumerations;
        private TestConfiguration _testConfiguration;

        private EvaluationObservationSession(
            int evaluationId,
            string projectPath,
            ProjectEvaluationStage evaluationStage,
            EvaluationContext.SharingPolicy sharingPolicy,
            bool hasDirectoryCache,
            TestConfiguration testConfiguration)
        {
            _evaluationId = evaluationId;
            _reasons = 0;
            _projectPath = NormalizePath(projectPath);
            _testConfiguration = testConfiguration;
            _allPropertyFunctionsEnabled = FeatureSwitches.EnableAllPropertyFunctions;
            _retainDetails = testConfiguration?.RetainDetails ?? false;
            MarkCategory(EvaluationObservationCategory.Request, EvaluationObservationCategoryState.Observed);

            if (_allPropertyFunctionsEnabled)
            {
                AddReason(EvaluationObservationReason.AllPropertyFunctionsEnabled);
                MarkCategory(EvaluationObservationCategory.PropertyFunction, EvaluationObservationCategoryState.Unsupported);
            }

            if (sharingPolicy == EvaluationContext.SharingPolicy.Shared)
            {
                AddReason(EvaluationObservationReason.UnversionedSharedCache);
                MarkCategory(EvaluationObservationCategory.SharedCache, EvaluationObservationCategoryState.Incomplete);
            }

            if (evaluationStage != ProjectEvaluationStage.Full)
            {
                AddReason(EvaluationObservationReason.IncompleteEvaluationStage);
            }

            if (Traits.Instance.CacheFileExistence)
            {
                AddReason(EvaluationObservationReason.UnversionedFileExistenceCache);
                MarkCategory(EvaluationObservationCategory.SharedCache, EvaluationObservationCategoryState.Incomplete);
            }

            if (Traits.Instance.MSBuildCacheFileEnumerations)
            {
                AddReason(EvaluationObservationReason.UnversionedGlobCache);
                MarkCategory(EvaluationObservationCategory.SharedCache, EvaluationObservationCategoryState.Incomplete);
            }

            if (hasDirectoryCache)
            {
                AddReason(EvaluationObservationReason.UnversionedDirectoryCache);
                MarkCategory(EvaluationObservationCategory.CustomProvider, EvaluationObservationCategoryState.Incomplete);
            }
        }

        internal static EvaluationObservationSession TryCreate(
            int evaluationId,
            string projectPath,
            ProjectEvaluationStage evaluationStage,
            EvaluationContext.SharingPolicy sharingPolicy,
            bool hasDirectoryCache)
        {
            TestConfiguration testConfiguration = Volatile.Read(ref s_testConfiguration);
            bool enabled = testConfiguration?.Enabled ?? s_enabled;

            return enabled
                ? new EvaluationObservationSession(
                    evaluationId,
                    projectPath,
                    evaluationStage,
                    sharingPolicy,
                    hasDirectoryCache,
                    testConfiguration)
                : null;
        }

        internal static EvaluationObservationSession CreateForTests(int evaluationId = 1)
        {
            return new EvaluationObservationSession(
                evaluationId,
                projectPath: null,
                ProjectEvaluationStage.Full,
                EvaluationContext.SharingPolicy.Isolated,
                hasDirectoryCache: false,
                testConfiguration: new TestConfiguration(
                    enabled: true,
                    reportCreated: null,
                    retainDetails: true));
        }

        internal static EvaluationObservationSession Current => s_current;
        internal bool ShouldRecordDirectoryEnumeration => Volatile.Read(ref _suppressDirectoryEnumerations) == 0;
        bool IEvaluationInputObserver.RetainDetails => _retainDetails;

        internal static bool IsEnabled
        {
            get
            {
                TestConfiguration testConfiguration = Volatile.Read(ref s_testConfiguration);
                return testConfiguration?.Enabled ?? s_enabled;
            }
        }

        internal IDisposable Enter()
        {
            EvaluationObservationSession previous = s_current;
            s_current = this;
            return new CurrentScope(previous, EvaluationInputObserver.Enter(this));
        }

        internal static DirectoryEnumerationSuppressionScope SuppressDirectoryEnumerations()
        {
            EvaluationObservationSession session = s_current;
            if (session is not null)
            {
                Interlocked.Increment(ref session._suppressDirectoryEnumerations);
            }

            return new DirectoryEnumerationSuppressionScope(session);
        }

        void IEvaluationInputObserver.RecordPathProbe(
            string path,
            EvaluationPathProbeKind kind,
            bool exists)
        {
            RecordProbe(path, ConvertPathKind(kind), exists);
        }

        void IEvaluationInputObserver.RecordAmbiguousPathProbe(
            string path,
            EvaluationPathProbeKind kind)
        {
            RecordProbe(path, ConvertPathKind(kind), exists: false);
            AddReason(EvaluationObservationReason.AmbiguousNegativeProbe);
        }

        void IEvaluationInputObserver.RecordItemMetadata(
            string itemSpec,
            string modifier,
            string baseDirectory,
            string value)
        {
            RecordItemMetadata(itemSpec, modifier, baseDirectory, value);
        }

        void IEvaluationInputObserver.RecordPathAdjustment(
            string value,
            string baseDirectory,
            string result)
        {
            RecordExternalInput(
                EvaluationExternalInputKind.Ambient,
                "UnixPathAdjustment",
                string.Concat(value, "|Base=", baseDirectory),
                result);
        }

        void IEvaluationInputObserver.RecordSearch(
            string kind,
            string request,
            IReadOnlyList<string> candidates,
            int candidateCount,
            string candidatesFingerprint,
            string selected)
        {
            RecordSearch(
                kind,
                request,
                _retainDetails ? CopyStrings(candidates) : [],
                candidateCount,
                candidatesFingerprint,
                selected,
                complete: true);
        }

        internal static IDisposable TestOnlyConfigure(
            bool enabled,
            Action<EvaluationObservationReport> reportCreated = null,
            bool retainDetails = true)
        {
            var configuration = new TestConfiguration(enabled, reportCreated, retainDetails);
            lock (s_testLock)
            {
                Assumed.Null(s_testConfiguration, "A test observation scope is already active.");
                Volatile.Write(ref s_testConfiguration, configuration);
            }

            return new TestScope(configuration);
        }

        internal bool IsCompleted => Volatile.Read(ref _completed) != 0;
        internal bool RetainDetails => _retainDetails;

        internal int TestOnlyRetainedObservationCount
        {
            get
            {
                lock (_observationLock)
                {
                    return _pathProbes.Count +
                        _directoryEnumerations.Count +
                        _metadataReads.Count +
                        _fileReads.Count +
                        (_request is null ? 0 : 1) +
                        _projectSources.Count +
                        _globs.Count +
                        _searches.Count +
                        _environment.Count +
                        _externalInputs.Count +
                        _propertyFunctions.Count +
                        _sdkResolutions.Count +
                        _taskRegistrations.Count +
                        _sideEffects.Count;
                }
            }
        }

        internal void RecordRequest(EvaluationRequestObservation request)
        {
            if (request is null)
            {
                return;
            }

            lock (_observationLock)
            {
                if (IsCompleted)
                {
                    return;
                }

                if (_request is not null)
                {
                    AddReason(EvaluationObservationReason.ConflictingObservation);
                    return;
                }

                _request = request;
            }
        }

        internal void RecordProjectSource(ProjectRootElement source, EvaluationProjectSourceRole role)
        {
            if (source is null)
            {
                return;
            }

            MarkCategory(EvaluationObservationCategory.ProjectSource, EvaluationObservationCategoryState.Observed);
            Record(
                () =>
                {
                    ProjectRootElementLink link = source.RootLink;
                    string path = source.FullPath is null
                        ? string.Concat(
                            "inmemory://",
                            RuntimeHelpers.GetHashCode(source).ToString("x", CultureInfo.InvariantCulture))
                        : NormalizePath(source.FullPath);
                    string provider = link is not null
                        ? link.GetType().AssemblyQualifiedName
                        : source.EvaluationObservationSourceKind;
                    string sourceHash = source.EvaluationObservationSourceHash;
                    string hash;
                    if (link is not null)
                    {
                        hash = null;
                    }
                    else
                    {
                        try
                        {
                            hash = sourceHash ?? GetProjectSourceHash(source);
                        }
                        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                        {
                            AddReason(EvaluationObservationReason.ProjectXmlContentNotObserved);
                            hash = null;
                        }
                    }

                    var observation = new EvaluationProjectSourceObservation(
                        role,
                        path,
                        source.Version,
                        hash,
                        link is not null
                            ? EvaluationContentHashKind.Unknown
                            : sourceHash is not null
                            ? EvaluationContentHashKind.RawBytes
                            : EvaluationContentHashKind.ParsedXml,
                        source.Encoding?.WebName,
                        provider);
                    string key = string.Concat(((int)role).ToString(CultureInfo.InvariantCulture), "\0", path ?? source.GetHashCode().ToString(CultureInfo.InvariantCulture));

                    bool hadPriorObservation = _projectSources.TryGetValue(
                        key,
                        out EvaluationProjectSourceObservation prior);
                    if (hadPriorObservation &&
                        (prior.Version != observation.Version ||
                         !string.Equals(prior.ContentHash, observation.ContentHash, StringComparison.Ordinal)))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                    else
                    {
                        _projectSources[key] = observation;
                    }

                    if (source.FullPath is not null && hash is not null)
                    {
                        bool hasRawSourceHash = sourceHash is not null;
                        RecordFileRead(
                            path,
                            hash,
                            isVerifiable: hasRawSourceHash,
                            hashKind: hasRawSourceHash
                                ? EvaluationContentHashKind.RawBytes
                                : EvaluationContentHashKind.ParsedXml,
                            provider: provider);
                    }

                    if (link is null &&
                        source.FullPath is not null &&
                        sourceHash is null)
                    {
                        AddReason(EvaluationObservationReason.ParsedProjectSourceOnly);
                        AddReason(EvaluationObservationReason.UnversionedProjectRootElementCache);
                    }

                    if (source.EvaluationObservationSourceKind is "XmlReader" or "Document" or "Unknown")
                    {
                        AddReason(EvaluationObservationReason.UnversionedSourceProvider);
                    }
                });
        }

        internal void RecordGlob(
            string role,
            string directory,
            string include,
            IReadOnlyList<string> excludes,
            IReadOnlyList<string> results,
            bool resultsEscaped,
            bool wasLazy,
            bool driveEnumerating,
            string failure)
        {
            MarkCategory(
                EvaluationObservationCategory.Glob,
                failure is null
                    ? EvaluationObservationCategoryState.Observed
                    : EvaluationObservationCategoryState.Incomplete);
            Record(
                () =>
                {
                    string[] excludeSnapshot = _retainDetails ? CopyStrings(excludes) : [];
                    int excludeCount = excludes?.Count ?? 0;
                    string excludesFingerprint = ComputeStringSequenceHash(excludes);
                    string[] resultSnapshot = _retainDetails ? CopyStrings(results) : [];
                    int resultCount = results?.Count ?? 0;
                    string resultsFingerprint = ComputeStringSequenceHash(results);
                    string normalizedDirectory = NormalizePath(directory);
                    var observation = new EvaluationGlobObservation(
                        role,
                        normalizedDirectory,
                        include,
                        excludeSnapshot,
                        excludeCount,
                        excludesFingerprint,
                        resultSnapshot,
                        resultCount,
                        resultsFingerprint,
                        resultsEscaped,
                        wasLazy,
                        driveEnumerating,
                        failure);
                    string key = string.Concat(
                        role,
                        "\0",
                        normalizedDirectory,
                        "\0",
                        include,
                        "\0",
                        excludesFingerprint);

                    if (_globs.TryGetValue(key, out EvaluationGlobObservation prior) &&
                        !GlobResultsEqual(prior, observation))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                    else
                    {
                        _globs[key] = observation;
                    }

                    if (failure is not null)
                    {
                        AddReason(EvaluationObservationReason.ExternalOperationFailure);
                    }
                });
        }

        internal void RecordSearch(
            string kind,
            string request,
            IReadOnlyList<string> candidates,
            string selected,
            bool complete)
        {
            RecordSearch(
                kind,
                request,
                _retainDetails ? CopyStrings(candidates) : [],
                candidates?.Count ?? 0,
                ComputeStringSequenceHash(candidates),
                selected,
                complete);
        }

        private void RecordSearch(
            string kind,
            string request,
            string[] candidates,
            int candidateCount,
            string candidatesFingerprint,
            string selected,
            bool complete)
        {
            MarkCategory(
                EvaluationObservationCategory.Search,
                complete
                    ? EvaluationObservationCategoryState.Observed
                    : EvaluationObservationCategoryState.Incomplete);
            Record(
                () =>
                {
                    var observation = new EvaluationSearchObservation(
                        kind,
                        request,
                        candidates,
                        candidateCount,
                        candidatesFingerprint,
                        selected,
                        complete);
                    string key = string.Concat(kind, "\0", request);

                    if (_searches.TryGetValue(key, out EvaluationSearchObservation prior) &&
                        (!string.Equals(prior.Selected, selected, StringComparison.Ordinal) ||
                         prior.CandidateCount != candidateCount ||
                         !string.Equals(prior.CandidatesFingerprint, candidatesFingerprint, StringComparison.Ordinal)))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                    else
                    {
                        _searches[key] = observation;
                    }

                    if (!complete)
                    {
                        AddReason(EvaluationObservationReason.OpaqueExternalInput);
                    }
                });
        }

        internal void RecordEnvironment(
            string name,
            EvaluationEnvironmentSource source,
            bool present,
            string value)
        {
            if (string.IsNullOrEmpty(name))
            {
                return;
            }

            MarkCategory(
                source is EvaluationEnvironmentSource.Imported or
                    EvaluationEnvironmentSource.MissingImported or
                    EvaluationEnvironmentSource.SdkInjected
                    ? EvaluationObservationCategory.ImportedEnvironment
                    : EvaluationObservationCategory.LiveEnvironment,
                EvaluationObservationCategoryState.Observed);
            Record(
                () =>
                {
                    var key = new EnvironmentKey(source, name);
                    var observation = new EvaluationEnvironmentObservation(name, source, present, value);
                    if (_environment.TryGetValue(key, out EvaluationEnvironmentObservation prior) &&
                        (prior.Present != present || !string.Equals(prior.Value, value, StringComparison.Ordinal)))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                    else
                    {
                        _environment[key] = observation;
                    }
                });
        }

        internal void RecordExternalInput(
            EvaluationExternalInputKind kind,
            string operation,
            string request,
            object result)
        {
            RecordExternalInputCore(kind, operation, request, SerializeValue(result));
        }

        private void RecordExternalInputCore(
            EvaluationExternalInputKind kind,
            string operation,
            string request,
            string serializedResult)
        {
            MarkCategory(
                kind switch
                {
                    EvaluationExternalInputKind.Registry => EvaluationObservationCategory.Registry,
                    EvaluationExternalInputKind.Toolset => EvaluationObservationCategory.Toolset,
                    EvaluationExternalInputKind.Sdk => EvaluationObservationCategory.SdkResolution,
                    EvaluationExternalInputKind.Search => EvaluationObservationCategory.Search,
                    EvaluationExternalInputKind.Environment => EvaluationObservationCategory.LiveEnvironment,
                    _ => EvaluationObservationCategory.Request,
                },
                EvaluationObservationCategoryState.Observed);
            Record(
                () =>
                {
                    string key = string.Concat(((int)kind).ToString(CultureInfo.InvariantCulture), "\0", operation, "\0", request);
                    var observation = new EvaluationExternalInputObservation(kind, operation, request, serializedResult);
                    if (_externalInputs.TryGetValue(key, out EvaluationExternalInputObservation prior) &&
                        !string.Equals(prior.Result, serializedResult, StringComparison.Ordinal))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                    else
                    {
                        _externalInputs[key] = observation;
                    }
                });
        }

        internal void RecordItemMetadata(
            string itemSpec,
            string metadataName,
            string baseDirectory,
            string value)
        {
            EvaluationMetadataKind kind = metadataName switch
            {
                "ModifiedTime" => EvaluationMetadataKind.ItemModifiedTime,
                "CreatedTime" => EvaluationMetadataKind.ItemCreatedTime,
                "AccessedTime" => EvaluationMetadataKind.ItemAccessedTime,
                "FullPath" => EvaluationMetadataKind.ItemFullPath,
                "RootDir" => EvaluationMetadataKind.ItemRootDirectory,
                "RelativeDir" => EvaluationMetadataKind.ItemRelativeDirectory,
                "Directory" => EvaluationMetadataKind.ItemDirectory,
                _ => EvaluationMetadataKind.PropertyFunction,
            };

            RecordMetadata(itemSpec, kind, value, baseDirectory, metadataName);
        }

        internal void RecordPropertyFunction(
            Type receiverType,
            string member,
            object instance,
            object[] arguments,
            object result,
            bool succeeded = true)
        {
            try
            {
                RecordPropertyFunctionCore(receiverType, member, instance, arguments, result, succeeded);
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        private void RecordPropertyFunctionCore(
            Type receiverType,
            string member,
            object instance,
            object[] arguments,
            object result,
            bool succeeded)
        {
            EvaluationPropertyFunctionEffect effects = ClassifyPropertyFunction(receiverType, member);
            if (succeeded && effects == EvaluationPropertyFunctionEffect.Pure)
            {
                return;
            }

            string[] serializedArguments = SerializeArguments(arguments);
            string serializedResult =
                !succeeded
                    ? "<failed>"
                    : (effects & EvaluationPropertyFunctionEffect.FileContent) != 0 &&
                        (effects & EvaluationPropertyFunctionEffect.SideEffect) == 0
                    ? "<file-content>"
                    : (effects & EvaluationPropertyFunctionEffect.DirectoryEnumeration) != 0
                        ? "<directory-enumeration>"
                        : SerializeValue(result);
            string receiverName = receiverType?.FullName ?? instance?.GetType().FullName ?? "<unknown>";
            string instanceIdentity = instance is FileSystemInfo fileSystemInfo
                ? fileSystemInfo.FullName
                : SerializeValue(instance);

            if (result is IEnumerable and not string and not ICollection)
            {
                effects |= EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            MarkCategory(
                EvaluationObservationCategory.PropertyFunction,
                (effects & EvaluationPropertyFunctionEffect.OpaqueUnsupported) != 0
                    ? EvaluationObservationCategoryState.Unsupported
                    : EvaluationObservationCategoryState.Observed);

            Record(
                () =>
                {
                    bool uniqueInvocation =
                        (effects & (EvaluationPropertyFunctionEffect.Volatile | EvaluationPropertyFunctionEffect.SideEffect)) != 0;
                    string key = string.Concat(
                        receiverName,
                        "\0",
                        member,
                        "\0",
                        instanceIdentity,
                        "\0",
                        string.Join("\0", serializedArguments),
                        "\0",
                        succeeded ? "success" : "failure",
                        uniqueInvocation
                            ? string.Concat("\0", Interlocked.Increment(ref _propertyFunctionInvocationId).ToString(CultureInfo.InvariantCulture))
                            : string.Empty);
                    var observation = new EvaluationPropertyFunctionObservation(
                        receiverName,
                        member,
                        instanceIdentity,
                        effects,
                        serializedArguments,
                        serializedResult,
                        succeeded);
                    if (_propertyFunctions.TryGetValue(key, out EvaluationPropertyFunctionObservation prior) &&
                        !string.Equals(prior.Result, serializedResult, StringComparison.Ordinal))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                    else
                    {
                        _propertyFunctions[key] = observation;
                    }

                    if ((effects & EvaluationPropertyFunctionEffect.Volatile) != 0)
                    {
                        AddReason(EvaluationObservationReason.UnsupportedVolatileInput);
                        MarkCategory(EvaluationObservationCategory.VolatileOrSideEffect, EvaluationObservationCategoryState.Unsupported);
                    }

                    if ((effects & EvaluationPropertyFunctionEffect.SideEffect) != 0)
                    {
                        AddReason(EvaluationObservationReason.EvaluationSideEffect);
                        MarkCategory(EvaluationObservationCategory.VolatileOrSideEffect, EvaluationObservationCategoryState.Unsupported);
                    }

                    if ((effects & EvaluationPropertyFunctionEffect.OpaqueUnsupported) != 0)
                    {
                        AddReason(EvaluationObservationReason.UnclassifiedPropertyFunction);
                    }
                });

            if (succeeded)
            {
                RecordTypedPropertyFunction(
                    receiverType,
                    member,
                    instance,
                    arguments,
                    result,
                    effects,
                    serializedArguments,
                    serializedResult);
            }
        }

        internal void RecordSdkResolution(
            SdkReference sdk,
            SdkResult result,
            bool fromCache)
        {
            if (sdk is null)
            {
                return;
            }

            MarkCategory(EvaluationObservationCategory.SdkResolution, EvaluationObservationCategoryState.Observed);
            Record(
                () =>
                {
                    _sdkResolutions.Add(new EvaluationSdkResolutionObservation(
                        sdk.Name,
                        sdk.Version,
                        sdk.MinimumVersion,
                        result?.Success ?? false,
                        result?.Path,
                        result?.Version,
                        fromCache,
                        CopyStrings(result?.AdditionalPaths),
                        CreateNamedValueSnapshot(result?.PropertiesToAdd, "SdkProperty"),
                        CreateSdkItemSnapshot(result?.ItemsToAdd),
                        CreateNamedValueSnapshot(result?.EnvironmentVariablesToAdd, "SdkEnvironment"),
                        CopyStrings(result?.Warnings),
                        CopyStrings(result?.Errors)));
                });
        }

        internal void RecordSdkRequest(
            SdkReference sdk,
            string projectPath,
            string solutionPath,
            bool interactive,
            bool isRunningInVisualStudio)
        {
            if (sdk is null)
            {
                return;
            }

            RecordExternalInputCore(
                EvaluationExternalInputKind.Sdk,
                "SdkRequest",
                string.Concat(
                    sdk.Name,
                    "|", sdk.Version,
                    "|", sdk.MinimumVersion,
                    "|", projectPath,
                    "|", solutionPath),
                string.Concat(
                    "Interactive=", interactive.ToString(CultureInfo.InvariantCulture),
                    ";VisualStudio=", isRunningInVisualStudio.ToString(CultureInfo.InvariantCulture)));
        }

        internal void RecordTaskRegistration(
            string taskName,
            string taskFactory,
            string assemblyFile,
            string assemblyName,
            string runtime,
            string architecture,
            bool isOverride)
        {
            MarkCategory(EvaluationObservationCategory.TaskRegistration, EvaluationObservationCategoryState.Observed);
            Record(
                () =>
                {
                    var observation = new EvaluationTaskRegistrationObservation(
                        taskName,
                        taskFactory,
                        NormalizePath(assemblyFile),
                        assemblyName,
                        runtime,
                        architecture,
                        isOverride);
                    string key = string.Concat(taskName, "\0", taskFactory, "\0", observation.AssemblyFile, "\0", assemblyName);
                    _taskRegistrations[key] = observation;
                });
        }

        internal void RecordSideEffect(string kind, string identity, object value)
        {
            RecordSideEffectCore(kind, identity, SerializeValue(value));
        }

        private void RecordSideEffectCore(string kind, string identity, string serializedValue)
        {
            MarkCategory(EvaluationObservationCategory.VolatileOrSideEffect, EvaluationObservationCategoryState.Unsupported);
            Record(
                () =>
                {
                    string key = string.Concat(kind, "\0", identity);
                    _sideEffects[key] = new EvaluationSideEffectObservation(kind, identity, serializedValue);
                    AddReason(EvaluationObservationReason.EvaluationSideEffect);
                });
        }

        internal void RecordProbe(
            string path,
            EvaluationPathKind kind,
            bool exists,
            string provider = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            MarkCategory(EvaluationObservationCategory.PathProbe, EvaluationObservationCategoryState.Observed);
            try
            {
                lock (_observationLock)
                {
                    if (IsCompleted)
                    {
                        return;
                    }

                    var key = new PathProbeKey(
                        NormalizePath(path),
                        kind,
                        provider ?? s_defaultFileSystemProvider);
                    if (!_pathProbes.TryAdd(key, exists) &&
                        _pathProbes.TryGetValue(key, out bool priorResult) &&
                        priorResult != exists)
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        internal void RecordEnumeration(
            string path,
            string searchPattern,
            SearchOption searchOption,
            EvaluationEnumerationKind kind,
            IReadOnlyList<string> entries,
            EvaluationEnumerationCompletion completion,
            string provider = null)
        {
            RecordEnumerationCore(
                path,
                searchPattern,
                searchOption,
                kind,
                _retainDetails ? CopyStrings(entries) : [],
                entries?.Count ?? 0,
                ComputeStringSequenceHash(entries),
                completion,
                provider);
        }

        internal void RecordEnumeration(
            string path,
            string searchPattern,
            SearchOption searchOption,
            EvaluationEnumerationKind kind,
            string[] entries,
            int entryCount,
            string entriesHash,
            EvaluationEnumerationCompletion completion,
            string provider = null)
        {
            RecordEnumerationCore(
                path,
                searchPattern,
                searchOption,
                kind,
                entries,
                entryCount,
                entriesHash,
                completion,
                provider);
        }

        private void RecordEnumerationCore(
            string path,
            string searchPattern,
            SearchOption searchOption,
            EvaluationEnumerationKind kind,
            string[] entries,
            int entryCount,
            string entriesHash,
            EvaluationEnumerationCompletion completion,
            string provider)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            MarkCategory(
                EvaluationObservationCategory.DirectoryEnumeration,
                completion == EvaluationEnumerationCompletion.Complete
                    ? EvaluationObservationCategoryState.Observed
                    : EvaluationObservationCategoryState.Incomplete);
            try
            {
                lock (_observationLock)
                {
                    if (IsCompleted)
                    {
                        return;
                    }

                    var key = new EnumerationKey(
                        NormalizePath(path),
                        searchPattern ?? "*",
                        searchOption,
                        kind,
                        provider ?? s_defaultFileSystemProvider);
                    var observation = new EvaluationDirectoryEnumerationObservation(
                        key.Path,
                        key.SearchPattern,
                        key.SearchOption,
                        key.Kind,
                        entries,
                        entryCount,
                        entriesHash,
                        key.Provider,
                        completion);

                    if (!_directoryEnumerations.TryAdd(key, observation) &&
                        _directoryEnumerations.TryGetValue(key, out EvaluationDirectoryEnumerationObservation priorObservation) &&
                        !EnumerationResultsEqual(priorObservation, observation))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }

                    if (completion != EvaluationEnumerationCompletion.Complete)
                    {
                        AddReason(completion == EvaluationEnumerationCompletion.Failure
                            ? EvaluationObservationReason.ExternalOperationFailure
                            : EvaluationObservationReason.PartialEnumeration);
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        internal void RecordMetadata(
            string path,
            EvaluationMetadataKind kind,
            long value,
            string provider = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            MarkCategory(EvaluationObservationCategory.FileMetadata, EvaluationObservationCategoryState.Observed);
            try
            {
                string normalizedPath = NormalizePath(path);
                RecordMetadataCore(
                    normalizedPath,
                    kind,
                    new EvaluationMetadataObservation(
                        normalizedPath,
                        kind,
                        value,
                        provider ?? s_defaultFileSystemProvider));
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        internal void RecordMetadata(
            string path,
            EvaluationMetadataKind kind,
            string value,
            string baseDirectory,
            string operation = null,
            string provider = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            MarkCategory(EvaluationObservationCategory.FileMetadata, EvaluationObservationCategoryState.Observed);
            try
            {
                string normalizedBaseDirectory = NormalizePath(baseDirectory);
                string normalizedPath =
                    !Path.IsPathRooted(path) && !string.IsNullOrEmpty(normalizedBaseDirectory)
                        ? path
                        : NormalizePath(path);
                RecordMetadataCore(
                    normalizedPath,
                    kind,
                    new EvaluationMetadataObservation(
                        normalizedPath,
                        kind,
                        value,
                        normalizedBaseDirectory,
                        operation,
                        provider ?? s_defaultFileSystemProvider));
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        private void RecordMetadataCore(
            string path,
            EvaluationMetadataKind kind,
            EvaluationMetadataObservation observation)
        {
            try
            {
                lock (_observationLock)
                {
                    if (IsCompleted)
                    {
                        return;
                    }

                    var key = new MetadataKey(
                        path,
                        kind,
                        observation.Operation,
                        observation.BaseDirectory,
                        observation.Provider);
                    if (!_metadataReads.TryAdd(key, observation) &&
                        _metadataReads.TryGetValue(key, out EvaluationMetadataObservation priorValue) &&
                        (priorValue.Value != observation.Value ||
                         !string.Equals(priorValue.TextValue, observation.TextValue, StringComparison.Ordinal) ||
                         !FileUtilities.PathComparer.Equals(priorValue.BaseDirectory, observation.BaseDirectory)))
                    {
                        AddReason(EvaluationObservationReason.ConflictingObservation);
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        internal void RecordFileRead(
            string path,
            string contentHash,
            bool isVerifiable,
            EvaluationContentHashKind hashKind = EvaluationContentHashKind.Unknown,
            string provider = null)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            MarkCategory(
                EvaluationObservationCategory.FileContent,
                isVerifiable
                    ? EvaluationObservationCategoryState.Observed
                    : EvaluationObservationCategoryState.Incomplete);
            try
            {
                lock (_observationLock)
                {
                    if (IsCompleted)
                    {
                        return;
                    }

                    string normalizedPath = NormalizePath(path);
                    string actualProvider = provider ?? s_defaultFileSystemProvider;
                    var key = new FileReadKey(normalizedPath, hashKind, actualProvider);
                    var observation = new EvaluationFileReadObservation(
                        normalizedPath,
                        contentHash,
                        isVerifiable,
                        hashKind,
                        actualProvider);

                    if (!_fileReads.TryAdd(key, observation) &&
                        _fileReads.TryGetValue(key, out EvaluationFileReadObservation priorObservation))
                    {
                        if (priorObservation.IsVerifiable && observation.IsVerifiable)
                        {
                            if (!string.Equals(priorObservation.ContentHash, observation.ContentHash, StringComparison.Ordinal))
                            {
                                AddReason(EvaluationObservationReason.ConflictingObservation);
                            }
                        }
                        else if (!priorObservation.IsVerifiable && observation.IsVerifiable)
                        {
                            _fileReads[key] = observation;
                        }
                    }

                    if (!isVerifiable)
                    {
                        AddReason(EvaluationObservationReason.UnverifiableFileRead);
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        internal void RecordOperationFailure()
        {
            MarkCategory(EvaluationObservationCategory.Completion, EvaluationObservationCategoryState.Incomplete);
            lock (_observationLock)
            {
                if (!IsCompleted)
                {
                    AddReason(EvaluationObservationReason.ExternalOperationFailure);
                }
            }
        }

        internal EvaluationObservationReport Complete(bool evaluationSucceeded)
        {
            MarkCategory(EvaluationObservationCategory.Completion, EvaluationObservationCategoryState.Observed);
            EvaluationObservationReport report;
            TestConfiguration testConfiguration;
            lock (_observationLock)
            {
                if (IsCompleted)
                {
                    return null;
                }

                Volatile.Write(ref _completed, 1);
                try
                {
                    EvaluationCategoryObservation[] categories;
                    EvaluationRequestObservation request;
                    EvaluationProjectSourceObservation[] projectSources;
                    EvaluationPathProbeObservation[] pathProbes;
                    EvaluationDirectoryEnumerationObservation[] directoryEnumerations;
                    EvaluationMetadataObservation[] metadataReads;
                    EvaluationFileReadObservation[] fileReads;
                    EvaluationGlobObservation[] globs;
                    EvaluationSearchObservation[] searches;
                    EvaluationEnvironmentObservation[] environment;
                    EvaluationExternalInputObservation[] externalInputs;
                    EvaluationPropertyFunctionObservation[] propertyFunctions;
                    EvaluationSdkResolutionObservation[] sdkResolutions;
                    EvaluationTaskRegistrationObservation[] taskRegistrations;
                    EvaluationSideEffectObservation[] sideEffects;
                    categories = CreateCategorySnapshot();
                    request = _request;
                    projectSources = CreateSnapshot(_projectSources.Values);
                    pathProbes = CreatePathProbeSnapshot();
                    directoryEnumerations = CreateSnapshot(_directoryEnumerations.Values);
                    metadataReads = CreateSnapshot(_metadataReads.Values);
                    fileReads = CreateSnapshot(_fileReads.Values);
                    globs = CreateSnapshot(_globs.Values);
                    searches = CreateSnapshot(_searches.Values);
                    environment = CreateSnapshot(_environment.Values);
                    externalInputs = CreateSnapshot(_externalInputs.Values);
                    propertyFunctions = CreateSnapshot(_propertyFunctions.Values);
                    sdkResolutions = _sdkResolutions.ToArray();
                    taskRegistrations = CreateSnapshot(_taskRegistrations.Values);
                    sideEffects = CreateSnapshot(_sideEffects.Values);

                    report = new EvaluationObservationReport(
                        _evaluationId,
                        _projectPath,
                        evaluationSucceeded,
                        (EvaluationObservationReason)Volatile.Read(ref _reasons),
                        ObservationSchemaVersion,
                        PropertyFunctionClassificationVersion,
                        categories,
                        request,
                        projectSources,
                        pathProbes,
                        directoryEnumerations,
                        metadataReads,
                        fileReads,
                        globs,
                        searches,
                        environment,
                        externalInputs,
                        propertyFunctions,
                        sdkResolutions,
                        taskRegistrations,
                        sideEffects);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    report = new EvaluationObservationReport(
                        _evaluationId,
                        _projectPath,
                        evaluationSucceeded,
                        (EvaluationObservationReason)Volatile.Read(ref _reasons) |
                            EvaluationObservationReason.ObservationIncomplete,
                        ObservationSchemaVersion,
                        PropertyFunctionClassificationVersion,
                        CreateCategorySnapshot(),
                        null,
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        [],
                        []);
                }

                _pathProbes.Clear();
                _directoryEnumerations.Clear();
                _metadataReads.Clear();
                _fileReads.Clear();
                _request = null;
                _projectSources.Clear();
                _globs.Clear();
                _searches.Clear();
                _environment.Clear();
                _externalInputs.Clear();
                _propertyFunctions.Clear();
                _sdkResolutions.Clear();
                _taskRegistrations.Clear();
                _sideEffects.Clear();
                testConfiguration = Interlocked.Exchange(ref _testConfiguration, null);
            }

            try
            {
                testConfiguration?.ReportCreated?.Invoke(report);
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                Interlocked.CompareExchange(ref testConfiguration.ReportException, ex, null);
            }

            return report;
        }

        private static string ComputeHash(byte[] content)
        {
#if NET
            return Convert.ToBase64String(SHA256.HashData(content));
#else
            using SHA256 sha256 = SHA256.Create();
            return Convert.ToBase64String(sha256.ComputeHash(content));
#endif
        }

        private static string ComputeHash(string content)
        {
            return ComputeHash(Encoding.UTF8.GetBytes(content));
        }

        internal static string ComputeTextHash(string content) => ComputeHash(content);
        internal static string ComputeBytesHash(byte[] content) => ComputeHash(content);

        private static string GetProjectSourceHash(ProjectRootElement source)
        {
            ProjectSourceHashCache cache = s_projectSourceHashes.GetValue(
                source,
                static _ => new ProjectSourceHashCache());
            lock (cache)
            {
                int version = source.Version;
                if (cache.Version != version || cache.ContentHash is null)
                {
                    cache.Version = version;
                    cache.ContentHash = ComputeTextHash(source.RawXml);
                }

                return cache.ContentHash;
            }
        }

        private string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path) || Path.IsPathRooted(path))
            {
                return string.IsNullOrEmpty(path) ? path : FileUtilities.GetFullPathNoThrow(path);
            }

            AddReason(EvaluationObservationReason.UnrootedPath);
            return path;
        }

        private static bool EnumerationResultsEqual(
            EvaluationDirectoryEnumerationObservation left,
            EvaluationDirectoryEnumerationObservation right)
        {
            return left.Completion == right.Completion &&
                left.EntryCount == right.EntryCount &&
                string.Equals(left.EntriesHash, right.EntriesHash, StringComparison.Ordinal);
        }

        private EvaluationPathProbeObservation[] CreatePathProbeSnapshot()
        {
            var snapshot = new EvaluationPathProbeObservation[_pathProbes.Count];
            int index = 0;
            foreach (KeyValuePair<PathProbeKey, bool> observation in _pathProbes)
            {
                snapshot[index++] = new EvaluationPathProbeObservation(
                    observation.Key.Path,
                    observation.Key.Kind,
                    observation.Value,
                    observation.Key.Provider);
            }

            return snapshot;
        }

        private void Record(Action action)
        {
            try
            {
                lock (_observationLock)
                {
                    if (!IsCompleted)
                    {
                        action();
                    }
                }
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                AddReason(EvaluationObservationReason.ObservationIncomplete);
            }
        }

        private static T[] CreateSnapshot<T>(ICollection<T> observations)
        {
            if (observations.Count == 0)
            {
                return [];
            }

            T[] snapshot = new T[observations.Count];
            observations.CopyTo(snapshot, 0);
            return snapshot;
        }

        private static StringComparer GetEnvironmentNameComparer(EvaluationEnvironmentSource source)
        {
            return source != EvaluationEnvironmentSource.LiveProcess || NativeMethodsShared.IsWindows
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal;
        }

        internal void MarkReason(EvaluationObservationReason reason) => AddReason(reason);

        private static EvaluationPathKind ConvertPathKind(EvaluationPathProbeKind kind)
        {
            return kind switch
            {
                EvaluationPathProbeKind.File => EvaluationPathKind.File,
                EvaluationPathProbeKind.Directory => EvaluationPathKind.Directory,
                EvaluationPathProbeKind.FileOrDirectory => EvaluationPathKind.FileOrDirectory,
                _ => Assumed.Unreachable<EvaluationPathKind>(),
            };
        }

        private void RecordTypedPropertyFunction(
            Type receiverType,
            string member,
            object instance,
            object[] arguments,
            object result,
            EvaluationPropertyFunctionEffect effects,
            string[] serializedArguments,
            string serializedResult)
        {
            string receiverName = receiverType?.FullName;
            string firstArgument = arguments is { Length: > 0 } ? arguments[0]?.ToString() : null;
            string serializedRequest = string.Join("|", serializedArguments);

            if (receiverName == typeof(Environment).FullName)
            {
                if (string.Equals(member, nameof(Environment.GetEnvironmentVariable), StringComparison.OrdinalIgnoreCase))
                {
                    string value = result as string;
                    RecordEnvironment(firstArgument, EvaluationEnvironmentSource.LiveProcess, value is not null, value);
                }
                else
                {
                    RecordExternalInputCore(
                        EvaluationExternalInputKind.Environment,
                        string.Concat(receiverName, "::", member),
                        serializedRequest,
                        serializedResult);
                }

                return;
            }

            if (receiverName == typeof(System.IO.File).FullName)
            {
                if (string.Equals(member, nameof(System.IO.File.ReadAllText), StringComparison.OrdinalIgnoreCase) &&
                    result is string text)
                {
                    RecordFileRead(
                        firstArgument,
                        ComputeTextHash(text),
                        isVerifiable: true,
                        hashKind: EvaluationContentHashKind.DecodedText);
                }
                else if (string.Equals(member, nameof(System.IO.File.ReadAllBytes), StringComparison.OrdinalIgnoreCase) &&
                    result is byte[] bytes)
                {
                    RecordFileRead(
                        firstArgument,
                        ComputeBytesHash(bytes),
                        isVerifiable: true,
                        hashKind: EvaluationContentHashKind.RawBytes);
                }
                else if (string.Equals(member, "ReadAllLines", StringComparison.OrdinalIgnoreCase) &&
                    result is string[] lines)
                {
                    RecordFileRead(
                        firstArgument,
                        ComputeStringSequenceHash(lines),
                        isVerifiable: true,
                        hashKind: EvaluationContentHashKind.DecodedTextSequence);
                }
                else if (string.Equals(member, nameof(System.IO.File.Exists), StringComparison.OrdinalIgnoreCase) &&
                    result is bool fileExists)
                {
                    RecordProbe(firstArgument, EvaluationPathKind.File, fileExists);
                }
                else
                {
                    RecordMetadata(
                        firstArgument,
                        EvaluationMetadataKind.PropertyFunction,
                        serializedResult,
                        null,
                        string.Concat(receiverName, "::", member));
                }

                return;
            }

            if (receiverName == typeof(System.IO.Directory).FullName)
            {
                if (string.Equals(member, nameof(System.IO.Directory.Exists), StringComparison.OrdinalIgnoreCase) &&
                    result is bool directoryExists)
                {
                    RecordProbe(firstArgument, EvaluationPathKind.Directory, directoryExists);
                }
                else if ((effects & EvaluationPropertyFunctionEffect.DirectoryEnumeration) != 0 &&
                         result is ICollection collection)
                {
                    var entries = new List<string>(collection.Count);
                    foreach (object entry in collection)
                    {
                        entries.Add(entry?.ToString());
                    }

                    RecordEnumeration(
                        firstArgument,
                        arguments is { Length: > 1 } ? arguments[1]?.ToString() : "*",
                        GetSearchOption(arguments),
                        GetEnumerationKind(member),
                        entries,
                        EvaluationEnumerationCompletion.Complete);
                }
                else
                {
                    RecordMetadata(
                        firstArgument,
                        EvaluationMetadataKind.PropertyFunction,
                        serializedResult,
                        null,
                        string.Concat(receiverName, "::", member));
                }

                return;
            }

            if (receiverName == typeof(System.IO.Path).FullName)
            {
                if (string.Equals(member, "Exists", StringComparison.OrdinalIgnoreCase) &&
                    result is bool exists)
                {
                    RecordProbe(firstArgument, EvaluationPathKind.FileOrDirectory, exists);
                }
                else if ((effects & EvaluationPropertyFunctionEffect.Ambient) != 0)
                {
                    RecordExternalInputCore(
                        EvaluationExternalInputKind.Ambient,
                        string.Concat(receiverName, "::", member),
                        serializedRequest,
                        serializedResult);
                }

                return;
            }

            if (receiverType == typeof(IntrinsicFunctions))
            {
                if (string.Equals(member, "FileExists", StringComparison.OrdinalIgnoreCase) &&
                    result is bool fileExists)
                {
                    RecordProbe(firstArgument, EvaluationPathKind.File, fileExists);
                }
                else if (string.Equals(member, "DirectoryExists", StringComparison.OrdinalIgnoreCase) &&
                         result is bool directoryExists)
                {
                    RecordProbe(firstArgument, EvaluationPathKind.Directory, directoryExists);
                }
                else if (string.Equals(member, "GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(member, "GetDirectoryNameOfFileAbove", StringComparison.OrdinalIgnoreCase))
                {
                    // The ordered candidate list is recorded by FileUtilities at the actual search seam.
                }
                else if (member.StartsWith("GetRegistryValue", StringComparison.OrdinalIgnoreCase))
                {
                    RecordExternalInputCore(
                        EvaluationExternalInputKind.Registry,
                        member,
                        serializedRequest,
                        serializedResult);
                }
                else if (string.Equals(member, "RegisterBuildCheck", StringComparison.OrdinalIgnoreCase))
                {
                    RecordSideEffectCore("RegisterBuildCheck", firstArgument, serializedResult);
                }
                else if ((effects & EvaluationPropertyFunctionEffect.Ambient) != 0)
                {
                    RecordExternalInputCore(
                        EvaluationExternalInputKind.Ambient,
                        member,
                        serializedRequest,
                        serializedResult);
                }

                return;
            }

            if (receiverName == "Microsoft.Build.Utilities.ToolLocationHelper")
            {
                RecordExternalInputCore(
                    EvaluationExternalInputKind.Toolset,
                    string.Concat(receiverName, "::", member),
                    serializedRequest,
                    serializedResult);
                MarkReason(EvaluationObservationReason.UnversionedToolLocationHelperCache);
                return;
            }

            if (instance is FileSystemInfo fileSystemInfo)
            {
                if ((effects & EvaluationPropertyFunctionEffect.DirectoryEnumeration) != 0 &&
                    result is ICollection collection)
                {
                    List<string> entries = [];
                    foreach (object entry in collection)
                    {
                        entries.Add(entry is FileSystemInfo resultInfo ? resultInfo.FullName : entry?.ToString());
                    }

                    RecordEnumeration(
                        fileSystemInfo.FullName,
                        "*",
                        GetSearchOption(arguments),
                        GetEnumerationKind(member),
                        entries,
                        EvaluationEnumerationCompletion.Complete);
                }
                else if ((effects & EvaluationPropertyFunctionEffect.FileMetadata) != 0)
                {
                    RecordMetadata(
                        fileSystemInfo.FullName,
                        EvaluationMetadataKind.PropertyFunction,
                        serializedResult,
                        null,
                        string.Concat(receiverName, "::", member));
                }

                return;
            }

            if ((effects & EvaluationPropertyFunctionEffect.Registry) != 0)
            {
                RecordExternalInputCore(
                    EvaluationExternalInputKind.Registry,
                    string.Concat(receiverName, "::", member),
                    serializedRequest,
                    serializedResult);
            }
            else if ((effects & EvaluationPropertyFunctionEffect.Ambient) != 0)
            {
                RecordExternalInputCore(
                    EvaluationExternalInputKind.Ambient,
                    string.Concat(receiverName, "::", member),
                    serializedRequest,
                    serializedResult);
            }

            if ((effects & EvaluationPropertyFunctionEffect.SideEffect) != 0)
            {
                RecordSideEffectCore(
                    string.Concat(receiverName, "::", member),
                    firstArgument,
                    serializedResult);
            }
        }

        private EvaluationPropertyFunctionEffect ClassifyPropertyFunction(
            Type receiverType,
            string member)
        {
            if (_allPropertyFunctionsEnabled)
            {
                return EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            string receiverName = receiverType?.FullName;
            if (receiverType == typeof(IntrinsicFunctions))
            {
                if (member.StartsWith("GetRegistryValue", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Registry;
                }

                if (string.Equals(member, "FileExists", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "DirectoryExists", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetPathOfFileAbove", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetDirectoryNameOfFileAbove", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.PathProbe;
                }

                if (string.Equals(member, "DoesTaskHostExist", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.PathProbe | EvaluationPropertyFunctionEffect.Ambient;
                }

                if (string.Equals(member, "RegisterBuildCheck", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.FileContent | EvaluationPropertyFunctionEffect.SideEffect;
                }

                if (member.StartsWith("GetCurrentToolsDirectory", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("GetToolsDirectory", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetMSBuildSDKsPath", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetVsInstallRoot", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetProgramFiles32", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetMSBuildExtensionsPath", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "IsRunningFromVisualStudio", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Ambient;
                }

                if (string.Equals(member, "NormalizePath", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "NormalizeDirectory", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "AreFeaturesEnabled", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "CheckFeatureAvailability", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("IsOs", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "IsOSPlatform", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Ambient;
                }

                return IsKnownPureIntrinsic(member)
                    ? EvaluationPropertyFunctionEffect.Pure
                    : EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            if (receiverName == typeof(Environment).FullName)
            {
                return IsVolatileEnvironmentMember(member)
                    ? EvaluationPropertyFunctionEffect.Volatile
                    : EvaluationPropertyFunctionEffect.Environment | EvaluationPropertyFunctionEffect.Ambient;
            }

            if (receiverName == typeof(System.IO.File).FullName)
            {
                if (IsMutatingFileSystemMember(member))
                {
                    return EvaluationPropertyFunctionEffect.SideEffect |
                        EvaluationPropertyFunctionEffect.OpaqueUnsupported;
                }

                if (member.StartsWith("ReadAll", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.FileContent;
                }

                if (string.Equals(member, nameof(System.IO.File.Exists), StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.PathProbe;
                }

                if (s_fileMetadataMembers.Contains(member))
                {
                    return EvaluationPropertyFunctionEffect.FileMetadata;
                }

                return EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            if (receiverName == typeof(System.IO.Directory).FullName)
            {
                if (IsMutatingFileSystemMember(member))
                {
                    return EvaluationPropertyFunctionEffect.SideEffect |
                        EvaluationPropertyFunctionEffect.OpaqueUnsupported;
                }

                if (string.Equals(member, nameof(System.IO.Directory.Exists), StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.PathProbe;
                }

                if (string.Equals(member, nameof(System.IO.Directory.GetFiles), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, nameof(System.IO.Directory.GetDirectories), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "GetFileSystemEntries", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Enumerate", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.DirectoryEnumeration;
                }

                if (s_directoryMetadataMembers.Contains(member))
                {
                    return EvaluationPropertyFunctionEffect.FileMetadata;
                }

                return EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            if (receiverName is "System.IO.FileInfo" or "System.IO.DirectoryInfo" or "System.IO.FileSystemInfo")
            {
                if (member.StartsWith("Enumerate", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.DirectoryEnumeration |
                        EvaluationPropertyFunctionEffect.OpaqueUnsupported;
                }

                if (member.StartsWith("GetFiles", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("GetDirectories", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("GetFileSystemInfos", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.DirectoryEnumeration;
                }

                if (member.StartsWith("Open", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Create", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Append", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Move", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Copy", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("Replace", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.SideEffect |
                        EvaluationPropertyFunctionEffect.OpaqueUnsupported;
                }

                return s_fileSystemInfoMetadataMembers.Contains(member)
                    ? EvaluationPropertyFunctionEffect.FileMetadata
                    : EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            if (receiverName == typeof(System.IO.Path).FullName)
            {
                if (string.Equals(member, "Exists", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.PathProbe;
                }

                if (string.Equals(member, nameof(System.IO.Path.GetTempFileName), StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Volatile | EvaluationPropertyFunctionEffect.SideEffect;
                }

                if (string.Equals(member, nameof(System.IO.Path.GetRandomFileName), StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Volatile;
                }

                if (string.Equals(member, nameof(System.IO.Path.GetTempPath), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, nameof(System.IO.Path.GetFullPath), StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Ambient;
                }

                return s_knownPurePathMembers.Contains(member)
                    ? EvaluationPropertyFunctionEffect.Pure
                    : EvaluationPropertyFunctionEffect.OpaqueUnsupported;
            }

            if (receiverName == typeof(DateTime).FullName ||
                receiverName == typeof(DateTimeOffset).FullName)
            {
                if (string.Equals(member, "Now", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "UtcNow", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(member, "Today", StringComparison.OrdinalIgnoreCase))
                {
                    return EvaluationPropertyFunctionEffect.Volatile;
                }

                return EvaluationPropertyFunctionEffect.Ambient;
            }

            if (receiverName == typeof(Guid).FullName &&
                string.Equals(member, nameof(Guid.NewGuid), StringComparison.OrdinalIgnoreCase))
            {
                return EvaluationPropertyFunctionEffect.Volatile;
            }

            if (receiverName == typeof(Guid).FullName)
            {
                return EvaluationPropertyFunctionEffect.Pure;
            }

            if (receiverName == typeof(char).FullName)
            {
                return member.StartsWith("ToLower", StringComparison.OrdinalIgnoreCase) ||
                    member.StartsWith("ToUpper", StringComparison.OrdinalIgnoreCase)
                    ? EvaluationPropertyFunctionEffect.Ambient
                    : EvaluationPropertyFunctionEffect.Pure;
            }

            if (IsNumericPropertyFunctionType(receiverType))
            {
                return IsCultureSensitiveNumericMember(member)
                    ? EvaluationPropertyFunctionEffect.Ambient
                    : EvaluationPropertyFunctionEffect.Pure;
            }

            if (receiverName == typeof(Convert).FullName)
            {
                return EvaluationPropertyFunctionEffect.Ambient;
            }

            if (receiverName == typeof(TimeSpan).FullName)
            {
                return IsCultureSensitiveNumericMember(member)
                    ? EvaluationPropertyFunctionEffect.Ambient
                    : EvaluationPropertyFunctionEffect.Pure;
            }

            if (receiverName == typeof(string).FullName)
            {
                return IsCultureSensitiveStringMember(member)
                    ? EvaluationPropertyFunctionEffect.Ambient
                    : EvaluationPropertyFunctionEffect.Pure;
            }

            if (receiverName == typeof(StringComparer).FullName)
            {
                return member.StartsWith("CurrentCulture", StringComparison.OrdinalIgnoreCase)
                    ? EvaluationPropertyFunctionEffect.Ambient
                    : EvaluationPropertyFunctionEffect.Pure;
            }

            if (receiverName == typeof(CultureInfo).FullName)
            {
                return EvaluationPropertyFunctionEffect.Ambient;
            }

            if (receiverName == "Microsoft.Build.Utilities.ToolLocationHelper")
            {
                return EvaluationPropertyFunctionEffect.FileContent |
                    EvaluationPropertyFunctionEffect.Registry |
                    EvaluationPropertyFunctionEffect.Ambient;
            }

            if (receiverName == typeof(RuntimeInformation).FullName ||
                receiverName == typeof(OSPlatform).FullName ||
                receiverName is "System.OperatingSystem" or "Microsoft.Build.Framework.OperatingSystem")
            {
                return EvaluationPropertyFunctionEffect.Ambient;
            }

            if (IsKnownPurePropertyFunctionType(receiverType))
            {
                return EvaluationPropertyFunctionEffect.Pure;
            }

            return EvaluationPropertyFunctionEffect.OpaqueUnsupported;
        }

        private static bool IsKnownPureIntrinsic(string member)
        {
            return s_knownPureIntrinsicMembers.Contains(member);
        }

        private static EvaluationEnumerationKind GetEnumerationKind(string member)
        {
            if (member.IndexOf("FileSystem", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return EvaluationEnumerationKind.FilesAndDirectories;
            }

            return member.IndexOf("Directories", StringComparison.OrdinalIgnoreCase) >= 0
                ? EvaluationEnumerationKind.Directories
                : EvaluationEnumerationKind.Files;
        }

        private static SearchOption GetSearchOption(object[] arguments)
        {
            if (arguments is not null)
            {
                for (int i = 0; i < arguments.Length; i++)
                {
                    if (arguments[i] is SearchOption searchOption)
                    {
                        return searchOption;
                    }
                }
            }

            return SearchOption.TopDirectoryOnly;
        }

        private static bool IsNumericPropertyFunctionType(Type type)
        {
            return type?.IsPrimitive == true ||
                type == typeof(decimal);
        }

        private static bool IsCultureSensitiveNumericMember(string member)
        {
            return member.StartsWith("Parse", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("TryParse", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("ToString", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsMutatingFileSystemMember(string member)
        {
            return member.StartsWith("Write", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Append", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Create", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Delete", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Move", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Copy", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Replace", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Set", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsCultureSensitiveStringMember(string member)
        {
            return member.StartsWith("Compare", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("EndsWith", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("IndexOf", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("LastIndexOf", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("Format", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("StartsWith", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("ToLower", StringComparison.OrdinalIgnoreCase) ||
                member.StartsWith("ToUpper", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsVolatileEnvironmentMember(string member)
        {
            return string.Equals(member, nameof(Environment.TickCount), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member, nameof(Environment.WorkingSet), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(member, nameof(Environment.StackTrace), StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsKnownPurePropertyFunctionType(Type type)
        {
            if (type is null)
            {
                return false;
            }

            return type.IsEnum ||
                type == typeof(decimal) ||
                type == typeof(Enum) ||
                type == typeof(Math) ||
                type == typeof(TimeSpan) ||
                type == typeof(Version) ||
                type == typeof(Uri) ||
                type == typeof(UriBuilder) ||
                type.FullName == "System.Text.RegularExpressions.Regex";
        }

        private static string[] SerializeArguments(object[] arguments)
        {
            if (arguments is null || arguments.Length == 0)
            {
                return [];
            }

            string[] result = new string[arguments.Length];
            for (int i = 0; i < arguments.Length; i++)
            {
                result[i] = SerializeValue(arguments[i]);
            }

            return result;
        }

        private static string SerializeValue(object value)
        {
            if (value is null)
            {
                return null;
            }

            if (value is string stringValue)
            {
                return stringValue;
            }

            if (value is byte[] bytes)
            {
                return ComputeBytesHash(bytes);
            }

            if (value is DateTime dateTime)
            {
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            }

            if (value is DateTimeOffset dateTimeOffset)
            {
                return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            }

            if (value is IDictionary dictionary)
            {
                List<string> entries = [];
                foreach (DictionaryEntry entry in dictionary)
                {
                    entries.Add(string.Concat(SerializeValue(entry.Key), "=", SerializeValue(entry.Value)));
                }

                entries.Sort(StringComparer.Ordinal);
                return string.Join(";", entries);
            }

            if (value is ICollection collection)
            {
                List<string> entries = [];
                foreach (object entry in collection)
                {
                    entries.Add(SerializeValue(entry));
                }

                return string.Join(";", entries);
            }

            return value is IFormattable formattable
                ? formattable.ToString(null, CultureInfo.InvariantCulture)
                : value.ToString();
        }

        private static string[] CopyStrings(IReadOnlyList<string> values)
        {
            if (values is null || values.Count == 0)
            {
                return [];
            }

            string[] snapshot = new string[values.Count];
            for (int i = 0; i < values.Count; i++)
            {
                snapshot[i] = values[i];
            }

            return snapshot;
        }

        private static string[] CopyStrings(IEnumerable<string> values)
        {
            if (values is null)
            {
                return [];
            }

            List<string> snapshot = values is ICollection<string> collection
                ? new List<string>(collection.Count)
                : [];
            foreach (string value in values)
            {
                snapshot.Add(value);
            }

            return snapshot.ToArray();
        }

        private static EvaluationNamedValueObservation[] CreateNamedValueSnapshot(
            IDictionary<string, string> values,
            string source)
        {
            if (values is null || values.Count == 0)
            {
                return [];
            }

            var snapshot = new EvaluationNamedValueObservation[values.Count];
            int index = 0;
            foreach (KeyValuePair<string, string> value in values)
            {
                snapshot[index++] = new EvaluationNamedValueObservation(
                    value.Key,
                    value.Value,
                    source);
            }

            return snapshot;
        }

        private static EvaluationSdkItemObservation[] CreateSdkItemSnapshot(
            IDictionary<string, SdkResultItem> items)
        {
            if (items is null || items.Count == 0)
            {
                return [];
            }

            var snapshot = new EvaluationSdkItemObservation[items.Count];
            int index = 0;
            foreach (KeyValuePair<string, SdkResultItem> item in items)
            {
                snapshot[index++] = new EvaluationSdkItemObservation(
                    item.Key,
                    item.Value?.ItemSpec,
                    CreateNamedValueSnapshot(item.Value?.Metadata, "SdkItemMetadata"));
            }

            return snapshot;
        }

        private static string ComputeStringSequenceHash(IReadOnlyList<string> values)
        {
            var hasher = new EvaluationInputFingerprintBuilder();
            if (values is not null)
            {
                for (int i = 0; i < values.Count; i++)
                {
                    hasher.Add(values[i]);
                }
            }

            return hasher.Complete();
        }

        private static bool GlobResultsEqual(
            EvaluationGlobObservation left,
            EvaluationGlobObservation right)
        {
            return left.WasLazy == right.WasLazy &&
                left.DriveEnumerating == right.DriveEnumerating &&
                left.ResultsEscaped == right.ResultsEscaped &&
                left.ExcludeCount == right.ExcludeCount &&
                string.Equals(left.ExcludesFingerprint, right.ExcludesFingerprint, StringComparison.Ordinal) &&
                left.ResultCount == right.ResultCount &&
                string.Equals(left.ResultsFingerprint, right.ResultsFingerprint, StringComparison.Ordinal) &&
                string.Equals(left.Failure, right.Failure, StringComparison.Ordinal);
        }

        private void MarkCategory(
            EvaluationObservationCategory category,
            EvaluationObservationCategoryState state)
        {
            long mask = 1L << (int)category;
            switch (state)
            {
                case EvaluationObservationCategoryState.Observed:
                    SetCategoryBit(ref _observedCategories, mask);
                    break;
                case EvaluationObservationCategoryState.Incomplete:
                    SetCategoryBit(ref _incompleteCategories, mask);
                    break;
                case EvaluationObservationCategoryState.Unsupported:
                    SetCategoryBit(ref _unsupportedCategories, mask);
                    break;
            }
        }

        private static void SetCategoryBit(ref long field, long mask)
        {
            long priorValue;
            long newValue;
            do
            {
                priorValue = Volatile.Read(ref field);
                if ((priorValue & mask) != 0)
                {
                    return;
                }

                newValue = priorValue | mask;
            }
            while (Interlocked.CompareExchange(ref field, newValue, priorValue) != priorValue);
        }

        private EvaluationCategoryObservation[] CreateCategorySnapshot()
        {
            EvaluationObservationCategory[] categories =
                (EvaluationObservationCategory[])Enum.GetValues(typeof(EvaluationObservationCategory));
            var result = new EvaluationCategoryObservation[categories.Length];
            long observed = Volatile.Read(ref _observedCategories);
            long incomplete = Volatile.Read(ref _incompleteCategories);
            long unsupported = Volatile.Read(ref _unsupportedCategories);

            for (int i = 0; i < categories.Length; i++)
            {
                EvaluationObservationCategory category = categories[i];
                long mask = 1L << (int)category;
                EvaluationObservationCategoryState state =
                    (unsupported & mask) != 0
                        ? EvaluationObservationCategoryState.Unsupported
                        : (incomplete & mask) != 0
                            ? EvaluationObservationCategoryState.Incomplete
                            : (observed & mask) != 0
                                ? EvaluationObservationCategoryState.Observed
                                : EvaluationObservationCategoryState.NotExercised;
                result[i] = new EvaluationCategoryObservation(
                    category,
                    GetCategoryCoverage(category),
                    state);
            }

            return result;
        }

        private static EvaluationObservationCoverage GetCategoryCoverage(
            EvaluationObservationCategory category)
        {
            return category == EvaluationObservationCategory.Completion
                ? EvaluationObservationCoverage.Complete
                : EvaluationObservationCoverage.Partial;
        }

        private void AddReason(EvaluationObservationReason reason)
        {
            long priorValue;
            long newValue;
            do
            {
                priorValue = Volatile.Read(ref _reasons);
                newValue = priorValue | (long)reason;
            }
            while (Interlocked.CompareExchange(ref _reasons, newValue, priorValue) != priorValue);

            switch (reason)
            {
                case EvaluationObservationReason.AllPropertyFunctionsEnabled:
                case EvaluationObservationReason.UnclassifiedPropertyFunction:
                    MarkCategory(EvaluationObservationCategory.PropertyFunction, EvaluationObservationCategoryState.Unsupported);
                    break;
                case EvaluationObservationReason.UnsupportedVolatileInput:
                case EvaluationObservationReason.EvaluationSideEffect:
                    MarkCategory(EvaluationObservationCategory.VolatileOrSideEffect, EvaluationObservationCategoryState.Unsupported);
                    break;
                case EvaluationObservationReason.UnversionedToolsetInputs:
                case EvaluationObservationReason.UnversionedToolLocationHelperCache:
                    MarkCategory(EvaluationObservationCategory.Toolset, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.UnversionedCustomProvider:
                case EvaluationObservationReason.UnversionedDirectoryCache:
                    MarkCategory(EvaluationObservationCategory.CustomProvider, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.UnversionedSharedCache:
                case EvaluationObservationReason.UnversionedFileExistenceCache:
                case EvaluationObservationReason.UnversionedGlobCache:
                    MarkCategory(EvaluationObservationCategory.SharedCache, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.ProjectXmlContentNotObserved:
                case EvaluationObservationReason.UnversionedProjectRootElementCache:
                case EvaluationObservationReason.UnversionedSourceProvider:
                case EvaluationObservationReason.ParsedProjectSourceOnly:
                    MarkCategory(EvaluationObservationCategory.ProjectSource, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.UnverifiableFileRead:
                    MarkCategory(EvaluationObservationCategory.FileContent, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.AmbiguousNegativeProbe:
                case EvaluationObservationReason.UnrootedPath:
                    MarkCategory(EvaluationObservationCategory.PathProbe, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.PartialEnumeration:
                    MarkCategory(EvaluationObservationCategory.DirectoryEnumeration, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.IncompleteEvaluationStage:
                case EvaluationObservationReason.ParserConfigurationProvenanceUnavailable:
                    MarkCategory(EvaluationObservationCategory.Request, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.ConflictingObservation:
                    MarkCategory(EvaluationObservationCategory.Completion, EvaluationObservationCategoryState.Incomplete);
                    break;
                case EvaluationObservationReason.ExternalOperationFailure:
                case EvaluationObservationReason.OpaqueExternalInput:
                case EvaluationObservationReason.ObservationIncomplete:
                    MarkCategory(EvaluationObservationCategory.Completion, EvaluationObservationCategoryState.Incomplete);
                    break;
            }
        }

        private readonly struct PathProbeKey : IEquatable<PathProbeKey>
        {
            internal PathProbeKey(string path, EvaluationPathKind kind, string provider)
            {
                Path = path;
                Kind = kind;
                Provider = provider;
            }

            internal string Path { get; }
            internal EvaluationPathKind Kind { get; }
            internal string Provider { get; }

            public bool Equals(PathProbeKey other)
            {
                return Kind == other.Kind &&
                    FileUtilities.PathComparer.Equals(Path, other.Path) &&
                    string.Equals(Provider, other.Provider, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is PathProbeKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = (FileUtilities.PathComparer.GetHashCode(Path) * 397) ^ (int)Kind;
                    return (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(Provider);
                }
            }
        }

        private readonly struct EnvironmentKey : IEquatable<EnvironmentKey>
        {
            internal EnvironmentKey(EvaluationEnvironmentSource source, string name)
            {
                Source = source;
                Name = name;
            }

            private EvaluationEnvironmentSource Source { get; }
            private string Name { get; }

            public bool Equals(EnvironmentKey other)
            {
                return Source == other.Source &&
                    GetEnvironmentNameComparer(Source).Equals(Name, other.Name);
            }

            public override bool Equals(object obj) => obj is EnvironmentKey other && Equals(other);

            public override int GetHashCode()
            {
                return ((int)Source * 397) ^
                    GetEnvironmentNameComparer(Source).GetHashCode(Name);
            }
        }

        private readonly struct EnumerationKey : IEquatable<EnumerationKey>
        {
            internal EnumerationKey(
                string path,
                string searchPattern,
                SearchOption searchOption,
                EvaluationEnumerationKind kind,
                string provider)
            {
                Path = path;
                SearchPattern = searchPattern;
                SearchOption = searchOption;
                Kind = kind;
                Provider = provider;
            }

            internal string Path { get; }
            internal string SearchPattern { get; }
            internal SearchOption SearchOption { get; }
            internal EvaluationEnumerationKind Kind { get; }
            internal string Provider { get; }

            public bool Equals(EnumerationKey other)
            {
                return SearchOption == other.SearchOption &&
                    Kind == other.Kind &&
                    FileUtilities.PathComparer.Equals(Path, other.Path) &&
                    string.Equals(SearchPattern, other.SearchPattern, StringComparison.Ordinal) &&
                    string.Equals(Provider, other.Provider, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is EnumerationKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = FileUtilities.PathComparer.GetHashCode(Path);
                    hashCode = (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(SearchPattern);
                    hashCode = (hashCode * 397) ^ (int)SearchOption;
                    hashCode = (hashCode * 397) ^ (int)Kind;
                    return (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(Provider);
                }
            }
        }

        private readonly struct MetadataKey : IEquatable<MetadataKey>
        {
            internal MetadataKey(
                string path,
                EvaluationMetadataKind kind,
                string operation,
                string baseDirectory,
                string provider)
            {
                Path = path;
                Kind = kind;
                Operation = operation;
                BaseDirectory = baseDirectory;
                Provider = provider;
            }

            internal string Path { get; }
            internal EvaluationMetadataKind Kind { get; }
            internal string Operation { get; }
            internal string BaseDirectory { get; }
            internal string Provider { get; }

            public bool Equals(MetadataKey other)
            {
                return Kind == other.Kind &&
                    FileUtilities.PathComparer.Equals(Path, other.Path) &&
                    string.Equals(Operation, other.Operation, StringComparison.Ordinal) &&
                    FileUtilities.PathComparer.Equals(BaseDirectory, other.BaseDirectory) &&
                    string.Equals(Provider, other.Provider, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is MetadataKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = (FileUtilities.PathComparer.GetHashCode(Path) * 397) ^ (int)Kind;
                    hashCode = (hashCode * 397) ^ (Operation is null ? 0 : StringComparer.Ordinal.GetHashCode(Operation));
                    hashCode = (hashCode * 397) ^ (BaseDirectory is null ? 0 : FileUtilities.PathComparer.GetHashCode(BaseDirectory));
                    return (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(Provider);
                }
            }
        }

        private readonly struct FileReadKey : IEquatable<FileReadKey>
        {
            internal FileReadKey(
                string path,
                EvaluationContentHashKind hashKind,
                string provider)
            {
                Path = path;
                HashKind = hashKind;
                Provider = provider;
            }

            internal string Path { get; }
            internal EvaluationContentHashKind HashKind { get; }
            internal string Provider { get; }

            public bool Equals(FileReadKey other)
            {
                return HashKind == other.HashKind &&
                    FileUtilities.PathComparer.Equals(Path, other.Path) &&
                    string.Equals(Provider, other.Provider, StringComparison.Ordinal);
            }

            public override bool Equals(object obj) => obj is FileReadKey other && Equals(other);

            public override int GetHashCode()
            {
                unchecked
                {
                    int hashCode = (FileUtilities.PathComparer.GetHashCode(Path) * 397) ^ (int)HashKind;
                    return (hashCode * 397) ^ StringComparer.Ordinal.GetHashCode(Provider);
                }
            }
        }

        private sealed class TestConfiguration
        {
            internal TestConfiguration(
                bool enabled,
                Action<EvaluationObservationReport> reportCreated,
                bool retainDetails)
            {
                Enabled = enabled;
                ReportCreated = reportCreated;
                RetainDetails = retainDetails;
            }

            internal bool Enabled { get; }
            internal Action<EvaluationObservationReport> ReportCreated { get; }
            internal bool RetainDetails { get; }
            internal Exception ReportException;
        }

        private sealed class ProjectSourceHashCache
        {
            internal int Version = -1;
            internal string ContentHash;
        }

        private sealed class CurrentScope : IDisposable
        {
            private readonly EvaluationObservationSession _previous;
            private readonly IDisposable _frameworkScope;
            private int _disposed;

            internal CurrentScope(
                EvaluationObservationSession previous,
                IDisposable frameworkScope)
            {
                _previous = previous;
                _frameworkScope = frameworkScope;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) == 0)
                {
                    try
                    {
                        _frameworkScope.Dispose();
                    }
                    finally
                    {
                        s_current = _previous;
                    }
                }
            }
        }

        internal readonly struct DirectoryEnumerationSuppressionScope : IDisposable
        {
            private readonly EvaluationObservationSession _session;

            internal DirectoryEnumerationSuppressionScope(EvaluationObservationSession session)
            {
                _session = session;
            }

            public void Dispose()
            {
                if (_session is not null)
                {
                    Interlocked.Decrement(ref _session._suppressDirectoryEnumerations);
                }
            }
        }

        private sealed class TestScope : IDisposable
        {
            private readonly TestConfiguration _configuration;
            private int _disposed;

            internal TestScope(TestConfiguration configuration)
            {
                _configuration = configuration;
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0)
                {
                    return;
                }

                Exception reportException;
                lock (s_testLock)
                {
                    Assumed.True(
                        ReferenceEquals(s_testConfiguration, _configuration),
                        "The active test observation scope changed unexpectedly.");
                    Volatile.Write(ref s_testConfiguration, null);
                    reportException = _configuration.ReportException;
                }

                if (reportException is not null)
                {
                    throw new InvalidOperationException(
                        "The test evaluation-observation callback failed.",
                        reportException);
                }
            }
        }
    }

    internal sealed class RecordingFileSystem : IFileSystem
    {
        private readonly IFileSystem _inner;
        private readonly string _providerIdentity;
        private readonly EvaluationObservationSession _session;

        internal RecordingFileSystem(IFileSystem inner, EvaluationObservationSession session)
        {
            _inner = inner;
            _providerIdentity = inner.GetType().AssemblyQualifiedName;
            _session = session;
        }

        public TextReader ReadFile(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.ReadFile(path);
            }

            try
            {
                TextReader reader = _inner.ReadFile(path);
                _session.RecordFileRead(
                    path,
                    contentHash: null,
                    isVerifiable: false,
                    provider: _providerIdentity);
                return reader;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public Stream GetFileStream(string path, FileMode mode, FileAccess access, FileShare share)
        {
            if (_session.IsCompleted)
            {
                return _inner.GetFileStream(path, mode, access, share);
            }

            try
            {
                Stream stream = _inner.GetFileStream(path, mode, access, share);
                if ((access & FileAccess.Read) != 0)
                {
                    _session.RecordFileRead(
                        path,
                        contentHash: null,
                        isVerifiable: false,
                        provider: _providerIdentity);
                }

                return stream;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public string ReadFileAllText(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.ReadFileAllText(path);
            }

            try
            {
                string content = _inner.ReadFileAllText(path);
                try
                {
                    _session.RecordFileRead(
                        path,
                        EvaluationObservationSession.ComputeTextHash(content),
                        isVerifiable: true,
                        hashKind: EvaluationContentHashKind.DecodedText,
                        provider: _providerIdentity);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    _session.RecordOperationFailure();
                }

                return content;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public byte[] ReadFileAllBytes(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.ReadFileAllBytes(path);
            }

            try
            {
                byte[] content = _inner.ReadFileAllBytes(path);
                try
                {
                    _session.RecordFileRead(
                        path,
                        EvaluationObservationSession.ComputeBytesHash(content),
                        isVerifiable: true,
                        hashKind: EvaluationContentHashKind.RawBytes,
                        provider: _providerIdentity);
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    _session.RecordOperationFailure();
                }

                return content;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public IEnumerable<string> EnumerateFiles(
            string path,
            string searchPattern = "*",
            SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (_session.IsCompleted || !_session.ShouldRecordDirectoryEnumeration)
            {
                return _inner.EnumerateFiles(path, searchPattern, searchOption);
            }

            return RecordEnumeration(
                path,
                searchPattern,
                searchOption,
                EvaluationEnumerationKind.Files,
                static (fileSystem, p, pattern, option) => fileSystem.EnumerateFiles(p, pattern, option));
        }

        public IEnumerable<string> EnumerateDirectories(
            string path,
            string searchPattern = "*",
            SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (_session.IsCompleted || !_session.ShouldRecordDirectoryEnumeration)
            {
                return _inner.EnumerateDirectories(path, searchPattern, searchOption);
            }

            return RecordEnumeration(
                path,
                searchPattern,
                searchOption,
                EvaluationEnumerationKind.Directories,
                static (fileSystem, p, pattern, option) => fileSystem.EnumerateDirectories(p, pattern, option));
        }

        public IEnumerable<string> EnumerateFileSystemEntries(
            string path,
            string searchPattern = "*",
            SearchOption searchOption = SearchOption.TopDirectoryOnly)
        {
            if (_session.IsCompleted || !_session.ShouldRecordDirectoryEnumeration)
            {
                return _inner.EnumerateFileSystemEntries(path, searchPattern, searchOption);
            }

            return RecordEnumeration(
                path,
                searchPattern,
                searchOption,
                EvaluationEnumerationKind.FilesAndDirectories,
                static (fileSystem, p, pattern, option) => fileSystem.EnumerateFileSystemEntries(p, pattern, option));
        }

        public FileAttributes GetAttributes(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.GetAttributes(path);
            }

            try
            {
                FileAttributes attributes = _inner.GetAttributes(path);
                _session.RecordMetadata(
                    path,
                    EvaluationMetadataKind.Attributes,
                    (long)attributes,
                    _providerIdentity);
                return attributes;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public DateTime GetLastWriteTimeUtc(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.GetLastWriteTimeUtc(path);
            }

            try
            {
                DateTime timestamp = _inner.GetLastWriteTimeUtc(path);
                _session.RecordMetadata(
                    path,
                    EvaluationMetadataKind.LastWriteTimeUtc,
                    timestamp.Ticks,
                    _providerIdentity);
                return timestamp;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public bool DirectoryExists(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.DirectoryExists(path);
            }

            try
            {
                bool exists = _inner.DirectoryExists(path);
                _session.RecordProbe(path, EvaluationPathKind.Directory, exists, _providerIdentity);
                return exists;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public bool FileExists(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.FileExists(path);
            }

            try
            {
                bool exists = _inner.FileExists(path);
                _session.RecordProbe(path, EvaluationPathKind.File, exists, _providerIdentity);
                return exists;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        public bool FileOrDirectoryExists(string path)
        {
            if (_session.IsCompleted)
            {
                return _inner.FileOrDirectoryExists(path);
            }

            try
            {
                bool exists = _inner.FileOrDirectoryExists(path);
                _session.RecordProbe(path, EvaluationPathKind.FileOrDirectory, exists, _providerIdentity);
                return exists;
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordOperationFailure();
                throw;
            }
        }

        private IEnumerable<string> RecordEnumeration(
            string path,
            string searchPattern,
            SearchOption searchOption,
            EvaluationEnumerationKind kind,
            Func<IFileSystem, string, string, SearchOption, IEnumerable<string>> enumerate)
        {
            IEnumerable<string> entries;
            try
            {
                entries = enumerate(_inner, path, searchPattern, searchOption);
            }
            catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
            {
                _session.RecordEnumeration(
                    path,
                    searchPattern,
                    searchOption,
                    kind,
                    [],
                    EvaluationEnumerationCompletion.Failure,
                    _providerIdentity);
                throw;
            }

            return RecordEnumerationIterator(path, searchPattern, searchOption, kind, entries);
        }

        private IEnumerable<string> RecordEnumerationIterator(
            string path,
            string searchPattern,
            SearchOption searchOption,
            EvaluationEnumerationKind kind,
            IEnumerable<string> entries)
        {
            List<string> observedEntries = _session.RetainDetails ? [] : null;
            var entriesHasher = new EvaluationInputFingerprintBuilder();
            int entryCount = 0;
            EvaluationEnumerationCompletion completion = EvaluationEnumerationCompletion.Partial;
            IEnumerator<string> enumerator = null;

            try
            {
                try
                {
                    enumerator = entries.GetEnumerator();
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    completion = EvaluationEnumerationCompletion.Failure;
                    throw;
                }

                while (true)
                {
                    bool hasNext;
                    try
                    {
                        hasNext = enumerator.MoveNext();
                    }
                    catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                    {
                        completion = EvaluationEnumerationCompletion.Failure;
                        throw;
                    }

                    if (!hasNext)
                    {
                        completion = EvaluationEnumerationCompletion.Complete;
                        yield break;
                    }

                    string entry = enumerator.Current;
                    entryCount++;
                    entriesHasher.Add(entry);
                    observedEntries?.Add(entry);
                    yield return entry;
                }
            }
            finally
            {
                try
                {
                    enumerator?.Dispose();
                }
                catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
                {
                    completion = EvaluationEnumerationCompletion.Failure;
                    throw;
                }
                finally
                {
                    _session.RecordEnumeration(
                        path,
                        searchPattern,
                        searchOption,
                        kind,
                        observedEntries?.ToArray() ?? [],
                        entryCount,
                        entriesHasher.Complete(),
                        completion,
                        _providerIdentity);
                }
            }
        }
    }
}
