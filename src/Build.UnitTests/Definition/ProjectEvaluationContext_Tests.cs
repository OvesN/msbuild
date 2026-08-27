// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Xml;
using Microsoft.Build.BackEnd.SdkResolution;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Engine.UnitTests.InstanceFromRemote;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.Unittest;
using Shouldly;
using Xunit;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

#nullable disable

namespace Microsoft.Build.UnitTests.Definition
{
    /// <summary>
    ///     Tests some manipulations of Project and ProjectCollection that require dealing with internal data.
    /// </summary>
    public class ProjectEvaluationContext_Tests : IDisposable
    {
        public ProjectEvaluationContext_Tests()
        {
            _env = TestEnvironment.Create();

            _resolver = new SdkUtilities.ConfigurableMockSdkResolver(
                new Dictionary<string, SdkResult>
                {
                    {"foo", new SdkResult(new SdkReference("foo", "1.0.0", null), "path", "1.0.0", null) },
                    {"bar", new SdkResult(new SdkReference("bar", "1.0.0", null), "path", "1.0.0", null) }
                });
        }

        public void Dispose()
        {
            _env.Dispose();
        }

        private readonly SdkUtilities.ConfigurableMockSdkResolver _resolver;
        private readonly TestEnvironment _env;

        private sealed class TransientAppContextSwitch : TransientTestState
        {
            private readonly bool _originalValue;
            private readonly string _switchName;
            private readonly bool _switchWasSet;

            internal TransientAppContextSwitch(string switchName, bool value)
            {
                _switchName = switchName;
                _switchWasSet = AppContext.TryGetSwitch(switchName, out _originalValue);
                AppContext.SetSwitch(switchName, value);
            }

            public override void Revert()
            {
                if (_switchWasSet)
                {
                    AppContext.SetSwitch(_switchName, _originalValue);
                    return;
                }

                foreach (FieldInfo field in typeof(AppContext).GetFields(BindingFlags.NonPublic | BindingFlags.Static))
                {
                    if (field.GetValue(null) is System.Collections.IDictionary switches)
                    {
                        lock (switches)
                        {
                            if (switches.Contains(_switchName))
                            {
                                switches.Remove(_switchName);
                                return;
                            }
                        }
                    }
                }

                throw new InvalidOperationException($"Could not restore unset AppContext switch '{_switchName}'.");
            }
        }

        private sealed class TransientThreadWorkingDirectory : TransientTestState
        {
            private readonly string _originalValue = FileUtilities.CurrentThreadWorkingDirectory;

            internal TransientThreadWorkingDirectory(string value)
            {
                FileUtilities.CurrentThreadWorkingDirectory = value;
            }

            public override void Revert()
            {
                FileUtilities.CurrentThreadWorkingDirectory = _originalValue;
            }
        }

        private static void SetResolverForContext(EvaluationContext context, SdkResolver resolver)
        {
            var sdkService = (SdkResolverService)context.SdkResolverService;

            sdkService.InitializeForTests(null, new List<SdkResolver> { resolver });
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        public void SharedContextShouldGetReusedWhereasIsolatedContextShouldNot(EvaluationContext.SharingPolicy policy)
        {
            var previousContext = EvaluationContext.Create(policy);

            for (var i = 0; i < 10; i++)
            {
                var currentContext = previousContext.ContextForNewProject();

                if (i == 0)
                {
                    currentContext.ShouldBeSameAs(previousContext, "first usage context was not the same as the initial context");
                }
                else
                {
                    switch (policy)
                    {
                        case EvaluationContext.SharingPolicy.Shared:
                            currentContext.ShouldBeSameAs(previousContext, $"Shared policy: usage {i} was not the same as usage {i - 1}");
                            break;
                        case EvaluationContext.SharingPolicy.SharedSDKCache:
                            currentContext.ShouldNotBeSameAs(previousContext, $"SharedSDKCache policy: usage {i} was the same as usage {i - 1}");
                            break;
                        case EvaluationContext.SharingPolicy.Isolated:
                            currentContext.ShouldNotBeSameAs(previousContext, $"Isolated policy: usage {i} was the same as usage {i - 1}");
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                    }
                }

                previousContext = currentContext;
            }
        }

        [Fact]
        public void PassedInFileSystemShouldBeReusedInSharedContext()
        {
            var projectFiles = new[]
            {
                _env.CreateFile("1.proj", @"<Project> <PropertyGroup Condition=`Exists('1.file')`></PropertyGroup> </Project>".Cleanup()).Path,
                _env.CreateFile("2.proj", @"<Project> <PropertyGroup Condition=`Exists('2.file')`></PropertyGroup> </Project>".Cleanup()).Path
            };

            var projectCollection = _env.CreateProjectCollection().Collection;
            var fileSystem = new Helpers.LoggingFileSystem();
            var evaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared, fileSystem);

            foreach (var projectFile in projectFiles)
            {
                Project.FromFile(
                    projectFile,
                    new ProjectOptions
                    {
                        ProjectCollection = projectCollection,
                        EvaluationContext = evaluationContext
                    });
            }

            fileSystem.ExistenceChecks.OrderBy(kvp => kvp.Key)
                .ShouldBe(
                    new Dictionary<string, int>
                    {
                        {Path.Combine(_env.DefaultTestDirectory.Path, "1.file"), 1},
                        {Path.Combine(_env.DefaultTestDirectory.Path, "2.file"), 1}
                    }.OrderBy(kvp => kvp.Key));

            fileSystem.FileOrDirectoryExistsCalls.ShouldBe(2);
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        public void NonSharedContextShouldNotSupportBeingPassedAFileSystem(EvaluationContext.SharingPolicy policy)
        {
            var fileSystem = new Helpers.LoggingFileSystem();
            Should.Throw<ArgumentException>(() => EvaluationContext.Create(policy, fileSystem));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EvaluationShouldUseDirectoryCache(bool useProjectInstance)
        {
            var projectFile = _env.CreateFile("1.proj", @"<Project> <Import Project='1.file' Condition=`Exists('1.file')`/> <ItemGroup><Compile Include='*.cs'/></ItemGroup> </Project>".Cleanup()).Path;

            var projectCollection = _env.CreateProjectCollection().Collection;
            var directoryCacheFactory = new Helpers.LoggingDirectoryCacheFactory();

            int expectedEvaluationId;
            if (useProjectInstance)
            {
                var projectInstance = ProjectInstance.FromFile(
                    projectFile,
                    new ProjectOptions
                    {
                        ProjectCollection = projectCollection,
                        DirectoryCacheFactory = directoryCacheFactory,
                    });
                expectedEvaluationId = projectInstance.EvaluationId;
            }
            else
            {
                var project = Project.FromFile(
                    projectFile,
                    new ProjectOptions
                    {
                        ProjectCollection = projectCollection,
                        DirectoryCacheFactory = directoryCacheFactory,
                    });
                expectedEvaluationId = project.LastEvaluationId;
            }

            directoryCacheFactory.DirectoryCaches.Count.ShouldBe(1);
            var directoryCache = directoryCacheFactory.DirectoryCaches[0];

            directoryCache.EvaluationId.ShouldBe(expectedEvaluationId);

            directoryCache.ExistenceChecks.OrderBy(kvp => kvp.Key).ShouldBe(
                new Dictionary<string, int>
                {
                    { _env.DefaultTestDirectory.Path, 1},
                    { Path.Combine(_env.DefaultTestDirectory.Path, "1.file"), 2 }
                }.OrderBy(kvp => kvp.Key));
            directoryCache.Enumerations.ShouldBe(
                new Dictionary<string, int>
                {
                    { _env.DefaultTestDirectory.Path, 1 }
                });
        }

        [Fact]
        public void EvaluationObservationCanBeDisabled()
        {
            int reportsCreated = 0;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: false,
                _ => reportsCreated++);

            string projectFile = _env.CreateFile(
                "disabled.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('disabled.marker')">
                    <Observed>true</Observed>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            reportsCreated.ShouldBe(0);
        }

        [Fact]
        public void EvaluationObservationFeatureSwitchRegistryIsExplicitlyClassified()
        {
            HashSet<string> actual = typeof(FeatureSwitches)
                .GetProperties(BindingFlags.Static | BindingFlags.NonPublic)
                .Where(property => property.PropertyType == typeof(bool))
                .Select(property => property.Name)
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> classified =
            [
                nameof(FeatureSwitches.EnableCustomPluginProbing),
                nameof(FeatureSwitches.EnableAllPropertyFunctions),
                nameof(FeatureSwitches.RestrictPropertyFunctionReceivers),
                nameof(FeatureSwitches.EnableSdkResolverDynamicLoading),
                nameof(FeatureSwitches.EnableConfigurationFileToolsets),
                // Execution-only switches are intentionally outside evaluation observation.
                nameof(FeatureSwitches.EnableReflectiveTaskExecution),
                nameof(FeatureSwitches.EnableReflectiveTaskParameterTypes),
                nameof(FeatureSwitches.EnableReflectiveLoggerLoading),
            ];

            actual.SetEquals(classified).ShouldBeTrue(
                $"Feature switches must be explicitly added to the evaluation request or classified execution-only. Actual: {string.Join(", ", actual.OrderBy(static name => name))}");
        }

        [Fact]
        public void EvaluationObservationEscapeHatchRegistryIsExplicitlyClassified()
        {
            HashSet<string> actual = typeof(EscapeHatches)
                .GetFields(BindingFlags.Instance | BindingFlags.Public)
                .Select(field => field.Name)
                .Concat(
                    typeof(EscapeHatches)
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                        .Select(property => property.Name))
                .ToHashSet(StringComparer.Ordinal);
            HashSet<string> classified =
            [
                // Evaluation-result-affecting values captured in EvaluationRequestObservation.
                nameof(EscapeHatches.AlwaysDoImmutableFilesUpToDateCheck),
                nameof(EscapeHatches.AlwaysEvaluateDangerousGlobs),
                nameof(EscapeHatches.AlwaysUseContentTimestamp),
                nameof(EscapeHatches.DisableLongPaths),
                nameof(EscapeHatches.DisableParseConfig),
                nameof(EscapeHatches.DisableSdkResolutionCache),
                nameof(EscapeHatches.DoNotExpandQualifiedMetadataInUpdateOperation),
                nameof(EscapeHatches.DoNotTruncateConditions),
                nameof(EscapeHatches.EvaluateElementsWithFalseConditionInProjectEvaluation),
                nameof(EscapeHatches.IgnoreEmptyImports),
                nameof(EscapeHatches.IgnoreTreatAsLocalProperty),
                nameof(EscapeHatches.SdkReferencePropertyExpansion),
                nameof(EscapeHatches.UseCaseSensitiveItemNames),
                nameof(EscapeHatches.UseSymlinkTimeInsteadOfTargetTime),

                // Diagnostics, execution, task, IPC, or build-result behavior outside project evaluation.
                nameof(EscapeHatches.AvoidUnicodeWhenWritingToolTaskBatch),
                nameof(EscapeHatches.CacheAssemblyInformation),
                nameof(EscapeHatches.CopyWithoutDelete),
                nameof(EscapeHatches.DebugEvaluation),
                nameof(EscapeHatches.DoNotLimitBuildCheckResultsNumber),
                nameof(EscapeHatches.DoNotSendDeferredMessagesToBuildManager),
                nameof(EscapeHatches.DoNotVersionBuildResult),
                nameof(EscapeHatches.EnsureStdOutForChildNodesIsPrimaryStdout),
                nameof(EscapeHatches.LogProjectImports),
                nameof(EscapeHatches.LogPropertiesAndItemsAfterEvaluation),
                nameof(EscapeHatches.LogTaskInputs),
                nameof(EscapeHatches.ProjectInstanceTranslation),
                nameof(EscapeHatches.ReuseTaskHostNodes),
                nameof(EscapeHatches.TargetPathForRelatedFiles),
                nameof(EscapeHatches.TruncateTaskInputs),
                nameof(EscapeHatches.UseAutoRunWhenLaunchingProcessUnderCmd),
                nameof(EscapeHatches.UseCustomLoadContextForDependenciesInToolsDirectory),
                nameof(EscapeHatches.UseMinimalResxParsingInCoreScenarios),
                nameof(EscapeHatches.UseSingleLoadContext),
                nameof(EscapeHatches.WarnOnUninitializedProperty),
            ];

            actual.SetEquals(classified).ShouldBeTrue(
                $"Escape hatches must be explicitly captured or classified outside evaluation. Actual: {string.Join(", ", actual.OrderBy(static name => name))}");
        }

        [Fact]
        public void EvaluationObservationDoesNotChangeEvaluatedState()
        {
            _env.SetEnvironmentVariable("OBSERVATION_EQUIVALENCE_ENV", "equivalent");
            _env.CreateFile("state.marker", string.Empty);
            _env.CreateFile("State.cs", string.Empty);
            _env.CreateFile("state.txt", "state-content");
            string projectFile = _env.CreateFile(
                "state.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('state.marker')">
                    <Observed>true</Observed>
                    <Environment>$(OBSERVATION_EQUIVALENCE_ENV)</Environment>
                    <Content>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)state.txt'))</Content>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="*.cs" />
                    <Input Include="state.txt" />
                    <MetadataValue Include="@(Input->'%(ModifiedTime)')" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Project baseline;
            using (EvaluationObservationSession.TestOnlyConfigure(enabled: false))
            {
                baseline = Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            }

            EvaluationObservationReport report = null;
            Project observed;
            using (EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport))
            {
                observed = Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            }

            report.ShouldNotBeNull();
            observed.Properties
                .Select(property => string.Concat(property.Name, "=", property.EvaluatedValue))
                .OrderBy(static property => property, StringComparer.OrdinalIgnoreCase)
                .ShouldBe(
                    baseline.Properties
                        .Select(property => string.Concat(property.Name, "=", property.EvaluatedValue))
                        .OrderBy(static property => property, StringComparer.OrdinalIgnoreCase));
            observed.Items
                .Select(item => string.Concat(
                    item.ItemType,
                    "|",
                    item.EvaluatedInclude,
                    "|",
                    string.Join(
                        ";",
                        item.Metadata
                            .Select(metadata => string.Concat(metadata.Name, "=", metadata.EvaluatedValue))
                            .OrderBy(static metadata => metadata, StringComparer.OrdinalIgnoreCase))))
                .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase)
                .ShouldBe(
                    baseline.Items
                        .Select(item => string.Concat(
                            item.ItemType,
                            "|",
                            item.EvaluatedInclude,
                            "|",
                            string.Join(
                                ";",
                                item.Metadata
                                    .Select(metadata => string.Concat(metadata.Name, "=", metadata.EvaluatedValue))
                                    .OrderBy(static metadata => metadata, StringComparer.OrdinalIgnoreCase))))
                        .OrderBy(static item => item, StringComparer.OrdinalIgnoreCase));
        }

        [WindowsFullFrameworkOnlyFact]
        public void EvaluationObservationDoesNotRejectInvalidFileMetadataPath()
        {
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(enabled: true);
            string projectFile = _env.CreateFile(
                "invalid-metadata-path.proj",
                """
                <Project>
                  <ItemGroup>
                    <A Include="Name|Value" />
                    <B Include="@(A->'%(ModifiedTime)')" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Should.NotThrow(() => Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            }));
        }

        [Fact]
        public void EvaluationObservationRecordsProbesAndGlobs()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            _env.CreateFile("Observed.cs", string.Empty);
            string importedProject = _env.CreateFile(
                "observed.props",
                """
                <Project>
                  <PropertyGroup>
                    <ImportedValue>true</ImportedValue>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;
            string projectFile = _env.CreateFile(
                "observed.proj",
                """
                <Project>
                  <Import Project="observed.props" />
                  <PropertyGroup Condition="Exists('missing.props')">
                    <Imported>true</Imported>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="*.cs" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.ProjectPath.ShouldBe(projectFile);
            report.HasBlockingObservations.ShouldBeTrue();
            report.EvaluationSucceeded.ShouldBeTrue();
            (report.Reasons & EvaluationObservationReason.ParsedProjectSourceOnly)
                .ShouldBe(EvaluationObservationReason.None);
            report.Request.ProjectPath.ShouldBe(projectFile);
            report.Request.EngineVersion.ShouldNotBe(report.Request.EngineAssemblyVersion);
            Assembly engineAssembly = typeof(Project).Assembly;
            report.Request.EngineVersion.ShouldBe(
                engineAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
                System.Diagnostics.FileVersionInfo.GetVersionInfo(engineAssembly.Location).FileVersion);
            report.Request.EngineAssemblyVersion.ShouldBe(engineAssembly.GetName().Version?.ToString());
            report.Request.Runtime.ShouldBe(
                System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            report.Request.OperatingSystem.ShouldBe(
                System.Runtime.InteropServices.RuntimeInformation.OSDescription);
            report.Request.ProcessArchitecture.ShouldBe(
                System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString());
            report.Request.PathComparison.ShouldBe(FileUtilities.PathComparison.ToString());
            report.ProjectSources.ShouldContain(observation =>
                observation.Role == EvaluationProjectSourceRole.Root &&
                observation.Outcome == EvaluationProjectSourceOutcome.Parsed &&
                FileUtilities.PathsEqual(observation.Path, projectFile) &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.ContentHash == EvaluationObservationSession.ComputeBytesHash(File.ReadAllBytes(projectFile)));
            report.ProjectSources.ShouldContain(observation =>
                observation.Role == EvaluationProjectSourceRole.Import &&
                observation.Outcome == EvaluationProjectSourceOutcome.Parsed &&
                FileUtilities.PathsEqual(observation.Path, importedProject) &&
                observation.ContentHash == EvaluationObservationSession.ComputeBytesHash(File.ReadAllBytes(importedProject)));

            string missingPath = Path.Combine(_env.DefaultTestDirectory.Path, "missing.props");
            report.PathProbes.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, missingPath) &&
                observation.Kind == EvaluationPathKind.FileOrDirectory &&
                !observation.Exists);

            string observedFile = Path.Combine(_env.DefaultTestDirectory.Path, "Observed.cs");
            report.Globs.ShouldContain(observation =>
                observation.Include == "*.cs" &&
                observation.Results.Any(entry => entry.EndsWith("Observed.cs", StringComparison.OrdinalIgnoreCase)));
            report.DirectoryEnumerations.ShouldBeEmpty(
                string.Join(
                    Environment.NewLine,
                    report.DirectoryEnumerations.Select(observation =>
                        string.Concat(observation.Kind, "|", observation.Path, "|", observation.SearchPattern))));

            project.GetItems("Compile").ShouldContain(item =>
                string.Equals(Path.GetFileName(item.EvaluatedInclude), "Observed.cs", StringComparison.OrdinalIgnoreCase));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EvaluationObservationRecordsMalformedImportBytes(bool ignoreInvalidImport)
        {
            string malformedImport = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "malformed.props");
            byte[] malformedBytes = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"windows-1252\"?>" +
                "<Project><PropertyGroup><Value>before</Value></Project>" +
                new string('x', 128 * 1024));
            File.WriteAllBytes(malformedImport, malformedBytes);
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(malformedBytes);
            string projectFile = _env.CreateFile(
                "malformed-import.proj",
                """
                <Project>
                  <Import Project="malformed.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            Action evaluate = () => Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
                LoadSettings = ignoreInvalidImport
                    ? ProjectLoadSettings.IgnoreInvalidImports
                    : ProjectLoadSettings.Default,
            });
            if (ignoreInvalidImport)
            {
                Should.NotThrow(evaluate);
            }
            else
            {
                Should.Throw<InvalidProjectFileException>(evaluate);
            }

            report.ShouldNotBeNull();
            report.EvaluationSucceeded.ShouldBe(ignoreInvalidImport);
            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => FileUtilities.PathsEqual(observation.Path, malformedImport));
            source.Role.ShouldBe(EvaluationProjectSourceRole.Import);
            source.Outcome.ShouldBe(EvaluationProjectSourceOutcome.ParseFailure);
            source.Version.ShouldBe(0);
            source.ContentHash.ShouldBe(expectedHash);
            source.HashKind.ShouldBe(EvaluationContentHashKind.RawBytes);
            source.Encoding.ShouldBe(
                "windows-1252",
                StringCompareShould.IgnoreCase);
            source.Provider.ShouldBe("Disk");
            source.HasLastWriteTimeUtc.ShouldBeTrue();
            source.TimestampWasStableDuringRead.ShouldBeTrue();
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, malformedImport) &&
                observation.ContentHash == expectedHash &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.IsVerifiable);
            EvaluationOperationFailureObservation failure =
                report.OperationFailures.Single(
                    observation => FileUtilities.PathsEqual(observation.Path, malformedImport));
            failure.Category.ShouldBe(EvaluationObservationCategory.ProjectSource);
            failure.Operation.ShouldBe("ProjectSource.Parse");
            failure.Provider.ShouldBe("Disk");
            failure.ExceptionType.ShouldBe(typeof(XmlException).FullName);
            (report.Reasons & EvaluationObservationReason.ExternalOperationFailure)
                .ShouldBe(EvaluationObservationReason.ExternalOperationFailure);
            (report.Reasons & EvaluationObservationReason.ProjectXmlContentNotObserved)
                .ShouldBe(EvaluationObservationReason.None);
            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.ProjectSource)
                .State.ShouldBe(EvaluationObservationCategoryState.Incomplete);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EvaluationObservationRecordsMalformedRootBytes(bool useProjectInstance)
        {
            string malformedRoot = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "malformed-root.proj");
            byte[] malformedBytes = Encoding.UTF8.GetBytes(
                "<Project><PropertyGroup><Value>before</Value></Project>" +
                new string('x', 128 * 1024));
            File.WriteAllBytes(malformedRoot, malformedBytes);
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(malformedBytes);
            var reports = new List<EvaluationObservationReport>();
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                reports.Add);
            var options = new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            };

            Action load = useProjectInstance
                ? () => ProjectInstance.FromFile(malformedRoot, options)
                : () => Project.FromFile(malformedRoot, options);

            Should.Throw<InvalidProjectFileException>(load);

            EvaluationObservationReport report = reports.ShouldHaveSingleItem();
            report.ProjectPath.ShouldBe(malformedRoot);
            report.EvaluationSucceeded.ShouldBeFalse();
            report.Request.ShouldBeNull();
            EvaluationProjectSourceObservation source =
                report.ProjectSources.ShouldHaveSingleItem();
            source.Role.ShouldBe(EvaluationProjectSourceRole.Root);
            source.Outcome.ShouldBe(EvaluationProjectSourceOutcome.ParseFailure);
            source.ContentHash.ShouldBe(expectedHash);
            source.HashKind.ShouldBe(EvaluationContentHashKind.RawBytes);
            source.Encoding.ShouldBe(Encoding.UTF8.WebName);
            source.Provider.ShouldBe("Disk");
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, malformedRoot) &&
                observation.ContentHash == expectedHash &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.IsVerifiable);
            EvaluationOperationFailureObservation failure =
                report.OperationFailures.ShouldHaveSingleItem();
            failure.Category.ShouldBe(EvaluationObservationCategory.ProjectSource);
            failure.Operation.ShouldBe("ProjectSource.Parse");
            failure.Path.ShouldBe(malformedRoot);
            failure.ExceptionType.ShouldBe(typeof(XmlException).FullName);
            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.Request)
                .State.ShouldBe(EvaluationObservationCategoryState.Incomplete);
            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.ProjectSource)
                .State.ShouldBe(EvaluationObservationCategoryState.Incomplete);
        }

        [Fact]
        public void EvaluationObservationRecordsInvalidMsbuildImportBytes()
        {
            string invalidImport = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "invalid-msbuild.props");
            byte[] invalidBytes = Encoding.UTF8.GetBytes("<NotProject />");
            File.WriteAllBytes(invalidImport, invalidBytes);
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(invalidBytes);
            string projectFile = _env.CreateFile(
                "invalid-msbuild-import.proj",
                """
                <Project>
                  <Import Project="invalid-msbuild.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            Should.Throw<InvalidProjectFileException>(() =>
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                }));

            report.ShouldNotBeNull();
            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => FileUtilities.PathsEqual(observation.Path, invalidImport));
            source.Outcome.ShouldBe(EvaluationProjectSourceOutcome.ParseFailure);
            source.ContentHash.ShouldBe(expectedHash);
            source.HashKind.ShouldBe(EvaluationContentHashKind.RawBytes);
            EvaluationOperationFailureObservation failure =
                report.OperationFailures.Single(
                    observation => FileUtilities.PathsEqual(observation.Path, invalidImport));
            failure.Operation.ShouldBe("ProjectSource.Parse");
            failure.ExceptionType.ShouldBe(typeof(InvalidProjectFileException).FullName);
        }

        [Fact]
        public void EvaluationObservationClassifiesInvalidXmlEncodingAsParseFailure()
        {
            string invalidImport = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "invalid-encoding.props");
            byte[] invalidBytes = Encoding.UTF8.GetBytes(
                "<?xml version=\"1.0\" encoding=\"utf-42\"?><Project />" +
                new string(' ', 128 * 1024));
            File.WriteAllBytes(invalidImport, invalidBytes);
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(invalidBytes);
            string projectFile = _env.CreateFile(
                "invalid-encoding-import.proj",
                """
                <Project>
                  <Import Project="invalid-encoding.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            Should.Throw<InvalidProjectFileException>(() =>
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                }));

            report.ShouldNotBeNull();
            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => FileUtilities.PathsEqual(observation.Path, invalidImport));
            source.Outcome.ShouldBe(EvaluationProjectSourceOutcome.ParseFailure);
            source.ContentHash.ShouldBe(expectedHash);
            source.HashKind.ShouldBe(EvaluationContentHashKind.RawBytes);
            report.OperationFailures.Single(
                observation => FileUtilities.PathsEqual(observation.Path, invalidImport))
                .Operation.ShouldBe("ProjectSource.Parse");
        }

        [Fact]
        public void EvaluationObservationRecordsImportLoadFailure()
        {
            string importFile = _env.CreateFile("load-failure.props", "<Project />").Path;
            string projectFile = _env.CreateFile(
                "load-failure-import.proj",
                """
                <Project>
                  <Import Project="load-failure.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            Microsoft.Build.Internal.XmlReaderExtension.TestOnlyHookBeforeSourceRead = path =>
            {
                if (FileUtilities.PathsEqual(path, importFile))
                {
                    throw new IOException("Test-only source load failure.");
                }
            };

            try
            {
                Should.Throw<InvalidProjectFileException>(() =>
                    Project.FromFile(projectFile, new ProjectOptions
                    {
                        ProjectCollection = _env.CreateProjectCollection().Collection,
                    }));
            }
            finally
            {
                Microsoft.Build.Internal.XmlReaderExtension.TestOnlyHookBeforeSourceRead = null;
            }

            report.ShouldNotBeNull();
            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => FileUtilities.PathsEqual(observation.Path, importFile));
            source.Outcome.ShouldBe(EvaluationProjectSourceOutcome.LoadFailure);
            source.ContentHash.ShouldBeNull();
            source.HashKind.ShouldBe(EvaluationContentHashKind.Unknown);
            source.HasLastWriteTimeUtc.ShouldBeTrue();
            source.TimestampWasStableDuringRead.ShouldBeTrue();
            EvaluationOperationFailureObservation failure =
                report.OperationFailures.Single(
                    observation => FileUtilities.PathsEqual(observation.Path, importFile));
            failure.Operation.ShouldBe("ProjectSource.Load");
            failure.ExceptionType.ShouldBe(typeof(IOException).FullName);
            (report.Reasons & EvaluationObservationReason.ProjectXmlContentNotObserved)
                .ShouldBe(EvaluationObservationReason.ProjectXmlContentNotObserved);
            (report.Reasons & EvaluationObservationReason.ObservationIncomplete)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationMarksMalformedImportTimestampChange()
        {
            string malformedImport = _env.CreateFile(
                "changing-malformed.props",
                "<Project><PropertyGroup></Project>").Path;
            DateTime initialTime = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(malformedImport, initialTime);
            string projectFile = _env.CreateFile(
                "changing-malformed-import.proj",
                """
                <Project>
                  <Import Project="changing-malformed.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            ProjectRootElement.TestOnlyHookAfterFailedSourceRead = path =>
            {
                if (FileUtilities.PathsEqual(path, malformedImport))
                {
                    File.SetLastWriteTimeUtc(path, initialTime.AddMinutes(1));
                }
            };

            try
            {
                Should.Throw<InvalidProjectFileException>(() =>
                    Project.FromFile(projectFile, new ProjectOptions
                    {
                        ProjectCollection = _env.CreateProjectCollection().Collection,
                    }));
            }
            finally
            {
                ProjectRootElement.TestOnlyHookAfterFailedSourceRead = null;
            }

            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => FileUtilities.PathsEqual(observation.Path, malformedImport));
            source.TimestampWasStableDuringRead.ShouldBeFalse();
            (report.Reasons & EvaluationObservationReason.ProjectSourceChangedDuringRead)
                .ShouldBe(EvaluationObservationReason.ProjectSourceChangedDuringRead);
        }

        [Fact]
        public void EvaluationObservationRetainsImportHashWhenFileIsDeletedAfterRead()
        {
            string importFile = _env.CreateFile(
                "deleted-after-read.props",
                """
                <Project>
                  <PropertyGroup>
                    <ImportedBeforeDeletion>true</ImportedBeforeDeletion>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(
                File.ReadAllBytes(importFile));
            string projectFile = _env.CreateFile(
                "deleted-after-read-import.proj",
                """
                <Project>
                  <Import Project="deleted-after-read.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            ProjectRootElement.TestOnlyHookAfterSourceRead = path =>
            {
                if (FileUtilities.PathsEqual(path, importFile))
                {
                    File.Delete(path);
                }
            };

            Project project;
            try
            {
                project = Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            }
            finally
            {
                ProjectRootElement.TestOnlyHookAfterSourceRead = null;
            }

            project.GetPropertyValue("ImportedBeforeDeletion").ShouldBe("true");
            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => FileUtilities.PathsEqual(observation.Path, importFile));
            source.Outcome.ShouldBe(EvaluationProjectSourceOutcome.Parsed);
            source.ContentHash.ShouldBe(expectedHash);
            source.HashKind.ShouldBe(EvaluationContentHashKind.RawBytes);
            source.HasLastWriteTimeUtc.ShouldBeFalse();
            (report.Reasons & EvaluationObservationReason.ProjectXmlContentNotObserved)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EvaluationObservationKeepsIgnoredMissingImportAsNegativeProbe(
            bool importPathIsDirectory)
        {
            string missingImport = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "missing-import.props");
            if (importPathIsDirectory)
            {
                Directory.CreateDirectory(missingImport);
            }

            string projectFile = _env.CreateFile(
                "ignored-missing-import.proj",
                """
                <Project>
                  <Import Project="missing-import.props" />
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
                LoadSettings = ProjectLoadSettings.IgnoreMissingImports,
            });

            report.ShouldNotBeNull();
            report.PathProbes.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, missingImport) &&
                !observation.Exists);
            report.ProjectSources.ShouldNotContain(observation =>
                FileUtilities.PathsEqual(observation.Path, missingImport));
            report.OperationFailures.ShouldNotContain(observation =>
                FileUtilities.PathsEqual(observation.Path, missingImport));
            (report.Reasons & EvaluationObservationReason.ExternalOperationFailure)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void ProjectInstanceGlobDoesNotRetainSupportingEnumerations()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            TransientTestFolder sourceFolder = _env.CreateFolder(
                Path.Combine(_env.DefaultTestDirectory.Path, "project-instance-src"));
            _env.CreateFile(sourceFolder, "ProjectInstance.cs", string.Empty);
            string projectFile = _env.CreateFile(
                "project-instance-glob.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="project-instance-src/**/*.cs" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            ProjectInstance.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.Globs.ShouldHaveSingleItem();
            report.DirectoryEnumerations.ShouldBeEmpty(
                string.Join(
                    Environment.NewLine,
                    report.DirectoryEnumerations.Select(observation =>
                        string.Concat(observation.Kind, "|", observation.Path, "|", observation.SearchPattern))));
        }

        [Fact]
        public void EvaluationObservationSummaryModeRetainsFingerprintsWithoutMemberArrays()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport,
                retainDetails: false);

            TransientTestFolder sourceFolder = _env.CreateFolder(
                Path.Combine(_env.DefaultTestDirectory.Path, "summary-src"));
            _env.CreateFile(sourceFolder, "Summary.cs", string.Empty);
            _env.CreateFile("summary.marker", string.Empty);
            string projectFile = _env.CreateFile(
                "summary.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Above>$([MSBuild]::GetPathOfFileAbove('summary.marker', '$(MSBuildThisFileDirectory)'))</Above>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="summary-src/**/*.cs" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            EvaluationGlobObservation glob = report.Globs.ShouldHaveSingleItem();
            glob.Results.ShouldBeEmpty();
            glob.ResultCount.ShouldBe(1);
            glob.ResultsFingerprint.ShouldNotBeNullOrEmpty();
            EvaluationSearchObservation search = report.Searches.Single(
                observation => observation.Kind == "GetPathOfFileAbove");
            search.Candidates.ShouldBeEmpty();
            search.CandidateCount.ShouldBeGreaterThan(0);
            search.CandidatesFingerprint.ShouldNotBeNullOrEmpty();
        }

        [Fact]
        public void EvaluationObservationUsesEffectiveEnvironmentNameCaseSemantics()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordEnvironment("Path", EvaluationEnvironmentSource.Imported, present: true, value: "value");
            session.RecordEnvironment("PATH", EvaluationEnvironmentSource.Imported, present: true, value: "value");
            session.RecordEnvironment("LiveName", EvaluationEnvironmentSource.LiveProcess, present: true, value: "value");
            session.RecordEnvironment("LIVENAME", EvaluationEnvironmentSource.LiveProcess, present: true, value: "value");

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.Environment.Count(observation =>
                observation.Source == EvaluationEnvironmentSource.Imported).ShouldBe(1);
            report.Environment.Count(observation =>
                observation.Source == EvaluationEnvironmentSource.LiveProcess).ShouldBe(
                    NativeMethodsShared.IsWindows ? 1 : 2);
        }

        [Fact]
        public void EvaluationObservationRetainsDistinctEnumerationSearchOptions()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordEnumeration(
                _env.DefaultTestDirectory.Path,
                "*",
                SearchOption.AllDirectories,
                EvaluationEnumerationKind.Files,
                [],
                EvaluationEnumerationCompletion.Complete);
            session.RecordEnumeration(
                _env.DefaultTestDirectory.Path,
                "*",
                SearchOption.TopDirectoryOnly,
                EvaluationEnumerationKind.Files,
                [],
                EvaluationEnumerationCompletion.Complete);

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.DirectoryEnumerations.Count.ShouldBe(2);
            report.DirectoryEnumerations.ShouldContain(
                observation => observation.SearchOption == SearchOption.TopDirectoryOnly);
            report.DirectoryEnumerations.ShouldContain(
                observation => observation.SearchOption == SearchOption.AllDirectories);
        }

        [Fact]
        public void EvaluationObservationRecordsRecursiveDirectoryPropertyFunctionArguments()
        {
            string sourceDirectory = _env.CreateFolder().Path;
            _env.CreateFile(Path.Combine(sourceDirectory, "Top.cs"), string.Empty);
            string nestedDirectory = _env.CreateFolder(Path.Combine(sourceDirectory, "nested")).Path;
            _env.CreateFile(Path.Combine(nestedDirectory, "Nested.cs"), string.Empty);
            _env.CreateFile(Path.Combine(nestedDirectory, "Ignored.txt"), string.Empty);
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                "recursive-enumeration.proj",
                $"""
                <Project>
                  <PropertyGroup>
                    <RecursiveCount>$([System.IO.Directory]::GetFiles('{sourceDirectory}', '*.cs', 'System.IO.SearchOption.AllDirectories').Length)</RecursiveCount>
                    <TopCount>$([System.IO.Directory]::GetFiles('{sourceDirectory}', '*.cs', 'System.IO.SearchOption.TopDirectoryOnly').Length)</TopCount>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("RecursiveCount").ShouldBe("2");
            project.GetPropertyValue("TopCount").ShouldBe("1");
            report.ShouldNotBeNull();
            report.DirectoryEnumerations.Count(observation =>
                observation.Path == sourceDirectory &&
                observation.SearchPattern == "*.cs").ShouldBe(2);
            EvaluationDirectoryEnumerationObservation recursive = report.DirectoryEnumerations.Single(
                observation =>
                    observation.Path == sourceDirectory &&
                    observation.SearchPattern == "*.cs" &&
                    observation.SearchOption == SearchOption.AllDirectories);
            recursive.EntryCount.ShouldBe(2);
            recursive.OptionsIdentity.ShouldBe($"{nameof(SearchOption)}:{(int)SearchOption.AllDirectories}");
            (report.Reasons & EvaluationObservationReason.PartialEnumeration)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationRecordsDirectoryInfoEnumerationPatterns()
        {
            string sourceDirectory = _env.CreateFolder().Path;
            _env.CreateFile(Path.Combine(sourceDirectory, "Directory.Build.props"), string.Empty);
            _env.CreateFile(Path.Combine(sourceDirectory, "Directory.Build.targets"), string.Empty);
            string childPath = Path.Combine(sourceDirectory, "child");
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                "directory-info-enumeration.proj",
                $"""
                <Project>
                  <PropertyGroup>
                    <PropsCount>$([System.IO.Directory]::GetParent('{childPath}').GetFiles('*.props').Length)</PropsCount>
                    <TargetsCount>$([System.IO.Directory]::GetParent('{childPath}').GetFiles('*.targets').Length)</TargetsCount>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("PropsCount").ShouldBe("1");
            project.GetPropertyValue("TargetsCount").ShouldBe("1");
            report.ShouldNotBeNull();
            report.DirectoryEnumerations.Count(observation =>
                observation.Path == sourceDirectory &&
                observation.SearchOption == SearchOption.TopDirectoryOnly).ShouldBe(2);
            report.DirectoryEnumerations.ShouldContain(observation =>
                observation.SearchPattern == "*.props" &&
                observation.Entries.ShouldHaveSingleItem().EndsWith(
                    "Directory.Build.props",
                    StringComparison.OrdinalIgnoreCase));
            report.DirectoryEnumerations.ShouldContain(observation =>
                observation.SearchPattern == "*.targets" &&
                observation.Entries.ShouldHaveSingleItem().EndsWith(
                    "Directory.Build.targets",
                    StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void EvaluationObservationSeparatesPathCalculationsFromFileMetadata()
        {
            string missingChild = Path.Combine(_env.DefaultTestDirectory.Path, "missing", "child");
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                "path-calculations.proj",
                $"""
                <Project>
                  <PropertyGroup>
                    <Parent>$([System.IO.Directory]::GetParent('{missingChild}'))</Parent>
                    <ParentFullName>$([System.IO.Directory]::GetParent('{missingChild}').FullName)</ParentFullName>
                    <ParentName>$([System.IO.Directory]::GetParent('{missingChild}').Name)</ParentName>
                    <GrandParent>$([System.IO.Directory]::GetParent('{missingChild}').Parent.FullName)</GrandParent>
                  </PropertyGroup>
                  <ItemGroup>
                    <Ghost Include="ghost.txt" />
                    <GhostFullPath Include="@(Ghost->'%(FullPath)')" />
                    <GhostRootDirectory Include="@(Ghost->'%(RootDir)')" />
                    <GhostRelativeDirectory Include="@(Ghost->'%(RelativeDir)')" />
                    <GhostDirectory Include="@(Ghost->'%(Directory)')" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("Parent").ShouldBe(Path.GetDirectoryName(missingChild));
            report.ShouldNotBeNull();
            report.MetadataReads.ShouldBeEmpty();
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(Directory).FullName &&
                observation.Member == nameof(Directory.GetParent) &&
                observation.Effects == EvaluationPropertyFunctionEffect.Ambient);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(DirectoryInfo).FullName &&
                observation.Member == nameof(DirectoryInfo.FullName) &&
                observation.Effects == EvaluationPropertyFunctionEffect.Ambient);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(DirectoryInfo).FullName &&
                observation.Member == nameof(DirectoryInfo.Parent) &&
                observation.Effects == EvaluationPropertyFunctionEffect.Ambient);
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.Ambient &&
                observation.Operation == $"{typeof(Directory).FullName}::{nameof(Directory.GetParent)}");
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.Ambient &&
                observation.Operation == $"{typeof(DirectoryInfo).FullName}::{nameof(DirectoryInfo.FullName)}");
            foreach (string modifier in new[] { "FullPath", "RootDir", "RelativeDir", "Directory" })
            {
                report.ExternalInputs.ShouldContain(observation =>
                    observation.Kind == EvaluationExternalInputKind.Ambient &&
                    observation.Operation == $"ItemMetadata::{modifier}" &&
                    observation.Request.IndexOf("ItemSpec=ghost.txt", StringComparison.Ordinal) >= 0);
            }

            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.FileMetadata)
                .State.ShouldBe(EvaluationObservationCategoryState.NotExercised);
        }

        [Fact]
        public void EvaluationObservationRetainsRealFileSystemMetadataClassifications()
        {
            string filePath = _env.CreateFile("metadata.txt", "content").Path;
            var fileInfo = new FileInfo(filePath);
            var directoryInfo = new DirectoryInfo(Path.GetDirectoryName(filePath));
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();

            session.RecordPropertyFunction(
                typeof(FileInfo),
                nameof(FileInfo.Attributes),
                fileInfo,
                [],
                fileInfo.Attributes);
            session.RecordPropertyFunction(
                typeof(FileInfo),
                nameof(FileInfo.Length),
                fileInfo,
                [],
                fileInfo.Length);
            session.RecordPropertyFunction(
                typeof(DirectoryInfo),
                nameof(DirectoryInfo.LastWriteTimeUtc),
                directoryInfo,
                [],
                directoryInfo.LastWriteTimeUtc);
            session.RecordPropertyFunction(
                typeof(FileInfo),
                nameof(FileInfo.FullName),
                fileInfo,
                [],
                fileInfo.FullName);
            session.RecordPropertyFunction(
                typeof(FileInfo),
                nameof(FileInfo.DirectoryName),
                fileInfo,
                [],
                fileInfo.DirectoryName);
            session.RecordPropertyFunction(
                typeof(DirectoryInfo),
                nameof(DirectoryInfo.Parent),
                directoryInfo,
                [],
                directoryInfo.Parent);
            session.RecordPropertyFunction(
                typeof(FileInfo),
                "LinkTarget",
                fileInfo,
                [],
                result: null);

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.MetadataReads.ShouldContain(observation =>
                observation.Path == filePath &&
                observation.Operation == $"{typeof(FileInfo).FullName}::{nameof(FileInfo.Attributes)}");
            report.MetadataReads.ShouldContain(observation =>
                observation.Path == filePath &&
                observation.Operation == $"{typeof(FileInfo).FullName}::{nameof(FileInfo.Length)}");
            report.MetadataReads.ShouldContain(observation =>
                observation.Path == directoryInfo.FullName &&
                observation.Operation == $"{typeof(DirectoryInfo).FullName}::{nameof(DirectoryInfo.LastWriteTimeUtc)}");
            report.MetadataReads.ShouldContain(observation =>
                observation.Path == filePath &&
                observation.Operation == $"{typeof(FileInfo).FullName}::LinkTarget");
            report.MetadataReads.ShouldNotContain(observation =>
                observation.Operation == $"{typeof(FileInfo).FullName}::{nameof(FileInfo.FullName)}" ||
                observation.Operation == $"{typeof(FileInfo).FullName}::{nameof(FileInfo.DirectoryName)}" ||
                observation.Operation == $"{typeof(DirectoryInfo).FullName}::{nameof(DirectoryInfo.Parent)}");
            report.ExternalInputs.ShouldContain(observation =>
                observation.Operation == $"{typeof(FileInfo).FullName}::{nameof(FileInfo.FullName)}");
            report.ExternalInputs.ShouldContain(observation =>
                observation.Operation == $"{typeof(FileInfo).FullName}::{nameof(FileInfo.DirectoryName)}");
            report.ExternalInputs.ShouldContain(observation =>
                observation.Operation == $"{typeof(DirectoryInfo).FullName}::{nameof(DirectoryInfo.Parent)}");
            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.FileMetadata)
                .State.ShouldBe(EvaluationObservationCategoryState.Observed);
        }

        [Fact]
        public void EvaluationObservationCanonicalizesRelativePropertyFunctionPaths()
        {
            string root = _env.CreateFolder().Path;
            string inputPath = _env.CreateFile(Path.Combine(root, "relative.txt"), "content").Path;
            string enumerationRoot = _env.CreateFolder(Path.Combine(root, "enum")).Path;
            string topFile = _env.CreateFile(Path.Combine(enumerationRoot, "top.txt"), string.Empty).Path;
            string nestedDirectory = _env.CreateFolder(Path.Combine(enumerationRoot, "nested")).Path;
            string nestedFile = _env.CreateFile(Path.Combine(nestedDirectory, "nested.txt"), string.Empty).Path;
            _env.SetCurrentDirectory(root);
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(root, "relative-paths.proj"),
                """
                <Project>
                  <PropertyGroup>
                    <Read>$([System.IO.File]::ReadAllText('relative.txt'))</Read>
                    <Exists>$([System.IO.File]::Exists('relative.txt'))</Exists>
                    <WriteTime>$([System.IO.File]::GetLastWriteTimeUtc('relative.txt'))</WriteTime>
                  </PropertyGroup>
                  <ItemGroup>
                    <Files Include="$([System.IO.Directory]::GetFiles('enum', '*.txt', 'System.IO.SearchOption.AllDirectories'))" />
                    <Input Include="relative.txt" />
                    <Modified Include="@(Input->'%(ModifiedTime)')" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("Read").ShouldBe("content");
            report.ShouldNotBeNull();
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, inputPath) &&
                observation.HashKind == EvaluationContentHashKind.DecodedText);
            report.FileReads.ShouldNotContain(observation => observation.Path == "relative.txt");
            report.PathProbes.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, inputPath) &&
                observation.Kind == EvaluationPathKind.File);
            report.MetadataReads.Count(observation =>
                FileUtilities.PathsEqual(observation.Path, inputPath)).ShouldBe(2);
            EvaluationDirectoryEnumerationObservation enumeration =
                report.DirectoryEnumerations.ShouldHaveSingleItem();
            FileUtilities.PathsEqual(enumeration.Path, enumerationRoot).ShouldBeTrue();
            enumeration.Entries.ShouldBe(
                [topFile, nestedFile],
                ignoreOrder: true);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(File).FullName &&
                observation.Member == nameof(File.ReadAllText) &&
                observation.Arguments.ShouldHaveSingleItem() == "relative.txt");
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(Directory).FullName &&
                observation.Member == nameof(Directory.GetFiles) &&
                observation.Arguments[0] == "enum");
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationCanonicalizesRelativeRecordingFileSystemPaths()
        {
            string root = _env.CreateFolder().Path;
            string inputPath = _env.CreateFile(Path.Combine(root, "input.txt"), "content").Path;
            string enumerationRoot = _env.CreateFolder(Path.Combine(root, "enum")).Path;
            string enumeratedPath = _env.CreateFile(Path.Combine(enumerationRoot, "input.txt"), string.Empty).Path;
            _env.SetCurrentDirectory(root);
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var fileSystem = new RecordingFileSystem(FileSystems.Default, session);

            fileSystem.FileExists("input.txt").ShouldBeTrue();
            fileSystem.GetAttributes("input.txt").ShouldNotBe(FileAttributes.Directory);
            fileSystem.ReadFileAllText("input.txt").ShouldBe("content");
            fileSystem.EnumerateFiles("enum", "*.txt").ShouldHaveSingleItem();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            report.PathProbes.ShouldHaveSingleItem().Path.ShouldBe(inputPath);
            report.MetadataReads.ShouldHaveSingleItem().Path.ShouldBe(inputPath);
            report.FileReads.ShouldHaveSingleItem().Path.ShouldBe(inputPath);
            EvaluationDirectoryEnumerationObservation enumeration =
                report.DirectoryEnumerations.ShouldHaveSingleItem();
            enumeration.Path.ShouldBe(enumerationRoot);
            enumeration.Entries.ShouldHaveSingleItem().ShouldBe(enumeratedPath);
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationPreservesAuthoredPathArgumentsInThreadWorkingDirectoryMode()
        {
            string correctRoot = _env.CreateFolder().Path;
            string wrongRoot = _env.CreateFolder().Path;
            string inputPath = _env.CreateFile(Path.Combine(correctRoot, "relative.txt"), "content").Path;
            _env.SetCurrentDirectory(wrongRoot);
            _env.WithTransientTestState(new TransientThreadWorkingDirectory(correctRoot));
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(correctRoot, "thread-working-directory.proj"),
                """
                <Project>
                  <PropertyGroup>
                    <Read>$([System.IO.File]::ReadAllText('relative.txt'))</Read>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("Read").ShouldBe("content");
            report.ShouldNotBeNull();
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, inputPath));
            EvaluationPropertyFunctionObservation function = report.PropertyFunctions.Single(
                observation =>
                    observation.ReceiverType == typeof(File).FullName &&
                    observation.Member == nameof(File.ReadAllText));
            function.Arguments.ShouldHaveSingleItem().ShouldBe("relative.txt");
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationUsesThreadWorkingDirectoryForPathGetFullPath()
        {
            string correctRoot = _env.CreateFolder().Path;
            string wrongRoot = _env.CreateFolder().Path;
            _env.SetCurrentDirectory(wrongRoot);
            _env.WithTransientTestState(new TransientThreadWorkingDirectory(correctRoot));
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(correctRoot, "path-get-full-path.proj"),
                """
                <Project>
                  <PropertyGroup>
                    <FullPath>$([System.IO.Path]::GetFullPath('sub'))</FullPath>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            string expected = Path.Combine(correctRoot, "sub");
            project.GetPropertyValue("FullPath").ShouldBe(expected);
            EvaluationExternalInputObservation observation =
                report.ShouldNotBeNull().ExternalInputs.Single(input =>
                    input.Operation == $"{typeof(Path).FullName}::{nameof(Path.GetFullPath)}");
            observation.Request.ShouldBe($"Arguments=sub\0Base={correctRoot}");
            observation.Result.ShouldBe(expected);
            report.PropertyFunctions.Single(function =>
                function.ReceiverType == typeof(Path).FullName &&
                function.Member == nameof(Path.GetFullPath))
                .Arguments.ShouldHaveSingleItem().ShouldBe("sub");
        }

        [Fact]
        public void EvaluationObservationRecordsOneCanonicalProbePerExistenceIntrinsic()
        {
            string root = _env.CreateFolder().Path;
            string filePath = _env.CreateFile(Path.Combine(root, "input.txt"), string.Empty).Path;
            string directoryPath = _env.CreateFolder(Path.Combine(root, "directory")).Path;
            _env.SetCurrentDirectory(root);
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(root, "intrinsic-probes.proj"),
                """
                <Project>
                  <PropertyGroup>
                    <FileExists>$([MSBuild]::FileExists('input.txt'))</FileExists>
                    <DirectoryExists>$([MSBuild]::DirectoryExists('directory'))</DirectoryExists>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.PathProbes.Count.ShouldBe(2);
            report.PathProbes.ShouldContain(observation =>
                observation.Kind == EvaluationPathKind.File &&
                observation.Exists &&
                FileUtilities.PathsEqual(observation.Path, filePath));
            report.PathProbes.ShouldContain(observation =>
                observation.Kind == EvaluationPathKind.Directory &&
                observation.Exists &&
                FileUtilities.PathsEqual(observation.Path, directoryPath));
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationCanonicalizesRelativeDirectoryMetadata()
        {
            string root = _env.CreateFolder().Path;
            string directoryPath = _env.CreateFolder(Path.Combine(root, "directory")).Path;
            DateTime timestamp = Directory.GetLastWriteTimeUtc(directoryPath);
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();

            session.RecordPropertyFunction(
                typeof(Directory),
                nameof(Directory.GetLastWriteTimeUtc),
                null,
                ["directory"],
                timestamp,
                pathBaseDirectory: root);

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.MetadataReads.ShouldHaveSingleItem().Path.ShouldBe(directoryPath);
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationDoesNotTreatUnrelatedPathArgumentsAsFilesystemPaths()
        {
            string root = _env.CreateFolder().Path;
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();

            session.RecordPropertyFunction(
                typeof(Path),
                "GetRelativePath",
                null,
                ["base", "target"],
                "target");
            session.RecordPropertyFunction(
                typeof(Path),
                nameof(Path.GetFullPath),
                null,
                ["child", root],
                Path.Combine(root, "child"));

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.PathProbes.ShouldBeEmpty();
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
            report.ExternalInputs.ShouldContain(observation =>
                observation.Operation == $"{typeof(Path).FullName}::{nameof(Path.GetFullPath)}" &&
                observation.Request.IndexOf($"Arguments=child|{root}", StringComparison.Ordinal) >= 0 &&
                observation.Request.EndsWith("\0Base=", StringComparison.Ordinal));
        }

        [WindowsOnlyFact]
        public void EvaluationObservationCapturesDriveRelativeEnumerationBaseAtIteration()
        {
            string firstRoot = _env.CreateFolder().Path;
            string secondRoot = _env.CreateFolder().Path;
            _env.CreateFolder(Path.Combine(firstRoot, "enum"));
            _env.CreateFolder(Path.Combine(secondRoot, "enum"));
            _env.CreateFile(Path.Combine(firstRoot, "enum", "first.txt"), string.Empty);
            string secondFile = _env.CreateFile(Path.Combine(secondRoot, "enum", "second.txt"), string.Empty).Path;
            string drive = Path.GetPathRoot(firstRoot).Substring(0, 2);
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var fileSystem = new RecordingFileSystem(FileSystems.Default, session);
            _env.SetCurrentDirectory(firstRoot);

            IEnumerable<string> entries = fileSystem.EnumerateFiles($"{drive}enum", "*.txt");
            _env.SetCurrentDirectory(secondRoot);
            entries.ShouldHaveSingleItem();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            EvaluationDirectoryEnumerationObservation enumeration =
                report.DirectoryEnumerations.ShouldHaveSingleItem();
            enumeration.Path.ShouldBe(Path.Combine(secondRoot, "enum"));
            enumeration.Entries.ShouldHaveSingleItem().ShouldBe(secondFile);
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [WindowsOnlyFact]
        public void EvaluationObservationCanonicalizesRootRelativeEnumeration()
        {
            string root = _env.CreateFolder().Path;
            string otherRoot = _env.CreateFolder().Path;
            string enumerationRoot = _env.CreateFolder(Path.Combine(root, "enum")).Path;
            string filePath = _env.CreateFile(Path.Combine(enumerationRoot, "input.txt"), string.Empty).Path;
            string rootRelativePath = enumerationRoot.Substring(2);
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var fileSystem = new RecordingFileSystem(FileSystems.Default, session);
            _env.SetCurrentDirectory(root);

            IEnumerable<string> entries = fileSystem.EnumerateFiles(rootRelativePath, "*.txt");
            _env.SetCurrentDirectory(otherRoot);
            entries.ShouldHaveSingleItem();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            EvaluationDirectoryEnumerationObservation enumeration =
                report.DirectoryEnumerations.ShouldHaveSingleItem();
            enumeration.Path.ShouldBe(enumerationRoot);
            enumeration.Entries.ShouldHaveSingleItem().ShouldBe(filePath);
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [UnixOnlyFact]
        public void EvaluationObservationCanonicalizesUnixSpecialCharacterPath()
        {
            string root = _env.CreateFolder().Path;
            string filePath = _env.CreateFile(Path.Combine(root, "a|b"), string.Empty).Path;
            _env.SetCurrentDirectory(root);
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var fileSystem = new RecordingFileSystem(FileSystems.Default, session);

            fileSystem.FileExists("a|b").ShouldBeTrue();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            report.PathProbes.ShouldHaveSingleItem().Path.ShouldBe(filePath);
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [WindowsFullFrameworkOnlyFact]
        public void EvaluationObservationCanonicalizesInvalidNonthrowingProbePath()
        {
            string root = _env.CreateFolder().Path;
            _env.SetCurrentDirectory(root);
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var fileSystem = new RecordingFileSystem(FileSystems.Default, session);

            fileSystem.FileExists("a|b").ShouldBeFalse();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            report.PathProbes.ShouldHaveSingleItem().Path.ShouldBe(
                string.Concat(root, Path.DirectorySeparatorChar, "a|b"));
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [WindowsOnlyFact]
        public void EvaluationObservationUnifiesExtendedDrivePathIdentity()
        {
            string root = _env.CreateFolder().Path;
            string inputPath = _env.CreateFile(Path.Combine(root, "input.txt"), "content").Path;
            string extendedPath = $@"\\?\{inputPath}";
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(root, "extended-path.proj"),
                $"""
                <Project>
                  <PropertyGroup>
                    <Normal>$([System.IO.File]::ReadAllText('{inputPath}'))</Normal>
                    <Extended>$([System.IO.File]::ReadAllText('{extendedPath}'))</Extended>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("Normal").ShouldBe("content");
            project.GetPropertyValue("Extended").ShouldBe("content");
            report.ShouldNotBeNull();
            report.FileReads.Count(observation =>
                observation.HashKind == EvaluationContentHashKind.DecodedText &&
                FileUtilities.PathsEqual(observation.Path, inputPath)).ShouldBe(1);
            report.FileReads.ShouldNotContain(observation =>
                observation.Path.StartsWith(@"\\?\", StringComparison.Ordinal));
            report.PropertyFunctions.Count(observation =>
                observation.ReceiverType == typeof(File).FullName &&
                observation.Member == nameof(File.ReadAllText)).ShouldBe(2);
            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [WindowsOnlyFact]
        public void EvaluationObservationNormalizesOnlyEquivalentExtendedNamespaces()
        {
            FileUtilities.NormalizePathForObservation(@"\\?\C:\root\file.txt")
                .ShouldBe(@"C:\root\file.txt");
            FileUtilities.NormalizePathForObservation(@"\\?\UNC\server\share\file.txt")
                .ShouldBe(@"\\server\share\file.txt");
            FileUtilities.NormalizePathForObservation(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\file.txt")
                .ShouldBe(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\file.txt");
            FileUtilities.NormalizePathForObservation(@"\\.\pipe\name")
                .ShouldBe(@"\\.\pipe\name");
        }

#if NET
        [Fact]
        public void EvaluationObservationRecordsEnumerationOptionsIdentity()
        {
            string sourceDirectory = _env.CreateFolder().Path;
            string inputFile = _env.CreateFile(Path.Combine(sourceDirectory, "Input.cs"), string.Empty).Path;
            var options = new EnumerationOptions
            {
                AttributesToSkip = FileAttributes.Hidden,
                BufferSize = 4096,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseSensitive,
                MatchType = MatchType.Simple,
                MaxRecursionDepth = 3,
                RecurseSubdirectories = true,
                ReturnSpecialDirectories = true,
            };
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordPropertyFunction(
                typeof(Directory),
                nameof(Directory.GetFiles),
                null,
                [sourceDirectory, "*.cs", options],
                new[] { inputFile });

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            EvaluationDirectoryEnumerationObservation observation =
                report.DirectoryEnumerations.ShouldHaveSingleItem();
            observation.SearchOption.ShouldBe(SearchOption.AllDirectories);
            observation.OptionsIdentity.ShouldContain("System.IO.EnumerationOptions");
            observation.OptionsIdentity.ShouldContain("AttributesToSkip=2");
            observation.OptionsIdentity.ShouldContain("BufferSize=4096");
            observation.OptionsIdentity.ShouldContain("IgnoreInaccessible=True");
            observation.OptionsIdentity.ShouldContain("MatchCasing=1");
            observation.OptionsIdentity.ShouldContain("MatchType=0");
            observation.OptionsIdentity.ShouldContain("MaxRecursionDepth=3");
            observation.OptionsIdentity.ShouldContain("RecurseSubdirectories=True");
            observation.OptionsIdentity.ShouldContain("ReturnSpecialDirectories=True");
            observation.Completion.ShouldBe(EvaluationEnumerationCompletion.Complete);
        }

        [Fact]
        public void EvaluationObservationRetainsDistinctEnumerationOptions()
        {
            string sourceDirectory = _env.CreateFolder().Path;
            string inputFile = _env.CreateFile(Path.Combine(sourceDirectory, "Input.cs"), string.Empty).Path;
            var caseSensitive = new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseSensitive,
            };
            var caseInsensitive = new EnumerationOptions
            {
                MatchCasing = MatchCasing.CaseInsensitive,
            };
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordPropertyFunction(
                typeof(Directory),
                nameof(Directory.GetFiles),
                null,
                [sourceDirectory, "*.cs", caseSensitive],
                new[] { inputFile });
            session.RecordPropertyFunction(
                typeof(Directory),
                nameof(Directory.GetFiles),
                null,
                [sourceDirectory, "*.cs", caseInsensitive],
                new[] { inputFile });

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.DirectoryEnumerations.Count.ShouldBe(2);
            report.DirectoryEnumerations.Select(observation => observation.OptionsIdentity)
                .Distinct(StringComparer.Ordinal)
                .Count()
                .ShouldBe(2);
        }

        [Fact]
        public void EvaluationObservationFailsClosedForUnsupportedEnumerationShape()
        {
            string sourceDirectory = _env.CreateFolder().Path;
            string firstInput = _env.CreateFile(Path.Combine(sourceDirectory, "First.cs"), string.Empty).Path;
            string secondInput = _env.CreateFile(Path.Combine(sourceDirectory, "Second.cs"), string.Empty).Path;
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
            };
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordPropertyFunction(
                typeof(Directory),
                nameof(Directory.GetFiles),
                null,
                [sourceDirectory, options, "extra"],
                new[] { firstInput });
            session.RecordPropertyFunction(
                typeof(Directory),
                nameof(Directory.GetFiles),
                null,
                [sourceDirectory, options, "different-extra"],
                new[] { secondInput });
            session.RecordPropertyFunction(
                typeof(DirectoryInfo),
                nameof(DirectoryInfo.GetFiles),
                new DirectoryInfo(sourceDirectory),
                [options, "extra"],
                new[] { new FileInfo(firstInput) });

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.DirectoryEnumerations.Count.ShouldBe(3);
            report.DirectoryEnumerations.ShouldAllBe(observation =>
                observation.SearchPattern == "*" &&
                observation.SearchOption == SearchOption.AllDirectories &&
                observation.Completion == EvaluationEnumerationCompletion.Partial &&
                observation.OptionsIdentity.Contains("UnsupportedArgumentShape", StringComparison.Ordinal));
            (report.Reasons & EvaluationObservationReason.PartialEnumeration)
                .ShouldBe(EvaluationObservationReason.PartialEnumeration);
        }
#endif

        [Fact]
        public void PropertyFunctionFailureDiagnosticRetainsOriginalEnumerationArgument()
        {
            string sourceDirectory = _env.CreateFolder().Path;
            string missingChild = Path.Combine(sourceDirectory, "missing", "child");
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(enabled: true);
            string projectFile = _env.CreateFile(
                "failed-enumeration.proj",
                $"""
                <Project>
                  <PropertyGroup>
                    <Failure>$([System.IO.Directory]::GetParent('{missingChild}').GetFiles('*.cs', 'System.IO.SearchOption.AllDirectories').Length)</Failure>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            InvalidProjectFileException exception = Should.Throw<InvalidProjectFileException>(() =>
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                }));

            exception.Message.ShouldContain("System.IO.SearchOption.AllDirectories");
        }

        [Fact]
        public void EvaluationObservationRecordsMakeRelativePathResolution()
        {
            string requestedRoot = _env.CreateFolder().Path;
            _env.SetCurrentDirectory(requestedRoot);
            string canonicalRoot = Directory.GetCurrentDirectory();
            string firstCurrentDirectory =
                _env.CreateFolder(Path.Combine(canonicalRoot, "first")).Path;
            string secondCurrentDirectory =
                _env.CreateFolder(Path.Combine(firstCurrentDirectory, "nested")).Path;
            string targetPath = Path.Combine(
                _env.CreateFolder(Path.Combine(canonicalRoot, "target")).Path,
                "target.txt");
            var reports = new List<EvaluationObservationReport>();
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                reports.Add);
            string projectFile = _env.CreateFile(
                Path.Combine(canonicalRoot, "make-relative.proj"),
                $"""
                <Project>
                  <PropertyGroup>
                    <Relative>$([MSBuild]::MakeRelative('relative-base', '{targetPath}'))</Relative>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            _env.SetCurrentDirectory(firstCurrentDirectory);
            string firstEffectiveCurrentDirectory = Directory.GetCurrentDirectory();
            Project firstProject = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });
            string firstResult = firstProject.GetPropertyValue("Relative");

            _env.SetCurrentDirectory(secondCurrentDirectory);
            string secondEffectiveCurrentDirectory = Directory.GetCurrentDirectory();
            Project secondProject = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });
            string secondResult = secondProject.GetPropertyValue("Relative");

            firstResult.ShouldNotBe(secondResult);
            reports.Count.ShouldBe(2);
            EvaluationExternalInputObservation firstResolution = reports[0].ExternalInputs.Single(
                observation => observation.Operation == "MSBuild::MakeRelative.PathResolution");
            EvaluationExternalInputObservation secondResolution = reports[1].ExternalInputs.Single(
                observation => observation.Operation == "MSBuild::MakeRelative.PathResolution");
            firstResolution.Request.ShouldBe($"First=relative-base\0Second={targetPath}");
            secondResolution.Request.ShouldBe(firstResolution.Request);
            firstResolution.Result.ShouldBe(
                $"First={Path.Combine(firstEffectiveCurrentDirectory, "relative-base")}\0Second={targetPath}");
            secondResolution.Result.ShouldBe(
                $"First={Path.Combine(secondEffectiveCurrentDirectory, "relative-base")}\0Second={targetPath}");
            reports.ShouldAllBe(report => report.PropertyFunctions.Any(observation =>
                observation.ReceiverType == typeof(IntrinsicFunctions).FullName &&
                observation.Member == "MakeRelative" &&
                observation.Effects == EvaluationPropertyFunctionEffect.Ambient));
        }

        [WindowsOnlyFact]
        public void EvaluationObservationRecordsMakeRelativeDriveRelativeResolution()
        {
            string requestedRoot = _env.CreateFolder().Path;
            _env.SetCurrentDirectory(requestedRoot);
            string effectiveCurrentDirectory = Directory.GetCurrentDirectory();
            string driveRelativeBase =
                Path.GetPathRoot(effectiveCurrentDirectory).Substring(0, 2) + "relative-base";
            string targetPath = Path.Combine(effectiveCurrentDirectory, "target.txt");
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(effectiveCurrentDirectory, "drive-relative.proj"),
                $"""
                <Project>
                  <PropertyGroup>
                    <Relative>$([MSBuild]::MakeRelative('{driveRelativeBase}', '{targetPath}'))</Relative>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            EvaluationExternalInputObservation resolution = report.ShouldNotBeNull().ExternalInputs.Single(
                observation => observation.Operation == "MSBuild::MakeRelative.PathResolution");
            resolution.Request.ShouldBe($"First={driveRelativeBase}\0Second={targetPath}");
            resolution.Result.ShouldBe(
                $"First={Path.GetFullPath(driveRelativeBase)}\0Second={targetPath}");
        }

        [Fact]
        public void MakeRelativeIgnoresPathResolutionObserverFailure()
        {
            string basePath = _env.CreateFolder().Path;
            string targetPath = Path.Combine(_env.CreateFolder().Path, "target.txt");
            string expected = IntrinsicFunctions.MakeRelative(basePath, targetPath);
            using IDisposable scope = EvaluationInputObserver.Enter(new ThrowingPathResolutionObserver());

            IntrinsicFunctions.MakeRelative(basePath, targetPath).ShouldBe(expected);
        }

        [UnixOnlyFact]
        public void MakeRelativeObservationDoesNotRequireCurrentDirectoryForRootedPaths()
        {
            string basePath = _env.CreateFolder().Path;
            string targetPath = Path.Combine(_env.CreateFolder().Path, "target.txt");
            string expected = IntrinsicFunctions.MakeRelative(basePath, targetPath);
            string originalCurrentDirectory = Directory.GetCurrentDirectory();
            string deletedCurrentDirectory = _env.CreateFolder().Path;
            Directory.SetCurrentDirectory(deletedCurrentDirectory);
            Directory.Delete(deletedCurrentDirectory);

            try
            {
                using IDisposable scope = EvaluationInputObserver.Enter(new ThrowingPathResolutionObserver());
                IntrinsicFunctions.MakeRelative(basePath, targetPath).ShouldBe(expected);
            }
            finally
            {
                Directory.SetCurrentDirectory(originalCurrentDirectory);
            }
        }

        [Fact]
        public void EvaluationObservationRecordsEnvironmentAndPropertyFunctions()
        {
            _env.SetEnvironmentVariable("OBSERVED_ENVIRONMENT_INPUT", "environment-value");
            _env.CreateFile("settings.txt", "settings-value");

            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile(
                "ambient.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Imported>$(OBSERVED_ENVIRONMENT_INPUT)</Imported>
                    <Missing>$(OBSERVED_MISSING_ENVIRONMENT_INPUT)</Missing>
                    <Live>$([System.Environment]::GetEnvironmentVariable('OBSERVED_ENVIRONMENT_INPUT'))</Live>
                    <Settings>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)settings.txt'))</Settings>
                    <Above>$([MSBuild]::GetPathOfFileAbove('settings.txt', '$(MSBuildThisFileDirectory)'))</Above>
                    <Formatted>$([System.String]::Format('{0}', 'formatted'))</Formatted>
                    <Volatile>$([System.DateTime]::utcnow)</Volatile>
                  </PropertyGroup>
                  <ItemGroup>
                    <Input Include="settings.txt" />
                    <MetadataValue Include="@(Input->'%(ModifiedTime)')" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("Imported").ShouldBe("environment-value");
            project.GetPropertyValue("Live").ShouldBe("environment-value");
            project.GetPropertyValue("Settings").ShouldBe("settings-value");
            report.ShouldNotBeNull();
            report.Environment.ShouldContain(observation =>
                observation.Name == "OBSERVED_ENVIRONMENT_INPUT" &&
                observation.Source == EvaluationEnvironmentSource.Imported &&
                observation.Value == "environment-value");
            report.Environment.ShouldContain(observation =>
                observation.Name == "OBSERVED_ENVIRONMENT_INPUT" &&
                observation.Source == EvaluationEnvironmentSource.LiveProcess &&
                observation.Value == "environment-value");
            report.Environment.ShouldContain(observation =>
                observation.Name == "OBSERVED_MISSING_ENVIRONMENT_INPUT" &&
                observation.Source == EvaluationEnvironmentSource.MissingImported &&
                !observation.Present);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(Environment).FullName &&
                observation.Member == nameof(Environment.GetEnvironmentVariable) &&
                (observation.Effects & EvaluationPropertyFunctionEffect.Environment) != 0);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(DateTime).FullName &&
                string.Equals(observation.Member, nameof(DateTime.UtcNow), StringComparison.OrdinalIgnoreCase) &&
                (observation.Effects & EvaluationPropertyFunctionEffect.Volatile) != 0);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(string).FullName &&
                observation.Member == nameof(string.Format) &&
                (observation.Effects & EvaluationPropertyFunctionEffect.Ambient) != 0);
            report.FileReads.ShouldContain(observation =>
                observation.Path.EndsWith("settings.txt", StringComparison.OrdinalIgnoreCase) &&
                observation.IsVerifiable);
            report.Searches.ShouldContain(observation =>
                observation.Kind == "GetPathOfFileAbove" &&
                observation.Candidates.Any(candidate =>
                    candidate.EndsWith("settings.txt", StringComparison.OrdinalIgnoreCase)) &&
                observation.Selected.EndsWith("settings.txt", StringComparison.OrdinalIgnoreCase));
            report.MetadataReads.ShouldContain(observation =>
                observation.Kind == EvaluationMetadataKind.ItemModifiedTime &&
                FileUtilities.PathsEqual(
                    observation.Path,
                    Path.Combine(Path.GetDirectoryName(projectFile), "settings.txt")));
            (report.Reasons & EvaluationObservationReason.UnsupportedVolatileInput)
                .ShouldBe(EvaluationObservationReason.UnsupportedVolatileInput);
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
            report.Categories.ShouldContain(observation =>
                observation.Category == EvaluationObservationCategory.PropertyFunction &&
                observation.State == EvaluationObservationCategoryState.Observed);
            report.SchemaVersion.ShouldBe(16);
            report.PropertyFunctionClassificationVersion.ShouldBeGreaterThan(0);
            report.Request.PathComparison.ShouldBe(FileUtilities.PathComparison.ToString());
        }

        [Fact]
        public void EvaluationObservationRecordsSourceTimestampUsedByMSBuildAllProjects()
        {
            var reports = new List<EvaluationObservationReport>();
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                reports.Add);
            string importFile = _env.CreateFile(
                "timestamp.props",
                "<Project><PropertyGroup><Imported>true</Imported></PropertyGroup></Project>").Path;
            string projectFile = _env.CreateFile(
                "timestamp.proj",
                "<Project><Import Project=\"timestamp.props\" /></Project>").Path;
            DateTime initialTime = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(projectFile, initialTime);
            File.SetLastWriteTimeUtc(importFile, initialTime.AddMinutes(1));
            DateTime firstProjectTime = File.GetLastWriteTimeUtc(projectFile);
            DateTime importTime = File.GetLastWriteTimeUtc(importFile);

            Project firstProject = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            firstProject.GetPropertyValue(Constants.MSBuildAllProjectsPropertyName)
                .ShouldStartWith(importFile);

            File.SetLastWriteTimeUtc(projectFile, initialTime.AddMinutes(2));
            DateTime secondProjectTime = File.GetLastWriteTimeUtc(projectFile);
            Project secondProject = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            secondProject.GetPropertyValue(Constants.MSBuildAllProjectsPropertyName)
                .ShouldStartWith(projectFile);
            reports.Count.ShouldBe(2);

            EvaluationProjectSourceObservation firstRoot = reports[0].ProjectSources.Single(
                observation => observation.Role == EvaluationProjectSourceRole.Root);
            EvaluationProjectSourceObservation firstImport = reports[0].ProjectSources.Single(
                observation => observation.Role == EvaluationProjectSourceRole.Import);
            EvaluationProjectSourceObservation secondRoot = reports[1].ProjectSources.Single(
                observation => observation.Role == EvaluationProjectSourceRole.Root);

            firstRoot.HasLastWriteTimeUtc.ShouldBeTrue();
            firstRoot.LastWriteTimeUtcTicks.ShouldBe(firstProjectTime.Ticks);
            firstImport.LastWriteTimeUtcTicks.ShouldBe(importTime.Ticks);
            secondRoot.LastWriteTimeUtcTicks.ShouldBe(secondProjectTime.Ticks);
            secondRoot.ContentHash.ShouldBe(firstRoot.ContentHash);
        }

        [Fact]
        public void EvaluationObservationMarksSourceTimestampChangeDuringReadIncomplete()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile("timestamp-race.proj", "<Project />").Path;
            DateTime initialTime = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(projectFile, initialTime);
            ProjectRootElement.TestOnlyHookAfterSourceRead =
                path => File.SetLastWriteTimeUtc(path, initialTime.AddMinutes(1));

            try
            {
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            }
            finally
            {
                ProjectRootElement.TestOnlyHookAfterSourceRead = null;
            }

            EvaluationProjectSourceObservation root = report.ProjectSources.Single(
                observation => observation.Role == EvaluationProjectSourceRole.Root);
            root.TimestampWasStableDuringRead.ShouldBeFalse();
            (report.Reasons & EvaluationObservationReason.ProjectSourceChangedDuringRead)
                .ShouldBe(EvaluationObservationReason.ProjectSourceChangedDuringRead);
            report.Categories.ShouldContain(observation =>
                observation.Category == EvaluationObservationCategory.ProjectSource &&
                observation.State == EvaluationObservationCategoryState.Incomplete);
        }

        [Fact]
        public void EvaluationObservationUsesTimestampCapturedByCachedProjectRootElement()
        {
            string projectFile = _env.CreateFile("cached-timestamp.proj", "<Project />").Path;
            DateTime initialTime = DateTime.UtcNow.AddMinutes(-10);
            File.SetLastWriteTimeUtc(projectFile, initialTime);
            DateTime capturedTime = File.GetLastWriteTimeUtc(projectFile);
            ProjectRootElement root = ProjectRootElement.Open(projectFile);
            File.SetLastWriteTimeUtc(projectFile, initialTime.AddMinutes(1));

            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordProjectSource(root, EvaluationProjectSourceRole.Root);
            EvaluationProjectSourceObservation source =
                session.Complete(evaluationSucceeded: true).ProjectSources.ShouldHaveSingleItem();

            source.HasLastWriteTimeUtc.ShouldBeTrue();
            source.LastWriteTimeUtcTicks.ShouldBe(capturedTime.Ticks);
            source.LastWriteTimeUtcTicks.ShouldNotBe(File.GetLastWriteTimeUtc(projectFile).Ticks);
        }

        [Fact]
        public void EvaluationObservationInvalidatesDiskSourceHashAfterInMemoryMutation()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile("mutated-source.proj", "<Project />").Path;
            ProjectRootElement root = ProjectRootElement.Open(projectFile);
            root.AddProperty("Mutated", "true");

            Project.FromProjectRootElement(root, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            EvaluationProjectSourceObservation source = report.ProjectSources.Single(
                observation => observation.Role == EvaluationProjectSourceRole.Root);
            source.ContentHash.ShouldBe(EvaluationObservationSession.ComputeTextHash(root.RawXml));
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, projectFile) &&
                observation.HashKind == EvaluationContentHashKind.ParsedXml &&
                !observation.IsVerifiable);
            (report.Reasons & EvaluationObservationReason.UnversionedProjectRootElementCache)
                .ShouldBe(EvaluationObservationReason.UnversionedProjectRootElementCache);
        }

        [Fact]
        public void EvaluationObservationInvalidatesSourceHashAfterFailedReload()
        {
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(enabled: true);

            string projectFile = _env.CreateFile("failed-reload-source.proj", "<Project />").Path;
            ProjectRootElement root = ProjectRootElement.Open(projectFile);
            string originalHash = root.EvaluationObservationSourceHash;
            int originalVersion = root.Version;

            File.WriteAllText(projectFile, "<Project><Target /></Project>");

            Should.Throw<InvalidProjectFileException>(
                () => root.Reload(throwIfUnsavedChanges: false));

            root.Version.ShouldBe(originalVersion);
            originalHash.ShouldNotBeNull();
            root.EvaluationObservationSourceHash.ShouldBeNull();
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordProjectSource(root, EvaluationProjectSourceRole.Root);
            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            report.ProjectSources.ShouldHaveSingleItem().HashKind
                .ShouldBe(EvaluationContentHashKind.ParsedXml);
            (report.Reasons & EvaluationObservationReason.ParsedProjectSourceOnly)
                .ShouldBe(EvaluationObservationReason.ParsedProjectSourceOnly);
        }

        [Fact]
        public void EvaluationObservationInvalidatesRawSourceHashAfterSave()
        {
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(enabled: true);
            string projectFile = _env.CreateFile("saved-source.proj", "<Project />").Path;
            ProjectRootElement root = ProjectRootElement.Open(projectFile);
            root.EvaluationObservationSourceHash.ShouldNotBeNull();

            root.Save(Encoding.UTF32);

            root.EvaluationObservationSourceHash.ShouldBeNull();
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordProjectSource(root, EvaluationProjectSourceRole.Root);
            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            EvaluationProjectSourceObservation source = report.ProjectSources.ShouldHaveSingleItem();
            source.HashKind.ShouldBe(EvaluationContentHashKind.ParsedXml);
            source.HasLastWriteTimeUtc.ShouldBeTrue();
            (report.Reasons & EvaluationObservationReason.ParsedProjectSourceOnly)
                .ShouldBe(EvaluationObservationReason.ParsedProjectSourceOnly);
        }

        [Fact]
        public void EvaluationObservationUsesLinkedProjectVersionAsAuthoritativeIdentity()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string projectFile = Path.Combine(_env.DefaultTestDirectory.Path, "linked.proj");
            var root = new ProjectRootElement(new FakeProjectRootElementLink(projectFile));

            session.RecordProjectSource(root, EvaluationProjectSourceRole.Root);
            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            EvaluationProjectSourceObservation source = report.ProjectSources.ShouldHaveSingleItem();
            source.Version.ShouldBe(7);
            source.ContentHash.ShouldBeNull();
            source.Provider.ShouldContain(nameof(FakeProjectRootElementLink));
            source.HasLastWriteTimeUtc.ShouldBeFalse();
            source.TimestampWasStableDuringRead.ShouldBeTrue();
            (report.Reasons & EvaluationObservationReason.ParsedProjectSourceOnly)
                .ShouldBe(EvaluationObservationReason.None);
            (report.Reasons & EvaluationObservationReason.UnversionedProjectRootElementCache)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationMarksXmlReaderSourceWithoutHostIdentityIncomplete()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            using var reader = XmlReader.Create(new StringReader("<Project />"));
            ProjectRootElement root = ProjectRootElement.Create(
                reader,
                _env.CreateProjectCollection().Collection);

            session.RecordProjectSource(root, EvaluationProjectSourceRole.Root);
            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.ProjectSources.ShouldHaveSingleItem().Provider.ShouldBe("XmlReader");
            report.ProjectSources.ShouldHaveSingleItem().HasLastWriteTimeUtc.ShouldBeFalse();
            (report.Reasons & EvaluationObservationReason.UnversionedSourceProvider)
                .ShouldBe(EvaluationObservationReason.UnversionedSourceProvider);
        }

        [Fact]
        public void EvaluationObservationMarksUnrestrictedFileSystemSideEffectsUnsupported()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            TransientTestFolder projectDirectory = _env.CreateFolder(
                Path.Combine(_env.DefaultTestDirectory.Path, "side-effect-project"));
            string projectFile = _env.CreateFile(
                projectDirectory,
                "side-effect.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Created>$([System.IO.Directory]::GetParent('$(MSBuildThisFileDirectory)').CreateSubdirectory('side-effect-created'))</Created>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            Directory.Exists(project.GetPropertyValue("Created")).ShouldBeTrue();
            report.ShouldNotBeNull();
            report.PropertyFunctions.ShouldContain(observation =>
                observation.Member == "CreateSubdirectory" &&
                (observation.Effects & EvaluationPropertyFunctionEffect.SideEffect) != 0 &&
                (observation.Effects & EvaluationPropertyFunctionEffect.OpaqueUnsupported) != 0);
            (report.Reasons & EvaluationObservationReason.EvaluationSideEffect)
                .ShouldBe(EvaluationObservationReason.EvaluationSideEffect);
            report.Categories.ShouldContain(observation =>
                observation.Category == EvaluationObservationCategory.VolatileOrSideEffect &&
                observation.State == EvaluationObservationCategoryState.Unsupported);
        }

        [Fact]
        public void EvaluationObservationMarksEnableAllPropertyFunctionsUnsupported()
        {
            _env.WithTransientTestState(
                new TransientAppContextSwitch("Microsoft.Build.EnableAllPropertyFunctions", value: true));

            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile("enable-all.proj", "<Project />").Path;
            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            (report.Reasons & EvaluationObservationReason.AllPropertyFunctionsEnabled)
                .ShouldBe(EvaluationObservationReason.AllPropertyFunctionsEnabled);
            report.Categories.ShouldContain(observation =>
                observation.Category == EvaluationObservationCategory.PropertyFunction &&
                observation.State == EvaluationObservationCategoryState.Unsupported);
        }

#if NET
        [Fact]
        public void EvaluationObservationFailsClosedForUnclassifiedKnownTypeMember()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile(
                "unclassified-property-function.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Relative>$([System.IO.Path]::GetRelativePath('a', 'b'))</Relative>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.PropertyFunctions.ShouldContain(observation =>
                observation.ReceiverType == typeof(Path).FullName &&
                observation.Member == nameof(Path.GetRelativePath) &&
                (observation.Effects & EvaluationPropertyFunctionEffect.OpaqueUnsupported) != 0);
            (report.Reasons & EvaluationObservationReason.UnclassifiedPropertyFunction)
                .ShouldBe(EvaluationObservationReason.UnclassifiedPropertyFunction);
        }
#endif

        [Fact]
        public void EvaluationObservationClassifiesFileReadAndEnumerationFamilies()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string root = _env.DefaultTestDirectory.Path;
            string file = Path.Combine(root, "input.txt");

            session.RecordPropertyFunction(
                typeof(File),
                "ReadAllLines",
                instance: null,
                arguments: [file],
                result: new[] { "line" });
            session.RecordPropertyFunction(
                typeof(Directory),
                "GetFileSystemEntries",
                instance: null,
                arguments: [root],
                result: new[] { file });

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.PropertyFunctions.ShouldContain(observation =>
                observation.Member == "ReadAllLines" &&
                (observation.Effects & EvaluationPropertyFunctionEffect.FileContent) != 0);
            report.PropertyFunctions.ShouldContain(observation =>
                observation.Member == "GetFileSystemEntries" &&
                (observation.Effects & EvaluationPropertyFunctionEffect.DirectoryEnumeration) != 0);
            report.FileReads.ShouldContain(observation =>
                observation.Path == file &&
                observation.HashKind == EvaluationContentHashKind.DecodedTextSequence);
            report.DirectoryEnumerations.ShouldContain(observation =>
                observation.Path == root &&
                observation.Kind == EvaluationEnumerationKind.FilesAndDirectories);
        }

        [Fact]
        public void EvaluationObservationDoesNotFabricateTypedRecordsForFailedFunctions()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string file = Path.Combine(_env.DefaultTestDirectory.Path, "missing.txt");

            session.RecordPropertyFunction(
                typeof(File),
                nameof(File.ReadAllText),
                instance: null,
                arguments: [file],
                result: null,
                succeeded: false);

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: false);

            EvaluationPropertyFunctionObservation function = report.PropertyFunctions.ShouldHaveSingleItem();
            function.Succeeded.ShouldBeFalse();
            function.Result.ShouldBe("<failed>");
            report.FileReads.ShouldBeEmpty();
        }

        [Fact]
        public void EvaluationObservationRecordsTypedPropertyFunctionFailure()
        {
            string root = _env.CreateFolder().Path;
            string missingPath = Path.Combine(root, "missing.txt");
            _env.SetCurrentDirectory(root);
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            string projectFile = _env.CreateFile(
                Path.Combine(root, "failed-read.proj"),
                """
                <Project>
                  <PropertyGroup>
                    <Missing>$([System.IO.File]::ReadAllText('missing.txt'))</Missing>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Should.Throw<InvalidProjectFileException>(() =>
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                }));

            report.ShouldNotBeNull();
            EvaluationOperationFailureObservation failure =
                report.OperationFailures.ShouldHaveSingleItem();
            failure.Category.ShouldBe(EvaluationObservationCategory.FileContent);
            failure.Operation.ShouldBe($"{typeof(File).FullName}::{nameof(File.ReadAllText)}");
            failure.Path.ShouldBe(missingPath);
            failure.Provider.ShouldBe(FileSystems.Default.GetType().AssemblyQualifiedName);
            failure.ExceptionType.ShouldBe(typeof(FileNotFoundException).FullName);
            failure.HResult.ShouldNotBe(0);
            failure.Message.ShouldNotBeNullOrEmpty();
            report.FileReads.ShouldNotContain(observation =>
                FileUtilities.PathsEqual(observation.Path, missingPath));
            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.FileContent)
                .State.ShouldBe(EvaluationObservationCategoryState.Incomplete);
        }

        [Fact]
        public void EvaluationObservationDoesNotInventPathForNonFilesystemPropertyFunctionFailure()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();

            session.RecordPropertyFunctionFailure(
                typeof(IntrinsicFunctions),
                "DoesTaskHostExist",
                instance: null,
                ["bogus-runtime", "x86"],
                pathBaseDirectory: null,
                new ArgumentException("Invalid runtime."));

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: false);

            EvaluationOperationFailureObservation failure =
                report.OperationFailures.ShouldHaveSingleItem();
            failure.Category.ShouldBe(EvaluationObservationCategory.PropertyFunction);
            failure.Path.ShouldBeNull();
            failure.Provider.ShouldBeNull();
            (report.Reasons & EvaluationObservationReason.UnrootedPath)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationUsesFileSystemInfoInstanceForFailurePath()
        {
            string missingPath = Path.Combine(_env.DefaultTestDirectory.Path, "missing-info.txt");
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();

            session.RecordPropertyFunctionFailure(
                typeof(FileInfo),
                nameof(FileInfo.Length),
                new FileInfo(missingPath),
                arguments: [],
                pathBaseDirectory: null,
                new FileNotFoundException("Missing file.", missingPath));

            EvaluationOperationFailureObservation failure =
                session.Complete(evaluationSucceeded: false).OperationFailures.ShouldHaveSingleItem();

            failure.Category.ShouldBe(EvaluationObservationCategory.FileMetadata);
            failure.Path.ShouldBe(missingPath);
            failure.Provider.ShouldBe(FileSystems.Default.GetType().AssemblyQualifiedName);
        }

        [Fact]
        public void EvaluationObservationFailureRecordingCannotReplaceEvaluationFailure()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();

            Should.NotThrow(() =>
                session.RecordPropertyFunctionFailure(
                    typeof(File),
                    nameof(File.ReadAllText),
                    instance: null,
                    [new ThrowingStringValue()],
                    _env.DefaultTestDirectory.Path,
                    new IOException("Original evaluation failure.")));

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: false);

            report.OperationFailures.ShouldBeEmpty();
            (report.Reasons & EvaluationObservationReason.ObservationIncomplete)
                .ShouldBe(EvaluationObservationReason.ObservationIncomplete);
            (report.Reasons & EvaluationObservationReason.ExternalOperationFailure)
                .ShouldBe(EvaluationObservationReason.ExternalOperationFailure);
        }

        [Fact]
        public void EvaluationObservationRecordsParserConfigurationInputs()
        {
            string parserConfig = _env.CreateFile(
                "Directory.Parse.config",
                """
                <ParseConfig />
                """).Path;
            _env.SetEnvironmentVariable(ParserIgnoreConfiguration.EnvironmentVariableName, parserConfig);

            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile("parser.proj", "<Project />").Path;
            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.Environment.ShouldContain(observation =>
                observation.Name == ParserIgnoreConfiguration.EnvironmentVariableName &&
                observation.Value == parserConfig);
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, parserConfig) &&
                observation.IsVerifiable &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.ContentHash == EvaluationObservationSession.ComputeBytesHash(
                    File.ReadAllBytes(parserConfig)));
            report.PathProbes.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, parserConfig) &&
                observation.Kind == EvaluationPathKind.File &&
                observation.Exists);
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.ParserConfiguration &&
                observation.Operation == "ParseOutcome" &&
                FileUtilities.PathsEqual(observation.Request, parserConfig) &&
                observation.Result == "ParsedParseConfig");
        }

        [Fact]
        public void EvaluationObservationRecordsMalformedParserConfigurationBytesAndOutcome()
        {
            string parserConfig = _env.CreateFile("Directory.Parse.config", string.Empty).Path;
            byte[] malformedBytes =
            [
                .. Encoding.Unicode.GetPreamble(),
                .. Encoding.Unicode.GetBytes("<ParseConfig><IgnoreAttributes></ParseConfig>trailing"),
            ];
            File.WriteAllBytes(parserConfig, malformedBytes);
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(malformedBytes);
            _env.SetEnvironmentVariable(ParserIgnoreConfiguration.EnvironmentVariableName, parserConfig);

            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile("malformed-parser.proj", "<Project />").Path;
            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, parserConfig) &&
                observation.IsVerifiable &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.ContentHash == expectedHash);
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.ParserConfiguration &&
                observation.Operation == "ParseOutcome" &&
                FileUtilities.PathsEqual(observation.Request, parserConfig) &&
                observation.Result == $"MalformedXml:{typeof(XmlException).FullName}");
            (report.Reasons & EvaluationObservationReason.ExternalOperationFailure)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationRecordsUnexpectedParserConfigurationRoot()
        {
            string parserConfig = _env.CreateFile(
                "Directory.Parse.config",
                "<UnexpectedRoot />").Path;
            string expectedHash = EvaluationObservationSession.ComputeBytesHash(
                File.ReadAllBytes(parserConfig));
            _env.SetEnvironmentVariable(ParserIgnoreConfiguration.EnvironmentVariableName, parserConfig);

            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile("unexpected-parser-root.proj", "<Project />").Path;
            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            report.ShouldNotBeNull();
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, parserConfig) &&
                observation.ContentHash == expectedHash);
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.ParserConfiguration &&
                observation.Operation == "ParseOutcome" &&
                FileUtilities.PathsEqual(observation.Request, parserConfig) &&
                observation.Result == "ParsedUnexpectedRoot");
        }

        [Fact]
        public void EvaluationObservationFailsClosedWhenParserConfigurationReadFails()
        {
            string parserConfig = _env.CreateFile(
                "Directory.Parse.config",
                "<ParseConfig />").Path;
            _env.SetEnvironmentVariable(ParserIgnoreConfiguration.EnvironmentVariableName, parserConfig);
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);
            ParserIgnoreConfiguration.TestOnlyHookBeforeConfigRead = path =>
            {
                if (FileUtilities.PathsEqual(path, parserConfig))
                {
                    throw new IOException("Test-only parser configuration read failure.");
                }
            };

            try
            {
                string projectFile = _env.CreateFile("failed-parser-read.proj", "<Project />").Path;
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            }
            finally
            {
                ParserIgnoreConfiguration.TestOnlyHookBeforeConfigRead = null;
            }

            report.ShouldNotBeNull();
            report.FileReads.ShouldNotContain(observation =>
                FileUtilities.PathsEqual(observation.Path, parserConfig));
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.ParserConfiguration &&
                observation.Operation == "LoadFailure" &&
                FileUtilities.PathsEqual(observation.Request, parserConfig) &&
                observation.Result == typeof(IOException).FullName);
            EvaluationOperationFailureObservation failure =
                report.OperationFailures.ShouldHaveSingleItem();
            failure.Category.ShouldBe(EvaluationObservationCategory.FileContent);
            failure.Operation.ShouldBe("ParserIgnoreConfiguration.Load");
            failure.Path.ShouldBe(parserConfig);
            failure.Provider.ShouldBe(FileSystems.Default.GetType().AssemblyQualifiedName);
            failure.ExceptionType.ShouldBe(typeof(IOException).FullName);
            failure.HResult.ShouldBe(new IOException().HResult);
            failure.Message.ShouldBe("Test-only parser configuration read failure.");
            (report.Reasons & EvaluationObservationReason.ExternalOperationFailure)
                .ShouldBe(EvaluationObservationReason.ExternalOperationFailure);
        }

        [Fact]
        public void EvaluationObservationRecordsDisabledParserConfigurationRegime()
        {
            TransientTestState environmentVariable =
                _env.SetEnvironmentVariable("MSBUILD_DISABLE_PARSE_CONFIG", "1");
            Traits.UpdateFromEnvironment();
            try
            {
                EvaluationObservationReport report = null;
                using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                    enabled: true,
                    createdReport => report = createdReport);

                string projectFile = _env.CreateFile("parser-disabled.proj", "<Project />").Path;
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });

                report.ShouldNotBeNull();
                report.Request.DisableParseConfig.ShouldBeTrue();
                (report.Reasons & EvaluationObservationReason.ParserConfigurationProvenanceUnavailable)
                    .ShouldBe(EvaluationObservationReason.None);
            }
            finally
            {
                environmentVariable.Revert();
                Traits.UpdateFromEnvironment();
            }
        }

        [Fact]
        public void EvaluationObservationReadsEngineEnvironmentInputsPerEvaluation()
        {
            const string EnvironmentName = "MSBUILDINCLUDEDEFAULTSDKRESOLVER";
            var reports = new List<EvaluationObservationReport>();
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                reports.Add);
            TransientTestState environment = _env.SetEnvironmentVariable(EnvironmentName, "first");
            string projectFile = _env.CreateFile("engine-environment.proj", "<Project />").Path;

            try
            {
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });

                Environment.SetEnvironmentVariable(EnvironmentName, "second");
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            }
            finally
            {
                environment.Revert();
            }

            reports.Count.ShouldBe(2);
            reports[0].Environment.ShouldContain(observation =>
                observation.Name == EnvironmentName &&
                observation.Value == "first");
            reports[1].Environment.ShouldContain(observation =>
                observation.Name == EnvironmentName &&
                observation.Value == "second");
        }

        [WindowsOnlyFact]
        public void EvaluationObservationRecordsRegistryFunctions()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile(
                "registry.proj",
                """
                <Project>
                  <PropertyGroup>
                    <RegistryValue>$([MSBuild]::GetRegistryValue('HKEY_CURRENT_USER\Software\MSBuildObservationMissing', 'Value', 'fallback'))</RegistryValue>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
            });

            project.GetPropertyValue("RegistryValue").ShouldBeEmpty();
            report.ShouldNotBeNull();
            report.ExternalInputs.ShouldContain(observation =>
                observation.Kind == EvaluationExternalInputKind.Registry &&
                observation.Operation == "GetRegistryValue" &&
                string.IsNullOrEmpty(observation.Result));
        }

        [Fact]
        public void EvaluationObservationRecordsBuildCheckThroughEvaluationFileSystem()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            TransientTestFile assemblyFile = _env.CreateFile(
                "observed-build-check.dll",
                "assembly-content");
            string projectFile = _env.CreateFile(
                "observed-build-check.proj",
                $"""
                <Project>
                  <PropertyGroup>
                    <Registered>$([MSBuild]::RegisterBuildCheck('{assemblyFile.Path}'))</Registered>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;
            var fileSystem = new Helpers.LoggingFileSystem();

            Project project = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
                EvaluationContext = EvaluationContext.Create(
                    EvaluationContext.SharingPolicy.Shared,
                    fileSystem),
            });

            project.GetPropertyValue("Registered").ShouldBe(Boolean.TrueString);
            report.ShouldNotBeNull();
            fileSystem.ExistenceChecks[assemblyFile.Path].ShouldBe(1);
            report.PathProbes.Count(observation =>
                FileUtilities.PathsEqual(observation.Path, assemblyFile.Path) &&
                observation.Kind == EvaluationPathKind.File &&
                observation.Exists &&
                observation.Provider.Contains(nameof(Helpers.LoggingFileSystem))).ShouldBe(1);
            report.FileReads.Count(observation =>
                FileUtilities.PathsEqual(observation.Path, assemblyFile.Path) &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.IsVerifiable &&
                observation.Provider.Contains(nameof(Helpers.LoggingFileSystem))).ShouldBe(1);
            report.SideEffects.ShouldContain(observation =>
                observation.Kind == "RegisterBuildCheck" &&
                FileUtilities.PathsEqual(observation.Identity, assemblyFile.Path));
        }

        [Fact]
        public void EvaluationObservationRecordsSdkResolverAndUsingTask()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile(
                "sdk-and-task.proj",
                """
                <Project Sdk="foo">
                  <UsingTask TaskName="ObservedTask" AssemblyFile="observed-task.dll" />
                </Project>
                """.Cleanup()).Path;

            EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Isolated);
            SetResolverForContext(context, _resolver);
            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
                EvaluationContext = context,
                LoadSettings = ProjectLoadSettings.IgnoreMissingImports,
            });

            report.ShouldNotBeNull();
            report.SdkResolutions.Count(observation =>
                observation.SdkName == "foo" &&
                !observation.FromCache).ShouldBe(1);
            report.SdkResolutions.ShouldAllBe(observation => observation.Success);
            report.TaskRegistrations.ShouldContain(observation =>
                observation.TaskName == "ObservedTask" &&
                observation.AssemblyFile.EndsWith("observed-task.dll", StringComparison.OrdinalIgnoreCase));
            report.Categories.ShouldContain(observation =>
                observation.Category == EvaluationObservationCategory.SdkResolution &&
                observation.State == EvaluationObservationCategoryState.Observed);
        }

        [Fact]
        public void EvaluationObservationMarksHostDirectoryCacheAsUnversioned()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile(
                "directory-cache.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('directory-cache.marker')">
                    <Observed>true</Observed>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
                DirectoryCacheFactory = new Helpers.LoggingDirectoryCacheFactory(),
            });

            report.ShouldNotBeNull();
            (report.Reasons & EvaluationObservationReason.UnversionedDirectoryCache)
                .ShouldBe(EvaluationObservationReason.UnversionedDirectoryCache);
        }

        [Fact]
        public void EvaluationObservationRecordsCustomFileSystemProvider()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile(
                "custom-filesystem.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('custom.marker')" />
                </Project>
                """.Cleanup()).Path;
            var fileSystem = new Helpers.LoggingFileSystem();

            Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = _env.CreateProjectCollection().Collection,
                EvaluationContext = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared, fileSystem),
            });

            report.ShouldNotBeNull();
            report.Request.FileSystemProvider
                .IndexOf(nameof(Helpers.LoggingFileSystem), StringComparison.Ordinal)
                .ShouldBeGreaterThanOrEqualTo(0);
            report.PathProbes.ShouldContain(observation =>
                observation.Provider.IndexOf(nameof(Helpers.LoggingFileSystem), StringComparison.Ordinal) >= 0);
            (report.Reasons & EvaluationObservationReason.UnversionedCustomProvider)
                .ShouldBe(EvaluationObservationReason.UnversionedCustomProvider);
        }

        [Fact]
        public void EvaluationObservationRecordsSdkResultOnCacheHit()
        {
            var reports = new List<EvaluationObservationReport>();
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                reports.Add);

            EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.SharedSDKCache);
            SetResolverForContext(context, _resolver);
            string firstProject = _env.CreateFile("sdk-cache-first.proj", "<Project Sdk=\"foo\" />").Path;
            string secondProject = _env.CreateFile("sdk-cache-second.proj", "<Project Sdk=\"foo\" />").Path;

            foreach (string projectFile in new[] { firstProject, secondProject })
            {
                Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                    EvaluationContext = context,
                    LoadSettings = ProjectLoadSettings.IgnoreMissingImports,
                });
            }

            reports.Count.ShouldBe(2);
            reports[0].SdkResolutions.Count(observation =>
                observation.SdkName == "foo" &&
                !observation.FromCache).ShouldBe(1);
            EvaluationSdkResolutionObservation cacheMiss =
                reports[0].SdkResolutions.Single(observation => !observation.FromCache);
            EvaluationObservationReport cacheHitReport = reports[1];
            EvaluationSdkResolutionObservation cacheHit =
                cacheHitReport.SdkResolutions.First(observation =>
                observation.FromCache &&
                observation.SdkName == "foo" &&
                observation.Success);
            cacheHit.CacheIdentity.OwnerId.ShouldBe(cacheMiss.CacheIdentity.OwnerId);
            cacheHit.CacheIdentity.Epoch.ShouldBe(cacheMiss.CacheIdentity.Epoch);
            cacheHit.CacheIdentity.EntryId.ShouldBe(cacheMiss.CacheIdentity.EntryId);
            reports[0].SdkResolutions.ShouldAllBe(observation =>
                observation.CacheIdentity.EntryId == cacheMiss.CacheIdentity.EntryId);
            cacheHitReport.SdkResolutions.ShouldAllBe(observation =>
                observation.CacheIdentity.EntryId == cacheMiss.CacheIdentity.EntryId);
            cacheHit.ProjectPath.ShouldBe(secondProject);
            cacheMiss.ProjectPath.ShouldBe(firstProject);
            ((ISdkResolverCacheValidator)context.SdkResolverService)
                .IsCacheEntryCurrent(cacheHit.CacheIdentity)
                .ShouldBeTrue();
        }

        [Fact]
        public void EvaluationObservationMarksPartialEvaluationAsIncomplete()
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport);

            string projectFile = _env.CreateFile("partial.proj", "<Project />").Path;

            ProjectInstance.FromProjectRootElement(
                ProjectRootElement.Open(projectFile),
                new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                    EvaluationStage = ProjectEvaluationStage.Properties,
                });

            report.ShouldNotBeNull();
            (report.Reasons & EvaluationObservationReason.IncompleteEvaluationStage)
                .ShouldBe(EvaluationObservationReason.IncompleteEvaluationStage);
        }

        [Fact]
        public void EvaluationObservationCallbackFailureIsReportedAfterEvaluation()
        {
            string projectFile = _env.CreateFile(
                "callback.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Observed>true</Observed>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;
            Project project = null;

            InvalidOperationException exception = Should.Throw<InvalidOperationException>(() =>
            {
                using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                    enabled: true,
                    _ => throw new ApplicationException("Callback failed."));

                project = Project.FromFile(projectFile, new ProjectOptions
                {
                    ProjectCollection = _env.CreateProjectCollection().Collection,
                });
            });

            exception.InnerException.ShouldBeOfType<ApplicationException>();
            project.ShouldNotBeNull();
            project.GetPropertyValue("Observed").ShouldBe("true");
        }

        [Fact]
        public async Task SharedEvaluationContextProducesDisjointObservationReports()
        {
            var reports = new ConcurrentBag<EvaluationObservationReport>();
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                reports.Add);

            string firstProject = _env.CreateFile(
                "first.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('first.marker')" />
                </Project>
                """.Cleanup()).Path;
            string secondProject = _env.CreateFile(
                "second.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('second.marker')" />
                </Project>
                """.Cleanup()).Path;

            EvaluationContext context = EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);
            ProjectCollection firstCollection = _env.CreateProjectCollection().Collection;
            ProjectCollection secondCollection = _env.CreateProjectCollection().Collection;

            await Task.WhenAll(
                Task.Run(() => Project.FromFile(firstProject, new ProjectOptions
                {
                    ProjectCollection = firstCollection,
                    EvaluationContext = context,
                })),
                Task.Run(() => Project.FromFile(secondProject, new ProjectOptions
                {
                    ProjectCollection = secondCollection,
                    EvaluationContext = context,
                })));

            reports.Count.ShouldBe(2);

            string firstMarker = Path.Combine(_env.DefaultTestDirectory.Path, "first.marker");
            string secondMarker = Path.Combine(_env.DefaultTestDirectory.Path, "second.marker");

            reports.Count(report =>
                FileUtilities.PathsEqual(report.ProjectPath, firstProject) &&
                report.PathProbes.Any(observation => FileUtilities.PathsEqual(observation.Path, firstMarker)) &&
                !report.PathProbes.Any(observation => FileUtilities.PathsEqual(observation.Path, secondMarker)))
                .ShouldBe(1);
            reports.Count(report =>
                FileUtilities.PathsEqual(report.ProjectPath, secondProject) &&
                report.PathProbes.Any(observation => FileUtilities.PathsEqual(observation.Path, secondMarker)) &&
                !report.PathProbes.Any(observation => FileUtilities.PathsEqual(observation.Path, firstMarker)))
                .ShouldBe(1);

            reports.ShouldAllBe(report =>
                (report.Reasons & EvaluationObservationReason.UnversionedSharedCache) != 0);
        }

        [Fact]
        public void RecordingFileSystemPreservesPartialEnumeration()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var innerFileSystem = new PartialEnumerationFileSystem();
            var recordingFileSystem = new RecordingFileSystem(innerFileSystem, session);

            using (IEnumerator<string> enumerator = recordingFileSystem.EnumerateFiles("root").GetEnumerator())
            {
                enumerator.MoveNext().ShouldBeTrue();
                enumerator.Current.ShouldBe("first.cs");
            }

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            innerFileSystem.EntriesProduced.ShouldBe(1);
            report.DirectoryEnumerations.ShouldHaveSingleItem()
                .Completion.ShouldBe(EvaluationEnumerationCompletion.Partial);
            report.DirectoryEnumerations.Single().Entries.ShouldBe(
                [Path.Combine(Directory.GetCurrentDirectory(), "first.cs")]);
            (report.Reasons & EvaluationObservationReason.PartialEnumeration)
                .ShouldBe(EvaluationObservationReason.PartialEnumeration);
        }

        [Fact]
        public void EvaluationObservationReportOwnsStableCollectionsAfterCompletion()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string root = _env.DefaultTestDirectory.Path;
            session.RecordRequest(new EvaluationRequestObservation { ProjectPath = "before" });
            session.RecordProbe(Path.Combine(root, "probe"), EvaluationPathKind.File, exists: true);
            session.RecordMetadata(
                Path.Combine(root, "metadata"),
                EvaluationMetadataKind.LastWriteTimeUtc,
                value: 1);
            session.RecordFileRead(
                Path.Combine(root, "content"),
                "hash",
                isVerifiable: true,
                EvaluationContentHashKind.RawBytes);
            session.RecordEnvironment(
                "before",
                EvaluationEnvironmentSource.Imported,
                present: true,
                value: "value");
            session.RecordEnumeration(
                Path.Combine(root, "enumeration"),
                "*.cs",
                SearchOption.TopDirectoryOnly,
                EvaluationEnumerationKind.Files,
                [Path.Combine(root, "before.cs")],
                EvaluationEnumerationCompletion.Complete);
            session.RecordOperationFailure(
                EvaluationObservationCategory.FileContent,
                "before-operation",
                Path.Combine(root, "failure"),
                provider: "test-provider",
                exception: new IOException("before"));
            var recordingFileSystem = new RecordingFileSystem(new PartialEnumerationFileSystem(), session);
            IEnumerator<string> lateEnumerator = recordingFileSystem.EnumerateFiles("late-enumeration").GetEnumerator();
            lateEnumerator.MoveNext().ShouldBeTrue();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            session.TestOnlyRetainedObservationCount.ShouldBe(0);
            session.TestOnlyObservationCollectionsDetached.ShouldBeTrue();
            lateEnumerator.Dispose();
            session.RecordRequest(new EvaluationRequestObservation { ProjectPath = "late" });
            session.RecordProbe("late", EvaluationPathKind.File, exists: false);
            session.RecordMetadata("late", EvaluationMetadataKind.LastWriteTimeUtc, value: 2);
            session.RecordFileRead(
                "late",
                "late-hash",
                isVerifiable: true,
                EvaluationContentHashKind.RawBytes);
            session.RecordEnumeration(
                "late",
                "*.cs",
                SearchOption.TopDirectoryOnly,
                EvaluationEnumerationKind.Files,
                ["late.cs"],
                EvaluationEnumerationCompletion.Complete);
            session.RecordEnvironment(
                "late",
                EvaluationEnvironmentSource.Imported,
                present: true,
                value: "late-value");
            session.RecordOperationFailure(
                EvaluationObservationCategory.FileContent,
                "late-operation",
                Path.Combine(root, "late-failure"),
                provider: "test-provider",
                exception: new IOException("late"));

            report.Request.ProjectPath.ShouldBe("before");
            report.PathProbes.ShouldHaveSingleItem().Path.ShouldEndWith("probe");
            report.MetadataReads.ShouldHaveSingleItem().Path.ShouldEndWith("metadata");
            report.FileReads.ShouldHaveSingleItem().Path.ShouldEndWith("content");
            report.DirectoryEnumerations.ShouldHaveSingleItem().Path.ShouldEndWith("enumeration");
            report.Environment.ShouldHaveSingleItem().Name.ShouldBe("before");
            report.OperationFailures.ShouldHaveSingleItem().Operation.ShouldBe("before-operation");
            (session.TestOnlyReasons & EvaluationObservationReason.ObservationIncomplete)
                .ShouldBe(EvaluationObservationReason.None);
            session.TestOnlyRetainedObservationCount.ShouldBe(0);
            session.TestOnlyObservationCollectionsDetached.ShouldBeTrue();
        }

        [Fact]
        public void RecordingFileSystemDoesNotRetainEnumerationAfterCompletion()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var recordingFileSystem = new RecordingFileSystem(new PartialEnumerationFileSystem(), session);
            IEnumerator<string> enumerator = recordingFileSystem.EnumerateFiles("root").GetEnumerator();

            enumerator.MoveNext().ShouldBeTrue();
            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);
            enumerator.Dispose();

            report.DirectoryEnumerations.ShouldBeEmpty();
            session.TestOnlyRetainedObservationCount.ShouldBe(0);
        }

        [Fact]
        public void RecordingFileSystemMarksConflictingProbeResults()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var recordingFileSystem = new RecordingFileSystem(new AlternatingProbeFileSystem(), session);

            recordingFileSystem.FileExists("probe").ShouldBeFalse();
            recordingFileSystem.FileExists("probe").ShouldBeTrue();

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.ConflictingObservation);
        }

        [Fact]
        public void RecordingFileSystemRecordsMetadataAndFileReads()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var recordingFileSystem = new RecordingFileSystem(new ReadAndMetadataFileSystem(), session);
            string textPath = Path.Combine(_env.DefaultTestDirectory.Path, "text.txt");
            string readerPath = Path.Combine(_env.DefaultTestDirectory.Path, "reader.txt");

            recordingFileSystem.ReadFileAllText(textPath).ShouldBe("content");
            recordingFileSystem.ReadFileAllBytes(textPath).ShouldBe(Encoding.UTF8.GetBytes("content"));
            recordingFileSystem.ReadFile(readerPath).ReadToEnd().ShouldBe("reader");
            recordingFileSystem.GetAttributes(textPath).ShouldBe(FileAttributes.ReadOnly);
            recordingFileSystem.GetLastWriteTimeUtc(textPath).ShouldBe(new DateTime(1234, DateTimeKind.Utc));

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            EvaluationFileReadObservation textRead = report.FileReads.Single(
                observation =>
                    FileUtilities.PathsEqual(observation.Path, textPath) &&
                    observation.HashKind == EvaluationContentHashKind.DecodedText);
            textRead.IsVerifiable.ShouldBeTrue();
            textRead.ContentHash.ShouldNotBeNull();
            report.FileReads.ShouldContain(observation =>
                FileUtilities.PathsEqual(observation.Path, textPath) &&
                observation.HashKind == EvaluationContentHashKind.RawBytes &&
                observation.IsVerifiable);

            EvaluationFileReadObservation readerRead = report.FileReads.Single(
                observation => FileUtilities.PathsEqual(observation.Path, readerPath));
            readerRead.IsVerifiable.ShouldBeFalse();
            readerRead.ContentHash.ShouldBeNull();
            report.MetadataReads.Count(observation => FileUtilities.PathsEqual(observation.Path, textPath))
                .ShouldBe(2);
            (report.Reasons & EvaluationObservationReason.UnverifiableFileRead)
                .ShouldBe(EvaluationObservationReason.UnverifiableFileRead);
            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void EvaluationObservationKeepsDistinctMetadataOperations()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string path = Path.Combine(_env.DefaultTestDirectory.Path, "metadata.txt");

            session.RecordMetadata(path, EvaluationMetadataKind.PropertyFunction, "first", null, "GetCreationTime");
            session.RecordMetadata(path, EvaluationMetadataKind.PropertyFunction, "second", null, "GetLastWriteTime");

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.MetadataReads.Count.ShouldBe(2);
            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.None);
        }

        [Fact]
        public void RecordingFileSystemMarksWritableStreamsUnsupported()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var recordingFileSystem = new RecordingFileSystem(new ReadAndMetadataFileSystem(), session);
            string path = Path.Combine(_env.DefaultTestDirectory.Path, "writable-stream.txt");

            using Stream stream = recordingFileSystem.GetFileStream(
                path,
                FileMode.OpenOrCreate,
                System.IO.FileAccess.ReadWrite,
                FileShare.None);

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.FileReads.ShouldContain(observation =>
                observation.Path == path &&
                !observation.IsVerifiable);
            EvaluationSideEffectObservation sideEffect = report.SideEffects.ShouldHaveSingleItem();
            sideEffect.Kind.ShouldBe("WritableFileStream");
            sideEffect.Identity.ShouldBe(path);
            (report.Reasons & EvaluationObservationReason.EvaluationSideEffect)
                .ShouldBe(EvaluationObservationReason.EvaluationSideEffect);
            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.VolatileOrSideEffect)
                .State.ShouldBe(EvaluationObservationCategoryState.Unsupported);
        }

        [Fact]
        public void EvaluationObservationDoesNotConflictVolatileOrDistinctInstances()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string firstPath = Path.Combine(_env.DefaultTestDirectory.Path, "first.txt");
            string secondPath = Path.Combine(_env.DefaultTestDirectory.Path, "second.txt");

            session.RecordPropertyFunction(
                typeof(DateTime),
                nameof(DateTime.UtcNow),
                instance: null,
                arguments: [],
                result: DateTime.UtcNow);
            session.RecordPropertyFunction(
                typeof(DateTime),
                nameof(DateTime.UtcNow),
                instance: null,
                arguments: [],
                result: DateTime.UtcNow.AddTicks(1));
            session.RecordPropertyFunction(
                typeof(FileInfo),
                nameof(FileInfo.Length),
                new FileInfo(firstPath),
                arguments: [],
                result: 1L);
            session.RecordPropertyFunction(
                typeof(FileInfo),
                nameof(FileInfo.Length),
                new FileInfo(secondPath),
                arguments: [],
                result: 2L);

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            report.PropertyFunctions.Count.ShouldBe(4);
            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.None);
            (report.Reasons & EvaluationObservationReason.UnsupportedVolatileInput)
                .ShouldBe(EvaluationObservationReason.UnsupportedVolatileInput);
        }

        [Fact]
        public void RecordingFileSystemMarksReadAndMetadataFailures()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            var recordingFileSystem = new RecordingFileSystem(new ThrowingFileSystem(), session);
            string root = _env.DefaultTestDirectory.Path;
            var operations = new List<(EvaluationObservationCategory Category, string Operation, string Path, Action Invoke)>();

            void AddOperation(
                EvaluationObservationCategory category,
                string operation,
                string pathSuffix,
                Action<string> invoke)
            {
                string path = Path.Combine(root, pathSuffix);
                operations.Add((category, operation, path, () => invoke(path)));
            }

            AddOperation(
                EvaluationObservationCategory.FileContent,
                nameof(IFileSystem.ReadFile),
                "reader",
                path => recordingFileSystem.ReadFile(path));
            AddOperation(
                EvaluationObservationCategory.FileContent,
                nameof(IFileSystem.ReadFileAllText),
                "text",
                path => recordingFileSystem.ReadFileAllText(path));
            AddOperation(
                EvaluationObservationCategory.FileContent,
                nameof(IFileSystem.ReadFileAllBytes),
                "bytes",
                path => recordingFileSystem.ReadFileAllBytes(path));
            AddOperation(
                EvaluationObservationCategory.FileContent,
                nameof(IFileSystem.GetFileStream),
                "read-stream",
                path => recordingFileSystem.GetFileStream(
                    path,
                    FileMode.Open,
                    System.IO.FileAccess.Read,
                    FileShare.Read));
            AddOperation(
                EvaluationObservationCategory.VolatileOrSideEffect,
                nameof(IFileSystem.GetFileStream),
                "write-stream",
                path => recordingFileSystem.GetFileStream(
                    path,
                    FileMode.OpenOrCreate,
                    System.IO.FileAccess.Write,
                    FileShare.None));
            AddOperation(
                EvaluationObservationCategory.VolatileOrSideEffect,
                nameof(IFileSystem.GetFileStream),
                "read-write-stream",
                path => recordingFileSystem.GetFileStream(
                    path,
                    FileMode.OpenOrCreate,
                    System.IO.FileAccess.ReadWrite,
                    FileShare.None));
            AddOperation(
                EvaluationObservationCategory.FileMetadata,
                nameof(IFileSystem.GetAttributes),
                "attributes",
                path => recordingFileSystem.GetAttributes(path));
            AddOperation(
                EvaluationObservationCategory.FileMetadata,
                nameof(IFileSystem.GetLastWriteTimeUtc),
                "write-time",
                path => recordingFileSystem.GetLastWriteTimeUtc(path));
            AddOperation(
                EvaluationObservationCategory.PathProbe,
                nameof(IFileSystem.FileExists),
                "file-probe",
                path => recordingFileSystem.FileExists(path));
            AddOperation(
                EvaluationObservationCategory.PathProbe,
                nameof(IFileSystem.DirectoryExists),
                "directory-probe",
                path => recordingFileSystem.DirectoryExists(path));
            AddOperation(
                EvaluationObservationCategory.PathProbe,
                nameof(IFileSystem.FileOrDirectoryExists),
                "path-probe",
                path => recordingFileSystem.FileOrDirectoryExists(path));
            AddOperation(
                EvaluationObservationCategory.DirectoryEnumeration,
                nameof(IFileSystem.EnumerateFiles),
                "files",
                path => recordingFileSystem.EnumerateFiles(path).ToArray());
            AddOperation(
                EvaluationObservationCategory.DirectoryEnumeration,
                nameof(IFileSystem.EnumerateDirectories),
                "directories",
                path => recordingFileSystem.EnumerateDirectories(path).ToArray());
            AddOperation(
                EvaluationObservationCategory.DirectoryEnumeration,
                nameof(IFileSystem.EnumerateFileSystemEntries),
                "entries",
                path => recordingFileSystem.EnumerateFileSystemEntries(path).ToArray());

            foreach (var operation in operations)
            {
                Should.Throw<IOException>(operation.Invoke);
            }

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: false);

            report.EvaluationSucceeded.ShouldBeFalse();
            (report.Reasons & EvaluationObservationReason.ExternalOperationFailure)
                .ShouldBe(EvaluationObservationReason.ExternalOperationFailure);
            report.OperationFailures.Count.ShouldBe(operations.Count);
            foreach (var expected in operations)
            {
                report.OperationFailures.ShouldContain(failure =>
                    failure.Category == expected.Category &&
                    failure.Operation == expected.Operation &&
                    failure.Path == expected.Path &&
                    failure.ExceptionType == typeof(IOException).FullName &&
                    failure.HResult == new IOException().HResult &&
                    failure.Message == "Operation failed." &&
                    failure.Provider.IndexOf(nameof(ThrowingFileSystem), StringComparison.Ordinal) >= 0);
            }

            report.Categories.Single(observation =>
                observation.Category == EvaluationObservationCategory.VolatileOrSideEffect)
                .State.ShouldBe(EvaluationObservationCategoryState.Unsupported);
        }

        [Fact]
        public void EvaluationObservationMarksNoThrowProbeFailuresAmbiguous()
        {
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            string path = Path.Combine(_env.DefaultTestDirectory.Path, "ambiguous.marker");

            using (session.Enter())
            {
                FileUtilities.FileExistsNoThrow(path, new ThrowingProbeFileSystem()).ShouldBeFalse();
            }

            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            (report.Reasons & EvaluationObservationReason.AmbiguousNegativeProbe)
                .ShouldBe(EvaluationObservationReason.AmbiguousNegativeProbe);
            report.Categories.ShouldContain(observation =>
                observation.Category == EvaluationObservationCategory.PathProbe &&
                observation.State == EvaluationObservationCategoryState.Incomplete);
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        public void ReevaluationShouldNotReuseInitialContext(EvaluationContext.SharingPolicy policy)
        {
            try
            {
                EvaluationContext.TestOnlyHookOnCreate = c => SetResolverForContext(c, _resolver);

                var collection = _env.CreateProjectCollection().Collection;

                var context = EvaluationContext.Create(policy);

                using var xmlReader = XmlReader.Create(new StringReader("<Project Sdk=\"foo\"></Project>"));
                var project = Project.FromXmlReader(
                    xmlReader,
                    new ProjectOptions
                    {
                        ProjectCollection = collection,
                        EvaluationContext = context,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                    });

                _resolver.ResolvedCalls["foo"].ShouldBe(1);

                project.AddItem("a", "b");

                project.ReevaluateIfNecessary();

                _resolver.ResolvedCalls["foo"].ShouldBe(2);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        public void ProjectInstanceShouldRespectSharingPolicy(EvaluationContext.SharingPolicy policy)
        {
            try
            {
                var seenContexts = new HashSet<EvaluationContext>();

                EvaluationContext.TestOnlyHookOnCreate = c => seenContexts.Add(c);

                var collection = _env.CreateProjectCollection().Collection;

                var context = EvaluationContext.Create(policy);

                const int numIterations = 10;
                for (int i = 0; i < numIterations; i++)
                {
                    ProjectInstance.FromProjectRootElement(
                        ProjectRootElement.Create(),
                        new ProjectOptions
                        {
                            ProjectCollection = collection,
                            EvaluationContext = context,
                            LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                        });
                }

                int expectedNumContexts = policy == EvaluationContext.SharingPolicy.Shared ? 1 : numIterations;

                seenContexts.Count.ShouldBe(expectedNumContexts);
                seenContexts.ShouldAllBe(c => c.Policy == policy);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        private static string[] _sdkResolutionProjects =
        {
            "<Project Sdk=\"foo\"></Project>",
            "<Project Sdk=\"bar\"></Project>",
            "<Project Sdk=\"foo\"></Project>",
            "<Project Sdk=\"bar\"></Project>"
        };

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Shared, 1, 1)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache, 1, 1)]
        [InlineData(EvaluationContext.SharingPolicy.Isolated, 4, 4)]
        public void ContextPinsSdkResolverCache(EvaluationContext.SharingPolicy policy, int sdkLookupsForFoo, int sdkLookupsForBar)
        {
            try
            {
                EvaluationContext.TestOnlyHookOnCreate = c => SetResolverForContext(c, _resolver);

                var context = EvaluationContext.Create(policy);
                EvaluateProjects(_sdkResolutionProjects, context, null);

                _resolver.ResolvedCalls.Count.ShouldBe(2);
                _resolver.ResolvedCalls["foo"].ShouldBe(sdkLookupsForFoo);
                _resolver.ResolvedCalls["bar"].ShouldBe(sdkLookupsForBar);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        [Fact]
        public void DefaultContextIsIsolatedContext()
        {
            try
            {
                var seenContexts = new HashSet<EvaluationContext>();

                EvaluationContext.TestOnlyHookOnCreate = c => seenContexts.Add(c);

                EvaluateProjects(_sdkResolutionProjects, null, null);

                seenContexts.Count.ShouldBe(8); // 4 evaluations and 4 reevaluations
                seenContexts.ShouldAllBe(c => c.Policy == EvaluationContext.SharingPolicy.Isolated);
            }
            finally
            {
                EvaluationContext.TestOnlyHookOnCreate = null;
            }
        }

        public static IEnumerable<object[]> ContextPinsGlobExpansionCacheData
        {
            get
            {
                yield return new object[]
                {
                    EvaluationContext.SharingPolicy.Shared,
                    new[]
                    {
                        new[] {"0.cs"},
                        new[] {"0.cs"},
                        new[] {"0.cs"},
                        new[] {"0.cs"}
                    }
                };

                foreach (var policy in new[] { EvaluationContext.SharingPolicy.SharedSDKCache, EvaluationContext.SharingPolicy.Isolated })
                {
                    yield return new object[]
                    {
                        policy,
                        new[]
                        {
                            new[] {"0.cs"},
                            new[] {"0.cs", "1.cs"},
                            new[] {"0.cs", "1.cs", "2.cs"},
                            new[] {"0.cs", "1.cs", "2.cs", "3.cs"},
                        }
                    };
                }
            }
        }

        private static string[] _projectsWithGlobs =
        {
            @"<Project>
                <ItemGroup>
                    <i Include=`**/*.cs` />
                </ItemGroup>
            </Project>",

            @"<Project>
                <ItemGroup>
                    <i Include=`**/*.cs` />
                </ItemGroup>
            </Project>",
        };

        [Theory]
        [MemberData(nameof(ContextPinsGlobExpansionCacheData))]
        public void ContextCachesItemElementGlobExpansions(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var projectDirectory = _env.DefaultTestDirectory.Path;

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.cs"), "");

            EvaluateProjects(
                _projectsWithGlobs,
                context,
                project =>
                {
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount];
                    evaluationCount++;

                    File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.cs"), "");

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));
                });
        }

        public static IEnumerable<object[]> ContextDisambiguatesRelativeGlobsData
        {
            get
            {
                yield return new object[]
                {
                    EvaluationContext.SharingPolicy.Shared,
                    new[]
                    {
                        new[] {"0.cs"}, // first project
                        new[] {"0.cs", "1.cs"}, // second project
                        new[] {"0.cs"}, // first project reevaluation
                        new[] {"0.cs", "1.cs"}, // second project reevaluation
                    }
                };

                foreach (var policy in new[] { EvaluationContext.SharingPolicy.SharedSDKCache, EvaluationContext.SharingPolicy.Isolated })
                {
                    yield return new object[]
                    {
                        policy,
                        new[]
                        {
                            new[] {"0.cs"},
                            new[] {"0.cs", "1.cs"},
                            new[] {"0.cs", "1.cs", "2.cs"},
                            new[] {"0.cs", "1.cs", "2.cs", "3.cs"},
                        }
                    };
                }
            }
        }

        [Theory]
        [MemberData(nameof(ContextDisambiguatesRelativeGlobsData))]
        public void ContextDisambiguatesSameRelativeGlobsPointingInsideDifferentProjectCones(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var projectDirectory1 = _env.DefaultTestDirectory.CreateDirectory("1").Path;
            var projectDirectory2 = _env.DefaultTestDirectory.CreateDirectory("2").Path;

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(projectDirectory1, $"1.{evaluationCount}.cs"), "");
            File.WriteAllText(Path.Combine(projectDirectory2, $"2.{evaluationCount}.cs"), "");

            EvaluateProjects(
                new[]
                {
                    new ProjectSpecification(
                        Path.Combine(projectDirectory1, "1"),
                        $@"<Project>
                            <ItemGroup>
                                <i Include=`{Path.Combine("**", "*.cs")}` />
                            </ItemGroup>
                        </Project>"),
                    new ProjectSpecification(
                        Path.Combine(projectDirectory2, "2"),
                        $@"<Project>
                            <ItemGroup>
                                <i Include=`{Path.Combine("**", "*.cs")}` />
                            </ItemGroup>
                        </Project>"),
                },
                context,
                project =>
                {
                    var projectName = Path.GetFileNameWithoutExtension(project.FullPath);

                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount]
                        .Select(i => $"{projectName}.{i}")
                        .ToArray();

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));

                    evaluationCount++;

                    File.WriteAllText(Path.Combine(projectDirectory1, $"1.{evaluationCount}.cs"), "");
                    File.WriteAllText(Path.Combine(projectDirectory2, $"2.{evaluationCount}.cs"), "");
                });
        }

        [Theory]
        [MemberData(nameof(ContextDisambiguatesRelativeGlobsData))]
        public void ContextDisambiguatesSameRelativeGlobsPointingOutsideDifferentProjectCones(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var project1Root = _env.DefaultTestDirectory.CreateDirectory("Project1");
            var project1Directory = project1Root.CreateDirectory("1").Path;
            var project1GlobDirectory = project1Root.CreateDirectory("Glob").CreateDirectory("1").Path;

            var project2Root = _env.DefaultTestDirectory.CreateDirectory("Project2");
            var project2Directory = project2Root.CreateDirectory("2").Path;
            var project2GlobDirectory = project2Root.CreateDirectory("Glob").CreateDirectory("2").Path;

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(project1GlobDirectory, $"1.{evaluationCount}.cs"), "");
            File.WriteAllText(Path.Combine(project2GlobDirectory, $"2.{evaluationCount}.cs"), "");

            EvaluateProjects(
                new[]
                {
                    new ProjectSpecification(
                        Path.Combine(project1Directory, "1"),
                        $@"<Project>
                            <ItemGroup>
                                <i Include=`{Path.Combine("..", "Glob", "**", "*.cs")}`/>
                            </ItemGroup>
                        </Project>"),
                    new ProjectSpecification(
                        Path.Combine(project2Directory, "2"),
                        $@"<Project>
                            <ItemGroup>
                                <i Include=`{Path.Combine("..", "Glob", "**", "*.cs")}`/>
                            </ItemGroup>
                        </Project>")
                },
                context,
                project =>
                {
                    var projectName = Path.GetFileNameWithoutExtension(project.FullPath);

                    // globs have the fixed directory part prepended, so add it to the expected results
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount]
                        .Select(i => Path.Combine("..", "Glob", projectName, $"{projectName}.{i}"))
                        .ToArray();

                    var actualGlobExpansion = project.GetItems("i");
                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, actualGlobExpansion);

                    evaluationCount++;

                    File.WriteAllText(Path.Combine(project1GlobDirectory, $"1.{evaluationCount}.cs"), "");
                    File.WriteAllText(Path.Combine(project2GlobDirectory, $"2.{evaluationCount}.cs"), "");
                });
        }

        [Theory]
        [MemberData(nameof(ContextDisambiguatesRelativeGlobsData))]
        public void ContextDisambiguatesAFullyQualifiedGlobPointingInAnotherRelativeGlobsCone(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            if (policy == EvaluationContext.SharingPolicy.Shared)
            {
                // This test case has a dependency on our glob expansion caching policy. If the evaluation context is reused
                // between evaluations and files are added to the filesystem between evaluations, the cache may be returning
                // stale results. Run only the Isolated variant.
                return;
            }

            var project1Directory = _env.DefaultTestDirectory.CreateDirectory("Project1");
            var project1GlobDirectory = project1Directory.CreateDirectory("Glob").CreateDirectory("1").Path;

            var project2Directory = _env.DefaultTestDirectory.CreateDirectory("Project2");

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(project1GlobDirectory, $"{evaluationCount}.cs"), "");

            EvaluateProjects(
                new[]
                {
                    // first project uses a relative path
                    new ProjectSpecification(
                        Path.Combine(project1Directory.Path, "1"),
                        $@"<Project>
                            <ItemGroup>
                                <i Include=`{Path.Combine("Glob", "**", "*.cs")}` />
                            </ItemGroup>
                        </Project>"),
                    // second project reaches out into first project's cone via a fully qualified path
                    new ProjectSpecification(
                        Path.Combine(project2Directory.Path, "2"),
                        $@"<Project>
                            <ItemGroup>
                                <i Include=`{Path.Combine(project1Directory.Path, "Glob", "**", "*.cs")}` />
                            </ItemGroup>
                        </Project>")
                },
                context,
                project =>
                {
                    var projectName = Path.GetFileNameWithoutExtension(project.FullPath);

                    // globs have the fixed directory part prepended, so add it to the expected results
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount]
                        .Select(i => Path.Combine("Glob", "1", i))
                        .ToArray();

                    // project 2 has fully qualified directory parts, so make the results for 2 fully qualified
                    if (projectName.Equals("2"))
                    {
                        expectedGlobExpansion = expectedGlobExpansion
                            .Select(i => Path.Combine(project1Directory.Path, i))
                            .ToArray();
                    }

                    var actualGlobExpansion = project.GetItems("i");
                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, actualGlobExpansion);

                    evaluationCount++;

                    File.WriteAllText(Path.Combine(project1GlobDirectory, $"{evaluationCount}.cs"), "");
                });
        }

        [Theory]
        [MemberData(nameof(ContextDisambiguatesRelativeGlobsData))]
        public void ContextDisambiguatesDistinctRelativeGlobsPointingOutsideOfSameProjectCone(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var globDirectory = _env.DefaultTestDirectory.CreateDirectory("glob");

            var projectRoot = _env.DefaultTestDirectory.CreateDirectory("proj");

            var project1Directory = projectRoot.CreateDirectory("Project1");

            var project2SubDir = projectRoot.CreateDirectory("subdirectory");

            var project2Directory = project2SubDir.CreateDirectory("Project2");

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(globDirectory.Path, $"{evaluationCount}.cs"), "");

            EvaluateProjects(
                new[]
                {
                    new ProjectSpecification(
                        Path.Combine(project1Directory.Path, "1"),
                        @"<Project>
                            <ItemGroup>
                                <i Include=`../../glob/*.cs` />
                            </ItemGroup>
                        </Project>"),
                    new ProjectSpecification(
                        Path.Combine(project2Directory.Path, "2"),
                        @"<Project>
                            <ItemGroup>
                                <i Include=`../../../glob/*.cs` />
                            </ItemGroup>
                        </Project>")
                },
                context,
                project =>
                {
                    var projectName = Path.GetFileNameWithoutExtension(project.FullPath);
                    var globFixedDirectoryPart = projectName.EndsWith("1", StringComparison.Ordinal)
                        ? Path.Combine("..", "..", "glob")
                        : Path.Combine("..", "..", "..", "glob");

                    // globs have the fixed directory part prepended, so add it to the expected results
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount]
                        .Select(i => Path.Combine(globFixedDirectoryPart, i))
                        .ToArray();

                    var actualGlobExpansion = project.GetItems("i");
                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, actualGlobExpansion);

                    evaluationCount++;

                    File.WriteAllText(Path.Combine(globDirectory.Path, $"{evaluationCount}.cs"), "");
                });
        }

        [Theory]
        [MemberData(nameof(ContextPinsGlobExpansionCacheData))]
        // projects should cache glob expansions when the __fully qualified__ glob is shared between projects and points outside of project cone
        public void ContextCachesCommonOutOfProjectConeFullyQualifiedGlob(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            ContextCachesCommonOutOfProjectCone(itemSpecPathIsRelative: false, policy: policy, expectedGlobExpansions: expectedGlobExpansions);
        }

        [Theory(Skip = "https://github.com/dotnet/msbuild/issues/3889")]
        [MemberData(nameof(ContextPinsGlobExpansionCacheData))]
        // projects should cache glob expansions when the __relative__ glob is shared between projects and points outside of project cone
        public void ContextCachesCommonOutOfProjectConeRelativeGlob(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            ContextCachesCommonOutOfProjectCone(itemSpecPathIsRelative: true, policy: policy, expectedGlobExpansions: expectedGlobExpansions);
        }

        private void ContextCachesCommonOutOfProjectCone(bool itemSpecPathIsRelative, EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var testDirectory = _env.DefaultTestDirectory;
            var globDirectory = testDirectory.CreateDirectory("GlobDirectory");

            var itemSpecDirectoryPart = itemSpecPathIsRelative
                ? Path.Combine("..", "GlobDirectory")
                : globDirectory.Path;

            Directory.CreateDirectory(globDirectory.Path);

            // Globs with a directory part will produce items prepended with that directory part.
            // Make a deep copy of the argument to avoid writing to global variables.
            string[][] prependedExpectedGlobExpansions = new string[expectedGlobExpansions.Length][];
            for (int expIndex = 0; expIndex < expectedGlobExpansions.Length; expIndex++)
            {
                string[] globExpansion = expectedGlobExpansions[expIndex];
                string[] prependedGlobExpansion = new string[globExpansion.Length];

                prependedExpectedGlobExpansions[expIndex] = prependedGlobExpansion;
                for (var i = 0; i < globExpansion.Length; i++)
                {
                    prependedGlobExpansion[i] = Path.Combine(itemSpecDirectoryPart, globExpansion[i]);
                }
            }

            var projectSpecs = new[]
            {
                $@"<Project>
                <ItemGroup>
                    <i Include=`{Path.Combine("{0}", "**", "*.cs")}`/>
                </ItemGroup>
            </Project>",
                $@"<Project>
                <ItemGroup>
                    <i Include=`{Path.Combine("{0}", "**", "*.cs")}`/>
                </ItemGroup>
            </Project>"
            }
                .Select(p => string.Format(p, itemSpecDirectoryPart))
                .Select((p, i) => new ProjectSpecification(Path.Combine(testDirectory.Path, $"ProjectDirectory{i}", $"Project{i}.proj"), p));

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(globDirectory.Path, $"{evaluationCount}.cs"), "");

            EvaluateProjects(
                projectSpecs,
                context,
                project =>
                {
                    var expectedGlobExpansion = prependedExpectedGlobExpansions[evaluationCount];
                    evaluationCount++;

                    File.WriteAllText(Path.Combine(globDirectory.Path, $"{evaluationCount}.cs"), "");

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));
                });
        }

        private static string[] _projectsWithGlobImports =
        {
            @"<Project>
                <Import Project=`*.props` />
            </Project>",

            @"<Project>
                <Import Project=`*.props` />
            </Project>",
        };

        [Theory]
        [MemberData(nameof(ContextPinsGlobExpansionCacheData))]
        public void ContextCachesImportGlobExpansions(EvaluationContext.SharingPolicy policy, string[][] expectedGlobExpansions)
        {
            var projectDirectory = _env.DefaultTestDirectory.Path;

            var context = EvaluationContext.Create(policy);

            var evaluationCount = 0;

            File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.props"), $"<Project><ItemGroup><i Include=`{evaluationCount}.cs`/></ItemGroup></Project>".Cleanup());

            EvaluateProjects(
                _projectsWithGlobImports,
                context,
                project =>
                {
                    var expectedGlobExpansion = expectedGlobExpansions[evaluationCount];
                    evaluationCount++;

                    File.WriteAllText(Path.Combine(projectDirectory, $"{evaluationCount}.props"), $"<Project><ItemGroup><i Include=`{evaluationCount}.cs`/></ItemGroup></Project>".Cleanup());

                    ObjectModelHelpers.AssertItems(expectedGlobExpansion, project.GetItems("i"));
                });
        }

        private static string[] _projectsWithConditions =
        {
            @"<Project>
                <PropertyGroup Condition=`Exists('0.cs')`>
                    <p>val</p>
                </PropertyGroup>
            </Project>",

            @"<Project>
                <PropertyGroup Condition=`Exists('0.cs')`>
                    <p>val</p>
                </PropertyGroup>
            </Project>",
        };

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        public void ContextCachesExistenceChecksInConditions(EvaluationContext.SharingPolicy policy)
        {
            var projectDirectory = _env.DefaultTestDirectory.Path;

            var context = EvaluationContext.Create(policy);

            var theFile = Path.Combine(projectDirectory, "0.cs");
            File.WriteAllText(theFile, "");

            var evaluationCount = 0;

            EvaluateProjects(
                _projectsWithConditions,
                context,
                project =>
                {
                    evaluationCount++;

                    if (File.Exists(theFile))
                    {
                        File.Delete(theFile);
                    }

                    if (evaluationCount == 1)
                    {
                        project.GetPropertyValue("p").ShouldBe("val");
                    }
                    else
                    {
                        switch (policy)
                        {
                            case EvaluationContext.SharingPolicy.Shared:
                                project.GetPropertyValue("p").ShouldBe("val");
                                break;
                            case EvaluationContext.SharingPolicy.SharedSDKCache:
                            case EvaluationContext.SharingPolicy.Isolated:
                                project.GetPropertyValue("p").ShouldBeEmpty();
                                break;
                            default:
                                throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                        }
                    }
                });
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        public void ContextCachesExistenceChecksInGetDirectoryNameOfFileAbove(EvaluationContext.SharingPolicy policy)
        {
            var context = EvaluationContext.Create(policy);

            var subdirectory = _env.DefaultTestDirectory.CreateDirectory("subDirectory");
            var subdirectoryFile = subdirectory.CreateFile("a");
            _env.DefaultTestDirectory.CreateFile("a");

            int evaluationCount = 0;

            EvaluateProjects(
                new[]
                {
                    $@"<Project>
                      <PropertyGroup>
                        <SearchedPath>$([MSBuild]::GetDirectoryNameOfFileAbove('{subdirectory.Path}', 'a'))</SearchedPath>
                      </PropertyGroup>
                    </Project>"
                },
                context,
                project =>
                {
                    evaluationCount++;

                    var searchedPath = project.GetProperty("SearchedPath");

                    switch (policy)
                    {
                        case EvaluationContext.SharingPolicy.Shared:
                            searchedPath.EvaluatedValue.ShouldBe(subdirectory.Path);
                            break;
                        case EvaluationContext.SharingPolicy.SharedSDKCache:
                        case EvaluationContext.SharingPolicy.Isolated:
                            searchedPath.EvaluatedValue.ShouldBe(
                                evaluationCount == 1
                                    ? subdirectory.Path
                                    : _env.DefaultTestDirectory.Path);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                    }

                    if (evaluationCount == 1)
                    {
                        // this will cause the upper file to get picked up in the Isolated policy
                        subdirectoryFile.Delete();
                    }
                });

            evaluationCount.ShouldBe(2);
        }

        [Fact]
        public void CachingFileSystemWrapperDistinguishesFileAndDirectoryExistence()
        {
            var directory = _env.DefaultTestDirectory.CreateDirectory("subDirectory");
            var file = _env.DefaultTestDirectory.CreateFile("file");
            var fileSystem = new CachingFileSystemWrapper(FileSystems.Default);

            fileSystem.FileExists(directory.Path).ShouldBeFalse();
            fileSystem.DirectoryExists(directory.Path).ShouldBeTrue();
            fileSystem.FileOrDirectoryExists(directory.Path).ShouldBeTrue();

            fileSystem.DirectoryExists(file.Path).ShouldBeFalse();
            fileSystem.FileExists(file.Path).ShouldBeTrue();
            fileSystem.FileOrDirectoryExists(file.Path).ShouldBeTrue();
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        public void GetDirectoryNameOfFileAboveWithEmptyFileNameDoesNotPoisonProjectLevelWildcards(EvaluationContext.SharingPolicy policy)
        {
            var context = EvaluationContext.Create(policy);
            _env.DefaultTestDirectory.CreateFile("Source.cs");

            EvaluateProjects(
                new[]
                {
                    """
                    <Project>
                      <PropertyGroup>
                        <SearchedPath>$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildProjectDirectory)', ''))</SearchedPath>
                      </PropertyGroup>
                      <ItemGroup>
                        <i Include="*.cs" />
                      </ItemGroup>
                    </Project>
                    """
                },
                context,
                project =>
                {
                    project.GetPropertyValue("SearchedPath").ShouldBeEmpty();
                    ObjectModelHelpers.AssertItems(["Source.cs"], project.GetItems("i"));
                });
        }

        [Theory]
        [InlineData(EvaluationContext.SharingPolicy.Isolated)]
        [InlineData(EvaluationContext.SharingPolicy.SharedSDKCache)]
        [InlineData(EvaluationContext.SharingPolicy.Shared)]
        public void ContextCachesExistenceChecksInGetPathOfFileAbove(EvaluationContext.SharingPolicy policy)
        {
            var context = EvaluationContext.Create(policy);

            var subdirectory = _env.DefaultTestDirectory.CreateDirectory("subDirectory");
            var subdirectoryFile = subdirectory.CreateFile("a");
            var rootFile = _env.DefaultTestDirectory.CreateFile("a");

            int evaluationCount = 0;

            EvaluateProjects(
                new[]
                {
                    $@"<Project>
                      <PropertyGroup>
                        <SearchedPath>$([MSBuild]::GetPathOfFileAbove('a', '{subdirectory.Path}'))</SearchedPath>
                      </PropertyGroup>
                    </Project>"
                },
                context,
                project =>
                {
                    evaluationCount++;

                    var searchedPath = project.GetProperty("SearchedPath");

                    switch (policy)
                    {
                        case EvaluationContext.SharingPolicy.Shared:
                            searchedPath.EvaluatedValue.ShouldBe(subdirectoryFile.Path);
                            break;
                        case EvaluationContext.SharingPolicy.SharedSDKCache:
                        case EvaluationContext.SharingPolicy.Isolated:
                            searchedPath.EvaluatedValue.ShouldBe(
                                evaluationCount == 1
                                    ? subdirectoryFile.Path
                                    : rootFile.Path);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(policy), policy, null);
                    }

                    if (evaluationCount == 1)
                    {
                        // this will cause the upper file to get picked up in the Isolated policy
                        subdirectoryFile.Delete();
                    }
                });

            evaluationCount.ShouldBe(2);
        }

        private abstract class TestFileSystemBase : IFileSystem
        {
            public virtual TextReader ReadFile(string path) => throw new NotSupportedException();
            public virtual Stream GetFileStream(string path, FileMode mode, System.IO.FileAccess access, FileShare share) => throw new NotSupportedException();
            public virtual string ReadFileAllText(string path) => throw new NotSupportedException();
            public virtual byte[] ReadFileAllBytes(string path) => throw new NotSupportedException();
            public virtual IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => throw new NotSupportedException();
            public virtual IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => throw new NotSupportedException();
            public virtual IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => throw new NotSupportedException();
            public virtual FileAttributes GetAttributes(string path) => throw new NotSupportedException();
            public virtual DateTime GetLastWriteTimeUtc(string path) => throw new NotSupportedException();
            public virtual bool DirectoryExists(string path) => throw new NotSupportedException();
            public virtual bool FileExists(string path) => throw new NotSupportedException();
            public virtual bool FileOrDirectoryExists(string path) => throw new NotSupportedException();
        }

        private sealed class PartialEnumerationFileSystem : TestFileSystemBase
        {
            internal int EntriesProduced { get; private set; }

            public override IEnumerable<string> EnumerateFiles(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly)
            {
                EntriesProduced++;
                yield return "first.cs";
                EntriesProduced++;
                yield return "second.cs";
            }
        }

        private sealed class AlternatingProbeFileSystem : TestFileSystemBase
        {
            private bool _exists;

            public override bool FileExists(string path)
            {
                bool result = _exists;
                _exists = true;
                return result;
            }
        }

        private sealed class ReadAndMetadataFileSystem : TestFileSystemBase
        {
            public override TextReader ReadFile(string path) => new StringReader("reader");
            public override Stream GetFileStream(
                string path,
                FileMode mode,
                System.IO.FileAccess access,
                FileShare share) => new MemoryStream();
            public override string ReadFileAllText(string path) => "content";
            public override byte[] ReadFileAllBytes(string path) => Encoding.UTF8.GetBytes("content");
            public override FileAttributes GetAttributes(string path) => FileAttributes.ReadOnly;
            public override DateTime GetLastWriteTimeUtc(string path) => new(1234, DateTimeKind.Utc);
        }

        private sealed class ThrowingFileSystem : TestFileSystemBase
        {
            public override TextReader ReadFile(string path) => throw new IOException("Operation failed.");
            public override Stream GetFileStream(
                string path,
                FileMode mode,
                System.IO.FileAccess access,
                FileShare share) => throw new IOException("Operation failed.");
            public override string ReadFileAllText(string path) => throw new IOException("Operation failed.");
            public override byte[] ReadFileAllBytes(string path) => throw new IOException("Operation failed.");
            public override IEnumerable<string> EnumerateFiles(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => throw new IOException("Operation failed.");
            public override IEnumerable<string> EnumerateDirectories(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => throw new IOException("Operation failed.");
            public override IEnumerable<string> EnumerateFileSystemEntries(string path, string searchPattern = "*", SearchOption searchOption = SearchOption.TopDirectoryOnly) => throw new IOException("Operation failed.");
            public override FileAttributes GetAttributes(string path) => throw new IOException("Operation failed.");
            public override DateTime GetLastWriteTimeUtc(string path) => throw new IOException("Operation failed.");
            public override bool DirectoryExists(string path) => throw new IOException("Operation failed.");
            public override bool FileExists(string path) => throw new IOException("Operation failed.");
            public override bool FileOrDirectoryExists(string path) => throw new IOException("Operation failed.");
        }

        private sealed class ThrowingProbeFileSystem : TestFileSystemBase
        {
            public override bool FileExists(string path) => throw new IOException("Probe failed.");
        }

        private sealed class ThrowingStringValue
        {
            public override string ToString() => throw new InvalidOperationException("Observation serialization failed.");
        }

        private sealed class ThrowingPathResolutionObserver : IEvaluationInputObserver
        {
            public bool RetainDetails => false;

            public void RecordPathProbe(string path, EvaluationPathProbeKind kind, bool exists)
            {
            }

            public void RecordAmbiguousPathProbe(string path, EvaluationPathProbeKind kind)
            {
            }

            public void RecordItemMetadata(
                string itemSpec,
                string modifier,
                string baseDirectory,
                string value)
            {
            }

            public void RecordPathAdjustment(string value, string baseDirectory, string result)
            {
            }

            public void RecordPathResolution(
                string operation,
                string firstInput,
                string secondInput,
                string firstResult,
                string secondResult)
            {
                throw new InvalidOperationException("Test-only observer failure.");
            }

            public void RecordSearch(
                string kind,
                string request,
                IReadOnlyList<string> candidates,
                int candidateCount,
                string candidatesFingerprint,
                string selected)
            {
            }
        }

        private void EvaluateProjects(IEnumerable<string> projectContents, EvaluationContext context, Action<Project> afterEvaluationAction)
        {
            EvaluateProjects(
                projectContents.Select((p, i) => new ProjectSpecification(Path.Combine(_env.DefaultTestDirectory.Path, $"Project{i}.proj"), p)),
                context,
                afterEvaluationAction);
        }

        private struct ProjectSpecification
        {
            public string ProjectFilePath { get; }
            public string ProjectContents { get; }

            public ProjectSpecification(string projectFilePath, string projectContents)
            {
                ProjectFilePath = projectFilePath;
                ProjectContents = projectContents;
            }

            public void Deconstruct(out string projectPath, out string projectContents)
            {
                projectPath = this.ProjectFilePath;
                projectContents = this.ProjectContents;
            }
        }

        /// <summary>
        /// Should be at least two test projects to test cache visibility between projects
        /// </summary>
        private void EvaluateProjects(IEnumerable<ProjectSpecification> projectSpecs, EvaluationContext context, Action<Project> afterEvaluationAction)
        {
            var collection = _env.CreateProjectCollection().Collection;

            var projects = new List<Project>();

            foreach (var (projectFilePath, projectContents) in projectSpecs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(projectFilePath));
                File.WriteAllText(projectFilePath, projectContents.Cleanup());

                var project = Project.FromFile(
                    projectFilePath,
                    new ProjectOptions
                    {
                        ProjectCollection = collection,
                        EvaluationContext = context,
                        LoadSettings = ProjectLoadSettings.IgnoreMissingImports
                    });

                afterEvaluationAction?.Invoke(project);

                projects.Add(project);
            }

            foreach (var project in projects)
            {
                project.AddItem("a", "b");
                project.ReevaluateIfNecessary(context);

                afterEvaluationAction?.Invoke(project);
            }
        }
    }
}
