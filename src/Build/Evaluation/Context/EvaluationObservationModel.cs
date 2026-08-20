// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;

#nullable disable

namespace Microsoft.Build.Evaluation.Context
{
    [Flags]
    internal enum EvaluationObservationReason : long
    {
        None = 0,
        AmbiguousNegativeProbe = 1L << 2,
        ConflictingObservation = 1L << 3,
        PartialEnumeration = 1L << 4,
        ExternalOperationFailure = 1L << 5,
        UnverifiableFileRead = 1L << 6,
        UnversionedSharedCache = 1L << 7,
        UnversionedFileExistenceCache = 1L << 8,
        UnversionedGlobCache = 1L << 9,
        UnversionedDirectoryCache = 1L << 10,
        ProjectXmlContentNotObserved = 1L << 11,
        UnversionedProjectRootElementCache = 1L << 12,
        IncompleteEvaluationStage = 1L << 14,
        UnrootedPath = 1L << 15,
        AllPropertyFunctionsEnabled = 1L << 16,
        UnclassifiedPropertyFunction = 1L << 17,
        UnsupportedVolatileInput = 1L << 18,
        EvaluationSideEffect = 1L << 19,
        UnversionedToolsetInputs = 1L << 23,
        UnversionedCustomProvider = 1L << 24,
        ParserConfigurationProvenanceUnavailable = 1L << 25,
        ParsedProjectSourceOnly = 1L << 26,
        OpaqueExternalInput = 1L << 27,
        UnversionedToolLocationHelperCache = 1L << 28,
        ObservationIncomplete = 1L << 29,
        UnversionedSourceProvider = 1L << 31,
    }

    internal enum EvaluationPathKind
    {
        File,
        Directory,
        FileOrDirectory,
    }

    internal enum EvaluationEnumerationKind
    {
        Files,
        Directories,
        FilesAndDirectories,
    }

    internal enum EvaluationEnumerationCompletion
    {
        Complete,
        Partial,
        Failure,
    }

    internal enum EvaluationMetadataKind
    {
        Attributes,
        LastWriteTimeUtc,
        Length,
        ItemModifiedTime,
        ItemCreatedTime,
        ItemAccessedTime,
        ItemFullPath,
        ItemRootDirectory,
        ItemRelativeDirectory,
        ItemDirectory,
        PropertyFunction,
    }

    internal enum EvaluationContentHashKind
    {
        Unknown,
        RawBytes,
        DecodedText,
        DecodedTextSequence,
        ParsedXml,
    }

    internal enum EvaluationObservationCategory
    {
        Request,
        ProjectSource,
        FileContent,
        PathProbe,
        FileMetadata,
        DirectoryEnumeration,
        Glob,
        Search,
        PropertyFunction,
        TaskRegistration,
        ImportedEnvironment,
        LiveEnvironment,
        Registry,
        Toolset,
        SdkResolution,
        SharedCache,
        CustomProvider,
        VolatileOrSideEffect,
        Completion,
    }

    internal enum EvaluationObservationCategoryState
    {
        NotExercised,
        Observed,
        Incomplete,
        Unsupported,
    }

    internal enum EvaluationObservationCoverage
    {
        NotImplemented,
        Partial,
        Complete,
    }

    internal enum EvaluationProjectSourceRole
    {
        Root,
        Import,
        Generated,
        InMemory,
    }

    internal enum EvaluationEnvironmentSource
    {
        Imported,
        MissingImported,
        SdkInjected,
        LiveProcess,
    }

    internal enum EvaluationExternalInputKind
    {
        Ambient,
        Registry,
        Toolset,
        ParserConfiguration,
        Sdk,
        Search,
        Environment,
    }

    [Flags]
    internal enum EvaluationPropertyFunctionEffect
    {
        None = 0,
        Pure = 1 << 0,
        FileContent = 1 << 1,
        PathProbe = 1 << 2,
        FileMetadata = 1 << 3,
        DirectoryEnumeration = 1 << 4,
        Environment = 1 << 5,
        Registry = 1 << 6,
        Ambient = 1 << 7,
        Volatile = 1 << 8,
        SideEffect = 1 << 9,
        OpaqueUnsupported = 1 << 10,
    }

    internal readonly struct EvaluationNamedValueObservation
    {
        internal EvaluationNamedValueObservation(string name, string value, string source)
        {
            Name = name;
            Value = value;
            Source = source;
        }

        internal string Name { get; }
        internal string Value { get; }
        internal string Source { get; }
    }

    internal sealed class EvaluationRequestObservation
    {
        internal string EngineVersion { get; init; }
        internal string EngineAssemblyVersion { get; init; }
        internal string HostMode { get; init; }
        internal string ProjectPath { get; init; }
        internal int ProjectLoadSettings { get; init; }
        internal int EvaluationStage { get; init; }
        internal int SharingPolicy { get; init; }
        internal string ExplicitToolsVersion { get; init; }
        internal string SubToolsetVersion { get; init; }
        internal bool LoadProjectsReadOnly { get; init; }
        internal bool AutoReloadProjectsFromDisk { get; init; }
        internal bool PreserveFormatting { get; init; }
        internal int MaxNodeCount { get; init; }
        internal bool InteractiveRequested { get; init; }
        internal bool InteractiveEffective { get; init; }
        internal bool BuildingInsideVisualStudio { get; init; }
        internal bool RunningInVisualStudio { get; init; }
        internal string StartupDirectory { get; init; }
        internal string ProcessCurrentDirectory { get; init; }
        internal string ThreadWorkingDirectory { get; init; }
        internal string CurrentCulture { get; init; }
        internal string CurrentUICulture { get; init; }
        internal string LocalTimeZone { get; init; }
        internal string Runtime { get; init; }
        internal string OperatingSystem { get; init; }
        internal string ProcessArchitecture { get; init; }
        internal string PathComparison { get; init; }
        internal bool EnableAllPropertyFunctions { get; init; }
        internal bool RestrictPropertyFunctionReceivers { get; init; }
        internal bool EnableCustomPluginProbing { get; init; }
        internal bool EnableSdkResolverDynamicLoading { get; init; }
        internal bool EnableConfigurationFileToolsets { get; init; }
        internal bool CacheFileExistence { get; init; }
        internal bool CacheFileEnumerations { get; init; }
        internal bool UseLazyWildcardEvaluation { get; init; }
        internal bool ForceEvaluateAsFullFramework { get; init; }
        internal string DisabledChangeWave { get; init; }
        internal string ChangeWaveConversionState { get; init; }
        internal bool DoNotExpandQualifiedMetadataInUpdateOperation { get; init; }
        internal bool? EvaluateElementsWithFalseCondition { get; init; }
        internal bool DoNotTruncateConditions { get; init; }
        internal bool AlwaysEvaluateDangerousGlobs { get; init; }
        internal bool DisableParseConfig { get; init; }
        internal bool IgnoreEmptyImports { get; init; }
        internal bool IgnoreTreatAsLocalProperty { get; init; }
        internal bool UseCaseSensitiveItemNames { get; init; }
        internal bool DisableSdkResolutionCache { get; init; }
        internal string SdkReferencePropertyExpansion { get; init; }
        internal bool AlwaysDoImmutableFilesUpToDateCheck { get; init; }
        internal bool AlwaysUseContentTimestamp { get; init; }
        internal bool UseSymlinkTimeInsteadOfTargetTime { get; init; }
        internal bool DisableLongPaths { get; init; }
        internal string FileSystemProvider { get; init; }
        internal string DirectoryCacheProvider { get; init; }
        internal string ToolsetProvider { get; init; }
        internal int ImportedEnvironmentCount { get; init; }
        internal string ToolsetDefinitionLocations { get; init; }
        internal string MSBuildToolsDirectory { get; init; }
        internal string MSBuildSdksPath { get; init; }
        internal string MSBuildExtensionsPath { get; init; }
        internal string VisualStudioInstallRoot { get; init; }
        internal EvaluationNamedValueObservation[] GlobalProperties { get; init; }
        internal string[] CommandLineProperties { get; init; }
    }

    internal readonly struct EvaluationPathProbeObservation
    {
        internal EvaluationPathProbeObservation(string path, EvaluationPathKind kind, bool exists, string provider)
        {
            Path = path;
            Kind = kind;
            Exists = exists;
            Provider = provider;
        }

        internal string Path { get; }
        internal EvaluationPathKind Kind { get; }
        internal bool Exists { get; }
        internal string Provider { get; }
    }

    internal readonly struct EvaluationDirectoryEnumerationObservation
    {
        internal EvaluationDirectoryEnumerationObservation(
            string path,
            string searchPattern,
            SearchOption searchOption,
            EvaluationEnumerationKind kind,
            string[] entries,
            int entryCount,
            string entriesHash,
            string provider,
            EvaluationEnumerationCompletion completion)
        {
            Path = path;
            SearchPattern = searchPattern;
            SearchOption = searchOption;
            Kind = kind;
            Entries = entries;
            EntryCount = entryCount;
            EntriesHash = entriesHash;
            Provider = provider;
            Completion = completion;
        }

        internal string Path { get; }
        internal string SearchPattern { get; }
        internal SearchOption SearchOption { get; }
        internal EvaluationEnumerationKind Kind { get; }
        internal string[] Entries { get; }
        internal int EntryCount { get; }
        internal string EntriesHash { get; }
        internal string Provider { get; }
        internal EvaluationEnumerationCompletion Completion { get; }
    }

    internal readonly struct EvaluationMetadataObservation
    {
        internal EvaluationMetadataObservation(
            string path,
            EvaluationMetadataKind kind,
            long value,
            string provider = null)
        {
            Path = path;
            Kind = kind;
            Value = value;
            TextValue = value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            BaseDirectory = null;
            Operation = null;
            Provider = provider;
        }

        internal EvaluationMetadataObservation(
            string path,
            EvaluationMetadataKind kind,
            string value,
            string baseDirectory,
            string operation,
            string provider)
        {
            Path = path;
            Kind = kind;
            Value = 0;
            TextValue = value;
            BaseDirectory = baseDirectory;
            Operation = operation;
            Provider = provider;
        }

        internal string Path { get; }
        internal EvaluationMetadataKind Kind { get; }
        internal long Value { get; }
        internal string TextValue { get; }
        internal string BaseDirectory { get; }
        internal string Operation { get; }
        internal string Provider { get; }
    }

    internal readonly struct EvaluationFileReadObservation
    {
        internal EvaluationFileReadObservation(
            string path,
            string contentHash,
            bool isVerifiable,
            EvaluationContentHashKind hashKind,
            string provider)
        {
            Path = path;
            ContentHash = contentHash;
            IsVerifiable = isVerifiable;
            HashKind = hashKind;
            Provider = provider;
        }

        internal string Path { get; }
        internal string ContentHash { get; }
        internal bool IsVerifiable { get; }
        internal EvaluationContentHashKind HashKind { get; }
        internal string Provider { get; }
    }

    internal readonly struct EvaluationProjectSourceObservation
    {
        internal EvaluationProjectSourceObservation(
            EvaluationProjectSourceRole role,
            string path,
            int version,
            string contentHash,
            EvaluationContentHashKind hashKind,
            string encoding,
            string provider)
        {
            Role = role;
            Path = path;
            Version = version;
            ContentHash = contentHash;
            HashKind = hashKind;
            Encoding = encoding;
            Provider = provider;
        }

        internal EvaluationProjectSourceRole Role { get; }
        internal string Path { get; }
        internal int Version { get; }
        internal string ContentHash { get; }
        internal EvaluationContentHashKind HashKind { get; }
        internal string Encoding { get; }
        internal string Provider { get; }
    }

    internal readonly struct EvaluationGlobObservation
    {
        internal EvaluationGlobObservation(
            string role,
            string directory,
            string include,
            string[] excludes,
            int excludeCount,
            string excludesFingerprint,
            string[] results,
            int resultCount,
            string resultsFingerprint,
            bool resultsEscaped,
            bool wasLazy,
            bool driveEnumerating,
            string failure)
        {
            Role = role;
            Directory = directory;
            Include = include;
            Excludes = excludes;
            ExcludeCount = excludeCount;
            ExcludesFingerprint = excludesFingerprint;
            Results = results;
            ResultCount = resultCount;
            ResultsFingerprint = resultsFingerprint;
            ResultsEscaped = resultsEscaped;
            WasLazy = wasLazy;
            DriveEnumerating = driveEnumerating;
            Failure = failure;
        }

        internal string Role { get; }
        internal string Directory { get; }
        internal string Include { get; }
        internal string[] Excludes { get; }
        internal int ExcludeCount { get; }
        internal string ExcludesFingerprint { get; }
        internal string[] Results { get; }
        internal int ResultCount { get; }
        internal string ResultsFingerprint { get; }
        internal bool ResultsEscaped { get; }
        internal bool WasLazy { get; }
        internal bool DriveEnumerating { get; }
        internal string Failure { get; }
    }

    internal readonly struct EvaluationSearchObservation
    {
        internal EvaluationSearchObservation(
            string kind,
            string request,
            string[] candidates,
            int candidateCount,
            string candidatesFingerprint,
            string selected,
            bool complete)
        {
            Kind = kind;
            Request = request;
            Candidates = candidates;
            CandidateCount = candidateCount;
            CandidatesFingerprint = candidatesFingerprint;
            Selected = selected;
            Complete = complete;
        }

        internal string Kind { get; }
        internal string Request { get; }
        internal string[] Candidates { get; }
        internal int CandidateCount { get; }
        internal string CandidatesFingerprint { get; }
        internal string Selected { get; }
        internal bool Complete { get; }
    }

    internal readonly struct EvaluationEnvironmentObservation
    {
        internal EvaluationEnvironmentObservation(
            string name,
            EvaluationEnvironmentSource source,
            bool present,
            string value)
        {
            Name = name;
            Source = source;
            Present = present;
            Value = value;
        }

        internal string Name { get; }
        internal EvaluationEnvironmentSource Source { get; }
        internal bool Present { get; }
        internal string Value { get; }
    }

    internal readonly struct EvaluationExternalInputObservation
    {
        internal EvaluationExternalInputObservation(
            EvaluationExternalInputKind kind,
            string operation,
            string request,
            string result)
        {
            Kind = kind;
            Operation = operation;
            Request = request;
            Result = result;
        }

        internal EvaluationExternalInputKind Kind { get; }
        internal string Operation { get; }
        internal string Request { get; }
        internal string Result { get; }
    }

    internal readonly struct EvaluationPropertyFunctionObservation
    {
        internal EvaluationPropertyFunctionObservation(
            string receiverType,
            string member,
            string instance,
            EvaluationPropertyFunctionEffect effects,
            string[] arguments,
            string result,
            bool succeeded)
        {
            ReceiverType = receiverType;
            Member = member;
            Instance = instance;
            Effects = effects;
            Arguments = arguments;
            Result = result;
            Succeeded = succeeded;
        }

        internal string ReceiverType { get; }
        internal string Member { get; }
        internal string Instance { get; }
        internal EvaluationPropertyFunctionEffect Effects { get; }
        internal string[] Arguments { get; }
        internal string Result { get; }
        internal bool Succeeded { get; }
    }

    internal readonly struct EvaluationSdkResolutionObservation
    {
        internal EvaluationSdkResolutionObservation(
            string sdkName,
            string requestedVersion,
            string minimumVersion,
            bool success,
            string path,
            string version,
            bool fromCache,
            string[] additionalPaths,
            EvaluationNamedValueObservation[] propertiesToAdd,
            EvaluationSdkItemObservation[] itemsToAdd,
            EvaluationNamedValueObservation[] environmentVariablesToAdd,
            string[] warnings,
            string[] errors)
        {
            SdkName = sdkName;
            RequestedVersion = requestedVersion;
            MinimumVersion = minimumVersion;
            Success = success;
            Path = path;
            Version = version;
            FromCache = fromCache;
            AdditionalPaths = additionalPaths;
            PropertiesToAdd = propertiesToAdd;
            ItemsToAdd = itemsToAdd;
            EnvironmentVariablesToAdd = environmentVariablesToAdd;
            Warnings = warnings;
            Errors = errors;
        }

        internal string SdkName { get; }
        internal string RequestedVersion { get; }
        internal string MinimumVersion { get; }
        internal bool Success { get; }
        internal string Path { get; }
        internal string Version { get; }
        internal bool FromCache { get; }
        internal string[] AdditionalPaths { get; }
        internal EvaluationNamedValueObservation[] PropertiesToAdd { get; }
        internal EvaluationSdkItemObservation[] ItemsToAdd { get; }
        internal EvaluationNamedValueObservation[] EnvironmentVariablesToAdd { get; }
        internal string[] Warnings { get; }
        internal string[] Errors { get; }
    }

    internal readonly struct EvaluationSdkItemObservation
    {
        internal EvaluationSdkItemObservation(
            string itemType,
            string itemSpec,
            EvaluationNamedValueObservation[] metadata)
        {
            ItemType = itemType;
            ItemSpec = itemSpec;
            Metadata = metadata;
        }

        internal string ItemType { get; }
        internal string ItemSpec { get; }
        internal EvaluationNamedValueObservation[] Metadata { get; }
    }

    internal readonly struct EvaluationTaskRegistrationObservation
    {
        internal EvaluationTaskRegistrationObservation(
            string taskName,
            string taskFactory,
            string assemblyFile,
            string assemblyName,
            string runtime,
            string architecture,
            bool isOverride)
        {
            TaskName = taskName;
            TaskFactory = taskFactory;
            AssemblyFile = assemblyFile;
            AssemblyName = assemblyName;
            Runtime = runtime;
            Architecture = architecture;
            IsOverride = isOverride;
        }

        internal string TaskName { get; }
        internal string TaskFactory { get; }
        internal string AssemblyFile { get; }
        internal string AssemblyName { get; }
        internal string Runtime { get; }
        internal string Architecture { get; }
        internal bool IsOverride { get; }
    }

    internal readonly struct EvaluationSideEffectObservation
    {
        internal EvaluationSideEffectObservation(string kind, string identity, string value)
        {
            Kind = kind;
            Identity = identity;
            Value = value;
        }

        internal string Kind { get; }
        internal string Identity { get; }
        internal string Value { get; }
    }

    internal readonly struct EvaluationCategoryObservation
    {
        internal EvaluationCategoryObservation(
            EvaluationObservationCategory category,
            EvaluationObservationCoverage coverage,
            EvaluationObservationCategoryState state)
        {
            Category = category;
            Coverage = coverage;
            State = state;
        }

        internal EvaluationObservationCategory Category { get; }
        internal EvaluationObservationCoverage Coverage { get; }
        internal EvaluationObservationCategoryState State { get; }
    }

    internal sealed class EvaluationObservationReport
    {
        // This report contains exact values consumed by evaluation, including environment
        // and property-function values. It must not be logged or serialized without redaction.
        internal EvaluationObservationReport(
            int evaluationId,
            string projectPath,
            bool evaluationSucceeded,
            EvaluationObservationReason reasons,
            int schemaVersion,
            int propertyFunctionClassificationVersion,
            EvaluationCategoryObservation[] categories,
            EvaluationRequestObservation request,
            EvaluationProjectSourceObservation[] projectSources,
            EvaluationPathProbeObservation[] pathProbes,
            EvaluationDirectoryEnumerationObservation[] directoryEnumerations,
            EvaluationMetadataObservation[] metadataReads,
            EvaluationFileReadObservation[] fileReads,
            EvaluationGlobObservation[] globs,
            EvaluationSearchObservation[] searches,
            EvaluationEnvironmentObservation[] environment,
            EvaluationExternalInputObservation[] externalInputs,
            EvaluationPropertyFunctionObservation[] propertyFunctions,
            EvaluationSdkResolutionObservation[] sdkResolutions,
            EvaluationTaskRegistrationObservation[] taskRegistrations,
            EvaluationSideEffectObservation[] sideEffects)
        {
            EvaluationId = evaluationId;
            ProjectPath = projectPath;
            EvaluationSucceeded = evaluationSucceeded;
            Reasons = reasons;
            SchemaVersion = schemaVersion;
            PropertyFunctionClassificationVersion = propertyFunctionClassificationVersion;
            Categories = categories;
            Request = request;
            ProjectSources = projectSources;
            PathProbes = pathProbes;
            DirectoryEnumerations = directoryEnumerations;
            MetadataReads = metadataReads;
            FileReads = fileReads;
            Globs = globs;
            Searches = searches;
            Environment = environment;
            ExternalInputs = externalInputs;
            PropertyFunctions = propertyFunctions;
            SdkResolutions = sdkResolutions;
            TaskRegistrations = taskRegistrations;
            SideEffects = sideEffects;
        }

        internal int EvaluationId { get; }
        internal string ProjectPath { get; }
        internal bool EvaluationSucceeded { get; }
        internal EvaluationObservationReason Reasons { get; }
        internal int SchemaVersion { get; }
        internal int PropertyFunctionClassificationVersion { get; }
        internal EvaluationCategoryObservation[] Categories { get; }
        internal EvaluationRequestObservation Request { get; }
        internal EvaluationProjectSourceObservation[] ProjectSources { get; }
        internal EvaluationPathProbeObservation[] PathProbes { get; }
        internal EvaluationDirectoryEnumerationObservation[] DirectoryEnumerations { get; }
        internal EvaluationMetadataObservation[] MetadataReads { get; }
        internal EvaluationFileReadObservation[] FileReads { get; }
        internal EvaluationGlobObservation[] Globs { get; }
        internal EvaluationSearchObservation[] Searches { get; }
        internal EvaluationEnvironmentObservation[] Environment { get; }
        internal EvaluationExternalInputObservation[] ExternalInputs { get; }
        internal EvaluationPropertyFunctionObservation[] PropertyFunctions { get; }
        internal EvaluationSdkResolutionObservation[] SdkResolutions { get; }
        internal EvaluationTaskRegistrationObservation[] TaskRegistrations { get; }
        internal EvaluationSideEffectObservation[] SideEffects { get; }

        internal bool HasBlockingObservations
        {
            get
            {
                if (Reasons != EvaluationObservationReason.None)
                {
                    return true;
                }

                foreach (EvaluationCategoryObservation category in Categories)
                {
                    if (category.State is EvaluationObservationCategoryState.Incomplete or
                        EvaluationObservationCategoryState.Unsupported)
                    {
                        return true;
                    }
                }

                return false;
            }
        }
    }
}
