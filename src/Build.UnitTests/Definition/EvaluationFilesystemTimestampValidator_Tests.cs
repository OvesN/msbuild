// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

#nullable disable

namespace Microsoft.Build.UnitTests.Definition
{
    public sealed class EvaluationFilesystemTimestampValidator_Tests : IDisposable
    {
        private readonly TestEnvironment _env;
        private readonly ITestOutputHelper _output;

        public EvaluationFilesystemTimestampValidator_Tests(ITestOutputHelper output)
        {
            _output = output;
            _env = CreateTestEnvironment(output);
        }

        public void Dispose()
        {
            _env.Dispose();
        }

        [Fact]
        public void EveryObservationReasonBlocksCacheAdmission()
        {
            EvaluationCategoryObservation[] categories = CreateCompleteCategories();

            foreach (EvaluationObservationReason reason in Enum.GetValues(typeof(EvaluationObservationReason)))
            {
                if (reason == EvaluationObservationReason.None)
                {
                    continue;
                }

                EvaluationObservationReport report = CreateReport(
                    evaluationSucceeded: true,
                    reason,
                    categories);

                report.HasBlockingObservations.ShouldBeTrue($"Reason: {reason}");
                EvaluationFilesystemTimestampCaptureResult capture =
                    EvaluationFilesystemTimestampValidator.Capture(report);
                capture.Status.ShouldBe(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                    $"Reason: {reason}");
                capture.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.BlockingObservation,
                    $"Reason: {reason}");
                capture.Snapshot.ShouldBeNull($"Reason: {reason}");
            }
        }

        [Fact]
        public void NonCompleteCoverageOrBlockingStatePreventsCacheAdmission()
        {
            foreach (EvaluationObservationCoverage coverage in Enum.GetValues(
                typeof(EvaluationObservationCoverage)))
            {
                foreach (EvaluationObservationCategoryState state in Enum.GetValues(
                    typeof(EvaluationObservationCategoryState)))
                {
                    bool allowed =
                        coverage == EvaluationObservationCoverage.Complete &&
                        state is EvaluationObservationCategoryState.NotExercised or
                            EvaluationObservationCategoryState.Observed;
                    EvaluationCategoryObservation[] categories = CreateCompleteCategories();
                    ReplaceCategory(
                        categories,
                        EvaluationObservationCategory.PropertyFunction,
                        coverage,
                        state);
                    EvaluationObservationReport report = CreateReport(
                        evaluationSucceeded: true,
                        EvaluationObservationReason.None,
                        categories);

                    report.HasBlockingObservations.ShouldBe(
                        !allowed,
                        $"Coverage={coverage}; State={state}");
                    if (!allowed)
                    {
                        EvaluationFilesystemTimestampCaptureResult capture =
                            EvaluationFilesystemTimestampValidator.Capture(report);
                        capture.Status.ShouldBe(
                            EvaluationFilesystemTimestampCaptureStatus.Unsupported,
                            $"Coverage={coverage}; State={state}");
                        capture.Failure.ShouldBe(
                            EvaluationFilesystemTimestampFailure.BlockingObservation,
                            $"Coverage={coverage}; State={state}");
                        capture.Snapshot.ShouldBeNull(
                            $"Coverage={coverage}; State={state}");
                    }
                }
            }
        }

        [Fact]
        public void IncompleteCategorySetPreventsCacheAdmission()
        {
            EvaluationCategoryObservation[] complete = CreateCompleteCategories();
            var missingOne = new EvaluationCategoryObservation[complete.Length - 1];
            Array.Copy(complete, missingOne, missingOne.Length);
            EvaluationCategoryObservation[] duplicate = (EvaluationCategoryObservation[])complete.Clone();
            duplicate[duplicate.Length - 1] = duplicate[0];
            EvaluationCategoryObservation[][] invalidCategorySets =
            [
                null,
                [],
                missingOne,
                duplicate,
            ];

            foreach (EvaluationCategoryObservation[] categories in invalidCategorySets)
            {
                EvaluationObservationReport report = CreateReport(
                    evaluationSucceeded: true,
                    EvaluationObservationReason.None,
                    categories);

                report.HasBlockingObservations.ShouldBeTrue();
                EvaluationFilesystemTimestampCaptureResult capture =
                    EvaluationFilesystemTimestampValidator.Capture(report);
                capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
                capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.IncompleteCategorySet);
                capture.Snapshot.ShouldBeNull();

                EvaluationFilesystemTimestampCaptureResult analysisCapture =
                    EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
                analysisCapture.Status.ShouldBe(
                    EvaluationFilesystemTimestampCaptureStatus.Unsupported);
                analysisCapture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
                analysisCapture.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.IncompleteCategorySet);
                analysisCapture.Snapshot.ShouldBeNull();
            }
        }

        [Fact]
        public void FailedEvaluationIsBlocking()
        {
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: false,
                EvaluationObservationReason.None,
                CreateCompleteCategories());

            report.HasBlockingObservations.ShouldBeTrue();
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.Capture(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.UnsuccessfulEvaluation);
            capture.Snapshot.ShouldBeNull();
        }

        [Theory]
        [InlineData(-1, 0)]
        [InlineData(1, 0)]
        [InlineData(0, -1)]
        [InlineData(0, 1)]
        public void UnsupportedObservationVersionPreventsCacheAdmission(
            int schemaVersionDelta,
            int classificationVersionDelta)
        {
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(),
                schemaVersion:
                    EvaluationObservationSession.ObservationSchemaVersion + schemaVersionDelta,
                propertyFunctionClassificationVersion:
                    EvaluationObservationSession.PropertyFunctionClassificationVersion +
                    classificationVersionDelta);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.Capture(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.UnsupportedObservationVersion);
            capture.Snapshot.ShouldBeNull();
        }

        [Fact]
        public void CacheAdmissionRequiresMatchingRequestEvidence()
        {
            string projectPath = _env.CreateFile(
                "request-root.proj",
                "<Project />").Path;
            string otherPath = _env.CreateFile(
                "other.proj",
                "<Project />").Path;
            EvaluationRequestObservation[] invalidRequests =
            [
                null,
                new EvaluationRequestObservation { ProjectPath = otherPath },
            ];

            foreach (EvaluationRequestObservation request in invalidRequests)
            {
                EvaluationObservationReport report = CreateReport(
                    evaluationSucceeded: true,
                    EvaluationObservationReason.None,
                    CreateCompleteCategories(EvaluationObservationCategory.ProjectSource),
                    projectPath,
                    [CreateProjectSource(EvaluationProjectSourceRole.Root, projectPath)],
                    request);

                EvaluationFilesystemTimestampCaptureResult capture =
                    EvaluationFilesystemTimestampValidator.Capture(report);

                capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
                capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
                capture.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.MissingRequestObservation);
                capture.Snapshot.ShouldBeNull();
            }
        }

        [Fact]
        public void CacheAdmissionRequiresMatchingParsedRootProjectSourceEvidence()
        {
            string projectPath = _env.CreateFile(
                "root.proj",
                "<Project />").Path;
            string otherPath = _env.CreateFile(
                "import.props",
                "<Project />").Path;
            EvaluationProjectSourceObservation[] invalidRootSources =
            [
                CreateProjectSource(EvaluationProjectSourceRole.Import, projectPath),
                CreateProjectSource(EvaluationProjectSourceRole.Root, otherPath),
                CreateProjectSource(
                    EvaluationProjectSourceRole.Root,
                    projectPath,
                    EvaluationProjectSourceOutcome.ParseFailure),
            ];

            foreach (EvaluationProjectSourceObservation source in invalidRootSources)
            {
                EvaluationObservationReport report = CreateReport(
                    evaluationSucceeded: true,
                    EvaluationObservationReason.None,
                    CreateCompleteCategories(EvaluationObservationCategory.ProjectSource),
                    projectPath,
                    [source],
                    CreateRequest(projectPath));

                report.HasBlockingObservations.ShouldBeFalse();
                EvaluationFilesystemTimestampCaptureResult capture =
                    EvaluationFilesystemTimestampValidator.Capture(report);

                capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
                capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
                capture.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.MissingRootProjectSourceObservation);
                capture.Snapshot.ShouldBeNull();
            }
        }

        [Fact]
        public void FullyEligibleSyntheticReportCaptures()
        {
            string projectPath = _env.CreateFile(
                "eligible.proj",
                "<Project />").Path;
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(EvaluationObservationCategory.ProjectSource),
                projectPath,
                [CreateProjectSource(EvaluationProjectSourceRole.Root, projectPath)],
                CreateRequest(projectPath));

            report.HasBlockingObservations.ShouldBeFalse();
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.Capture(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Success);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeTrue();
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.None);
            capture.Snapshot.Entries.ShouldHaveSingleItem().Path.ShouldBe(projectPath);
        }

        [Fact]
        public void CurrentObserverCoverageKeepsCacheAdmissionDisabled()
        {
            EvaluationObservationReport report = Evaluate(
                "partial-coverage.proj",
                "<Project />");
            var allCategories = (EvaluationObservationCategory[])Enum.GetValues(
                typeof(EvaluationObservationCategory));

            report.Categories.Length.ShouldBe(allCategories.Length);
            foreach (EvaluationCategoryObservation category in report.Categories)
            {
                category.Coverage.ShouldBe(
                    category.Category == EvaluationObservationCategory.Completion
                        ? EvaluationObservationCoverage.Complete
                        : EvaluationObservationCoverage.Partial,
                    $"Category: {category.Category}");
            }

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.Capture(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.BlockingObservation);
            capture.Snapshot.ShouldBeNull();
        }

        [Fact]
        public void ConflictingObservationPreventsFilesystemSliceCapture()
        {
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.ConflictingObservation,
                CreateCompleteCategories());

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ConflictingObservation);
            capture.Snapshot.ShouldBeNull();
        }

        [Fact]
        public void UnchangedFilesystemSnapshotValidates()
        {
            _env.CreateFolder(Path.Combine(_env.DefaultTestDirectory.Path, "Nested"));
            _env.CreateFile(
                Path.Combine("Nested", "Observed.cs"),
                string.Empty);
            _env.CreateFile("settings.txt", "settings-value");
            EvaluationObservationReport report = Evaluate(
                "unchanged.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('missing.props')">
                    <Missing>true</Missing>
                  </PropertyGroup>
                  <PropertyGroup>
                    <Settings>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)settings.txt'))</Settings>
                  </PropertyGroup>
                  <ItemGroup>
                    <Compile Include="**/*.cs" />
                  </ItemGroup>
                </Project>
                """);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.None);
            capture.Snapshot.ShouldNotBeNull();
            capture.Snapshot.TimestampCount.ShouldBeGreaterThan(3);
            capture.Snapshot.ReparsePointCheckCount.ShouldBeGreaterThanOrEqualTo(
                capture.Snapshot.TimestampCount);
            capture.ReparsePointProbeCount.ShouldBe(
                capture.Snapshot.ReparsePointCheckCount * 2);
            foreach (EvaluationFilesystemTimestampEntry entry in capture.Snapshot.Entries)
            {
                capture.Snapshot.ReparsePointCheckPaths.ShouldContain(path =>
                    FileUtilities.PathsEqual(path, entry.Path));
            }
            capture.Snapshot.Entries.ShouldContain(entry =>
                (entry.Sources & EvaluationFilesystemTimestampSource.Glob) != 0);

            EvaluationFilesystemTimestampValidationResult reparsePointValidation =
                capture.Snapshot.ValidateReparsePoints();
            reparsePointValidation.Status.ShouldBe(
                EvaluationFilesystemTimestampValidationStatus.Valid);
            reparsePointValidation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.None);
            reparsePointValidation.CheckedReparsePointCount.ShouldBe(
                capture.Snapshot.ReparsePointCheckCount);
            reparsePointValidation.CheckedTimestampCount.ShouldBe(0);

            EvaluationFilesystemTimestampValidationResult timestampValidation =
                EvaluationFilesystemTimestampValidator
                    .ValidateTimestampsWithoutReparsePointCheck(capture.Snapshot);
            timestampValidation.Status.ShouldBe(
                EvaluationFilesystemTimestampValidationStatus.Valid);
            timestampValidation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.None);
            timestampValidation.CheckedReparsePointCount.ShouldBe(0);
            timestampValidation.CheckedTimestampCount.ShouldBe(
                capture.Snapshot.TimestampCount);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Valid);
            validation.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.None);
            validation.CheckedReparsePointCount.ShouldBe(
                capture.Snapshot.ReparsePointCheckCount);
            validation.CheckedTimestampCount.ShouldBe(capture.Snapshot.TimestampCount);
        }

        [Fact]
        public void ChangedProjectSourceInvalidates()
        {
            string projectFile;
            EvaluationObservationReport report = Evaluate(
                "changed-source.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>before</Value>
                  </PropertyGroup>
                </Project>
                """,
                out projectFile);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);

            SetDistinctLastWriteTimeUtc(projectFile);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, projectFile).ShouldBeTrue();
            validation.ExpectedLastWriteTimeUtcTicks
                .ShouldNotBe(validation.ActualLastWriteTimeUtcTicks);
        }

        [Fact]
        public void ProjectSourceChangedBeforeCaptureIsRejected()
        {
            string projectFile;
            EvaluationObservationReport report = Evaluate(
                "changed-before-capture.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>before</Value>
                  </PropertyGroup>
                </Project>
                """,
                out projectFile);

            SetDistinctLastWriteTimeUtc(projectFile);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Changed);
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.ConflictingTimestamp);
            FileUtilities.PathsEqual(capture.Path, projectFile).ShouldBeTrue();
        }

        [Fact]
        public void ChangedFileReadInvalidates()
        {
            string settingsFile = _env.CreateFile("settings.txt", "before").Path;
            EvaluationObservationReport report = Evaluate(
                "changed-read.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Settings>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)settings.txt'))</Settings>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);

            SetDistinctLastWriteTimeUtc(settingsFile);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, settingsFile).ShouldBeTrue();
        }

        [Fact]
        public void FileReadKindReplacementWithPreservedTimestampInvalidates()
        {
            string settingsFile = _env.CreateFile("kind-read.txt", "before").Path;
            EvaluationObservationReport report = Evaluate(
                "kind-read.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Settings>$([System.IO.File]::ReadAllText('$(MSBuildThisFileDirectory)kind-read.txt'))</Settings>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            EvaluationFilesystemTimestampEntry entry =
                GetEntry(
                    capture.Snapshot,
                    settingsFile,
                    EvaluationFilesystemTimestampSource.FileRead);
            entry.Existence.FileExists.ShouldBe(true);
            DateTime timestamp = File.GetLastWriteTimeUtc(settingsFile);

            File.Delete(settingsFile);
            Directory.CreateDirectory(settingsFile);
            Directory.SetLastWriteTimeUtc(settingsFile, timestamp);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ExistenceChanged);
            validation.ExpectedLastWriteTimeUtcTicks.ShouldBe(
                validation.ActualLastWriteTimeUtcTicks);
        }

        [Fact]
        public void DirectoryEnumerationKindReplacementWithPreservedTimestampInvalidates()
        {
            string directory = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "enumerated");
            Directory.CreateDirectory(directory);
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            var recordingFileSystem =
                new RecordingFileSystem(FileSystems.Default, session);
            foreach (string _ in recordingFileSystem.EnumerateFiles(directory))
            {
            }

            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            EvaluationFilesystemTimestampEntry entry =
                capture.Snapshot.Entries.ShouldHaveSingleItem();
            entry.Existence.DirectoryExists.ShouldBe(true);
            DateTime timestamp = Directory.GetLastWriteTimeUtc(directory);

            Directory.Delete(directory);
            File.WriteAllText(directory, string.Empty);
            File.SetLastWriteTimeUtc(directory, timestamp);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ExistenceChanged);
            validation.ExpectedLastWriteTimeUtcTicks.ShouldBe(
                validation.ActualLastWriteTimeUtcTicks);
        }

        [Fact]
        public void CreatedNegativeProbeInvalidates()
        {
            string missingFile = Path.Combine(_env.DefaultTestDirectory.Path, "missing.props");
            EvaluationObservationReport report = Evaluate(
                "negative-probe.proj",
                """
                <Project>
                  <PropertyGroup Condition="Exists('missing.props')">
                    <Unexpected>true</Unexpected>
                  </PropertyGroup>
                </Project>
                """);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            capture.Snapshot.ReparsePointCheckPaths.ShouldContain(path =>
                FileUtilities.PathsEqual(path, missingFile));

            File.WriteAllText(missingFile, "<Project />");

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, missingFile).ShouldBeTrue();
        }

        [Fact]
        public void CreatedMissingGlobRootInvalidates()
        {
            string missingDirectory = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "MissingGlobRoot");
            EvaluationObservationReport report = Evaluate(
                "missing-glob-root.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="MissingGlobRoot/**/*.cs" />
                  </ItemGroup>
                </Project>
                """);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            EvaluationFilesystemTimestampEntry entry =
                GetEntry(
                    capture.Snapshot,
                    missingDirectory,
                    EvaluationFilesystemTimestampSource.Glob);
            entry.Existence.DirectoryExists.ShouldBe(false);

            Directory.CreateDirectory(missingDirectory);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, missingDirectory).ShouldBeTrue();
        }

        [Fact]
        public void RecursiveGlobMembershipChangeInvalidates()
        {
            _env.CreateFolder(Path.Combine(_env.DefaultTestDirectory.Path, "Nested"));
            string nestedDirectory = Path.Combine(_env.DefaultTestDirectory.Path, "Nested");
            Directory.SetLastWriteTimeUtc(
                nestedDirectory,
                DateTime.UtcNow.AddMinutes(-5));
            EvaluationObservationReport report = Evaluate(
                "glob.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="**/*.cs" />
                  </ItemGroup>
                </Project>
                """);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            capture.Snapshot.Entries.ShouldContain(entry =>
                FileUtilities.PathsEqual(entry.Path, nestedDirectory) &&
                (entry.Sources & EvaluationFilesystemTimestampSource.Glob) != 0);

            File.WriteAllText(Path.Combine(nestedDirectory, "Added.cs"), string.Empty);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, nestedDirectory).ShouldBeTrue();
        }

        [Fact]
        public void OutOfTreeRecursiveGlobMembershipChangeInvalidates()
        {
            string projectDirectory = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "Project");
            string sharedDirectory = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "Shared");
            string nestedSharedDirectory = Path.Combine(sharedDirectory, "Nested");
            TransientTestFolder projectFolder =
                _env.CreateFolder(projectDirectory);
            _env.CreateFolder(nestedSharedDirectory);
            Directory.SetLastWriteTimeUtc(
                nestedSharedDirectory,
                DateTime.UtcNow.AddMinutes(-5));
            string projectFile = _env.CreateFile(
                projectFolder,
                "out-of-tree.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="../Shared/**/*.cs" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            EvaluationObservationReport report = Evaluate(projectFile);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            capture.Snapshot.Entries.ShouldContain(entry =>
                FileUtilities.PathsEqual(entry.Path, nestedSharedDirectory) &&
                (entry.Sources & EvaluationFilesystemTimestampSource.Glob) != 0);

            File.WriteAllText(
                Path.Combine(nestedSharedDirectory, "Added.cs"),
                string.Empty);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, nestedSharedDirectory).ShouldBeTrue();
        }

        [WindowsOnlyFact]
        public void JunctionGlobTraversalIsRejected()
        {
            TransientTestFolder root = _env.CreateFolder();
            TransientTestFolder project = _env.CreateFolder(
                Path.Combine(root.Path, "Project"));
            TransientTestFolder target = _env.CreateFolder(
                Path.Combine(root.Path, "Target"));
            _env.CreateFile(target, "Observed.cs", string.Empty);
            TransientTestFolder targetSubdirectory = _env.CreateFolder(
                Path.Combine(target.Path, "Sub"));
            _env.CreateFile(targetSubdirectory, "Nested.cs", string.Empty);
            string junction = Path.Combine(project.Path, "Linked");

            CreateJunction(junction, target.Path);
            try
            {
                AssertReparsePointGlobTraversalIsRejected(
                    project,
                    junction,
                    "junction-root.proj",
                    Path.Combine("Linked", "**", "*.cs"),
                    expectedResultCount: 2);
                AssertReparsePointGlobTraversalIsRejected(
                    project,
                    junction,
                    "junction-ancestor.proj",
                    Path.Combine("Linked", "Sub", "**", "*.cs"),
                    expectedResultCount: 1);
                AssertMissingPathBeneathReparsePointIsRejected(junction);
                AssertProjectBeneathReparsePointIsRejected(target, junction);
            }
            finally
            {
                if (Directory.Exists(junction))
                {
                    Directory.Delete(junction);
                }
            }
        }

        [WindowsOnlyFact]
        public void ReparsePointIntroducedAfterCaptureInvalidates()
        {
            TransientTestFolder project = _env.CreateFolder();
            TransientTestFolder observedDirectory = _env.CreateFolder(
                Path.Combine(project.Path, "Observed"));
            _env.CreateFile(observedDirectory, "Input.cs", string.Empty);
            TransientTestFolder target = _env.CreateFolder();
            _env.CreateFile(target, "Input.cs", string.Empty);
            string projectFile = _env.CreateFile(
                project,
                "replacement.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="Observed/**/*.cs" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;
            EvaluationObservationReport report = Evaluate(projectFile);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            capture.Snapshot.ReparsePointCheckPaths.ShouldContain(path =>
                FileUtilities.PathsEqual(path, observedDirectory.Path));

            Directory.Delete(observedDirectory.Path, recursive: true);
            CreateJunction(observedDirectory.Path, target.Path);
            try
            {
                EvaluationFilesystemTimestampValidationResult validation =
                    capture.Snapshot.Validate();

                validation.Status.ShouldBe(
                    EvaluationFilesystemTimestampValidationStatus.Changed);
                validation.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.ReparsePointTraversal);
                validation.CheckedReparsePointCount.ShouldBeGreaterThan(0);
                validation.CheckedTimestampCount.ShouldBe(0);
                FileUtilities.PathsEqual(
                    validation.Path,
                    observedDirectory.Path).ShouldBeTrue();
            }
            finally
            {
                if (Directory.Exists(observedDirectory.Path))
                {
                    Directory.Delete(observedDirectory.Path);
                }
            }
        }

#if NET
        [WindowsOnlyFact]
        public void ReparseTempRootIsResolved()
        {
            TransientTestFolder target = _env.CreateFolder();
            string junction = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "TempJunction");
            CreateJunction(junction, target.Path);
            try
            {
                using TestEnvironment redirected =
                    CreateTestEnvironment(_output, junction);
                string testDirectory = redirected.DefaultTestDirectory.Path;

                HasReparsePointComponent(testDirectory).ShouldBeFalse();
                testDirectory.StartsWith(
                    string.Concat(target.Path, Path.DirectorySeparatorChar),
                    FileUtilities.PathComparison).ShouldBeTrue();
            }
            finally
            {
                if (Directory.Exists(junction))
                {
                    Directory.Delete(junction);
                }
            }
        }
#endif

        [Fact]
        public void ReparsePointAttributeFailureRejectsCapture()
        {
            string projectFile = _env.CreateFile(
                "attribute-failure.proj",
                "<Project />").Path;
            EvaluationObservationReport report = Evaluate(projectFile);
            // Initialize the validator's accepted default-provider identity before replacing FileSystems.Default.
            EvaluationFilesystemTimestampCaptureResult baselineCapture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(baselineCapture, report);
            baselineCapture.Status.ShouldBe(
                EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            _env.WithTransientTestState(
                new TransientDefaultFileSystem(
                    new ThrowingAttributesFileSystem(
                        FileSystems.Default,
                        projectFile)));

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Failed);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ReparsePointStateUnknown);
            FileUtilities.PathsEqual(capture.Path, projectFile).ShouldBeTrue();
            capture.ExceptionType.ShouldBe(typeof(UnauthorizedAccessException).FullName);
            capture.HResult.ShouldNotBe(0);
            capture.ReparsePointProbeCount.ShouldBeGreaterThan(0);
            capture.Snapshot.ShouldBeNull();
        }

        [WindowsOnlyFact]
        public void InvalidParentPathFailsClosed()
        {
            string invalidParent = string.Concat(
                Path.GetPathRoot(_env.DefaultTestDirectory.Path),
                "invalid|component");
            string invalidPath = string.Concat(
                invalidParent,
                Path.DirectorySeparatorChar,
                "input.props");
            var timestamp = new EvaluationFilesystemTimestampObservation(
                invalidPath,
                DateTime.FromFileTimeUtc(0).Ticks,
                EvaluationPathExistence.Create(
                    EvaluationPathKind.File,
                    exists: false),
                EvaluationFilesystemTimestampSource.PathProbe,
                provider: null);
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(),
                filesystemTimestamps: [timestamp]);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Failed);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ReparsePointStateUnknown);
#if NETFRAMEWORK
            capture.Path.ShouldBe(invalidPath);
            capture.ExceptionType.ShouldBe(typeof(ArgumentException).FullName);
#else
            capture.Path.ShouldBe(invalidParent);
            capture.ExceptionType.ShouldBe(typeof(IOException).FullName);
#endif
            capture.HResult.ShouldNotBe(0);
            capture.Snapshot.ShouldBeNull();
        }

        [Fact]
        public void ReparsePointAttributeFailureFailsValidation()
        {
            string projectFile = _env.CreateFile(
                "validation-attribute-failure.proj",
                "<Project />").Path;
            EvaluationObservationReport report = Evaluate(projectFile);
            // Initialize the validator's accepted default-provider identity before replacing FileSystems.Default.
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            _env.WithTransientTestState(
                new TransientDefaultFileSystem(
                    new ThrowingAttributesFileSystem(
                        FileSystems.Default,
                        projectFile)));

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Failed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ReparsePointStateUnknown);
            validation.CheckedReparsePointCount.ShouldBeGreaterThan(0);
            FileUtilities.PathsEqual(validation.Path, projectFile).ShouldBeTrue();
            validation.ExceptionType.ShouldBe(
                typeof(UnauthorizedAccessException).FullName);
            validation.HResult.ShouldNotBe(0);
        }

#if FEATURE_SYMLINK_TARGET
        [RequiresSymbolicLinksFact]
        public void SymbolicLinkGlobTraversalIsRejected()
        {
            TransientTestFolder root = _env.CreateFolder();
            TransientTestFolder project = _env.CreateFolder(
                Path.Combine(root.Path, "Project"));
            TransientTestFolder target = _env.CreateFolder(
                Path.Combine(root.Path, "Target"));
            _env.CreateFile(target, "Observed.cs", string.Empty);
            TransientTestFolder targetSubdirectory = _env.CreateFolder(
                Path.Combine(target.Path, "Sub"));
            _env.CreateFile(targetSubdirectory, "Nested.cs", string.Empty);
            string symbolicLink = Path.Combine(project.Path, "Linked");

            Directory.CreateSymbolicLink(symbolicLink, target.Path);
            try
            {
                AssertReparsePointGlobTraversalIsRejected(
                    project,
                    symbolicLink,
                    "symlink-root.proj",
                    Path.Combine("Linked", "**", "*.cs"),
                    expectedResultCount: 2);
                AssertReparsePointGlobTraversalIsRejected(
                    project,
                    symbolicLink,
                    "symlink-ancestor.proj",
                    Path.Combine("Linked", "Sub", "**", "*.cs"),
                    expectedResultCount: 1);
                AssertMissingPathBeneathReparsePointIsRejected(symbolicLink);
                AssertProjectBeneathReparsePointIsRejected(target, symbolicLink);
            }
            finally
            {
                if (Directory.Exists(symbolicLink))
                {
                    Directory.Delete(symbolicLink);
                }
            }
        }

        [RequiresSymbolicLinksFact]
        public void SymbolicLinkProjectSourceIsRejected()
        {
            TransientTestFolder root = _env.CreateFolder();
            TransientTestFolder project = _env.CreateFolder(
                Path.Combine(root.Path, "Project"));
            string target = _env.CreateFile(
                root,
                "Target.props",
                """
                <Project>
                  <PropertyGroup>
                    <Imported>true</Imported>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;
            string symbolicLink = Path.Combine(project.Path, "Linked.props");

            File.CreateSymbolicLink(symbolicLink, target);
            try
            {
                string projectFile = _env.CreateFile(
                    project,
                    "symlink-import.proj",
                    """
                    <Project>
                      <Import Project="Linked.props" />
                    </Project>
                    """.Cleanup()).Path;
                EvaluationObservationReport report = Evaluate(projectFile);

                report.ProjectSources.ShouldContain(source =>
                    FileUtilities.PathsEqual(source.Path, symbolicLink));
                EvaluationFilesystemTimestampCaptureResult capture =
                    EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
                WriteCaptureFailure(capture, report);

                capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
                capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
                capture.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.ReparsePointTraversal);
                FileUtilities.PathsEqual(capture.Path, symbolicLink).ShouldBeTrue();
                capture.Snapshot.ShouldBeNull();
            }
            finally
            {
                if (File.Exists(symbolicLink))
                {
                    File.Delete(symbolicLink);
                }
            }
        }
#endif

        [Fact]
        public void CreatedSearchCandidateInvalidates()
        {
            string candidate = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "candidate.props");
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                candidate,
                EvaluationPathKind.File,
                exists: false);
            session.RecordSearch(
                "TestSearch",
                "candidate.props",
                [candidate],
                selected: null,
                complete: true);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);

            File.WriteAllText(candidate, "<Project />");

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, candidate).ShouldBeTrue();
        }

        [Fact]
        public void ProductionSearchCaptureRetainsCandidatesAndSelectedFile()
        {
            string marker = _env.CreateFile("marker.txt", string.Empty).Path;
            TransientTestFolder nested =
                _env.CreateFolder(Path.Combine(_env.DefaultTestDirectory.Path, "Nested"));
            string projectFile = _env.CreateFile(
                nested,
                "search.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Found>$([MSBuild]::GetDirectoryNameOfFileAbove('$(MSBuildProjectDirectory)', 'marker.txt'))</Found>
                  </PropertyGroup>
                </Project>
                """.Cleanup()).Path;

            EvaluationObservationReport report = Evaluate(projectFile);
            EvaluationSearchObservation search = default;
            bool foundSearch = false;
            foreach (EvaluationSearchObservation candidate in report.Searches)
            {
                if (candidate.Kind == "GetDirectoryNameOfFileAbove")
                {
                    search = candidate;
                    foundSearch = true;
                    break;
                }
            }

            foundSearch.ShouldBeTrue();
            search.Candidates.Length.ShouldBe(search.CandidateCount);
            search.CandidateCount.ShouldBeGreaterThan(1);
            search.SelectedPaths.Length.ShouldBe(search.SelectedPathCount);
            search.SelectedPathCount.ShouldBe(1);
            FileUtilities.PathsEqual(search.SelectedPaths[0], marker).ShouldBeTrue();

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
        }

        [Fact]
        public void ExcludedGlobRootsAreNotTracked()
        {
            string binDirectory = _env.CreateFolder(
                Path.Combine(_env.DefaultTestDirectory.Path, "bin")).Path;
            string objDirectory = _env.CreateFolder(
                Path.Combine(_env.DefaultTestDirectory.Path, "obj")).Path;
            _env.CreateFolder(Path.Combine(_env.DefaultTestDirectory.Path, "src"));
            _env.CreateFile(Path.Combine("src", "Included.cs"), string.Empty);
            _env.CreateFile(Path.Combine("bin", "Excluded.cs"), string.Empty);
            _env.CreateFile(Path.Combine("obj", "Excluded.cs"), string.Empty);
            EvaluationObservationReport report = Evaluate(
                "excluded-globs.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="**/*.cs" Exclude="bin/**;obj/**" />
                  </ItemGroup>
                </Project>
                """);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            capture.Snapshot.Entries.ShouldNotContain(entry =>
                (entry.Sources & EvaluationFilesystemTimestampSource.Glob) != 0 &&
                (FileUtilities.PathsEqual(entry.Path, binDirectory) ||
                 FileUtilities.PathsEqual(entry.Path, objDirectory)));
        }

        [Fact]
        public void RepeatedCachedGlobRetainsTraversalCoverage()
        {
            _env.CreateFolder(Path.Combine(_env.DefaultTestDirectory.Path, "src"));
            _env.CreateFile(Path.Combine("src", "Included.cs"), string.Empty);
            EvaluationObservationReport report = Evaluate(
                "repeated-glob.proj",
                """
                <Project>
                  <ItemGroup>
                    <Compile Include="src/**/*.cs" />
                    <Content Include="src/**/*.cs" />
                  </ItemGroup>
                </Project>
                """);

            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.None);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
        }

        [Fact]
        public void CachedImportAndItemGlobShareTraversalCoverage()
        {
            _env.CreateFolder(Path.Combine(_env.DefaultTestDirectory.Path, "Imports"));
            _env.CreateFile(
                Path.Combine("Imports", "Imported.props"),
                "<Project />");
            EvaluationObservationReport report = Evaluate(
                "shared-import-item-glob.proj",
                """
                <Project>
                  <Import Project="Imports/**/*.props" />
                  <ItemGroup>
                    <None Include="Imports/**/*.props" />
                  </ItemGroup>
                </Project>
                """);

            int matchingGlobCount = 0;
            foreach (EvaluationGlobObservation glob in report.Globs)
            {
                if (glob.Include == "Imports/**/*.props")
                {
                    matchingGlobCount++;
                    glob.TraversedDirectories.ShouldNotBeEmpty();
                }
            }

            matchingGlobCount.ShouldBe(2);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
        }

        [Fact]
        public void EachTraversingGlobRequiresItsOwnDirectoryCoverage()
        {
            string coveredDirectory = _env.CreateFolder(
                Path.Combine(_env.DefaultTestDirectory.Path, "Covered")).Path;
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            string globIdentity = FileMatcher.ComputeFileEnumerationCacheKey(
                _env.DefaultTestDirectory.Path,
                "Covered/**/*.cs",
                excludes: []);
            ((IEvaluationInputObserver)session).RecordGlobDirectory(
                _env.DefaultTestDirectory.Path,
                "Covered/**/*.cs",
                coveredDirectory,
                exists: true,
                globIdentity: globIdentity);
            session.RecordGlob(
                "Item",
                _env.DefaultTestDirectory.Path,
                "Covered/**/*.cs",
                excludes: [],
                results: [],
                filesystemTraversalExpected: true,
                resultsEscaped: false,
                wasLazy: false,
                driveEnumerating: false,
                failure: null);
            session.RecordGlob(
                "Item",
                _env.DefaultTestDirectory.Path,
                "Missing/**/*.cs",
                excludes: [],
                results: [],
                filesystemTraversalExpected: true,
                resultsEscaped: false,
                wasLazy: false,
                driveEnumerating: false,
                failure: null);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.MissingTimestampObservation);
        }

        [Fact]
        public void ExactIncludeWithExcludesDoesNotRequireGlobTraversal()
        {
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordGlob(
                "Item",
                _env.DefaultTestDirectory.Path,
                "file.cs",
                excludes: ["excluded.cs"],
                results: ["file.cs"],
                filesystemTraversalExpected: false,
                resultsEscaped: false,
                wasLazy: false,
                driveEnumerating: false,
                failure: null);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
        }

        [Fact]
        public void EqualTimestampWithChangedExistenceInvalidates()
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "zero-timestamp.props");
            long zeroTimestamp = DateTime.FromFileTimeUtc(0).Ticks;
            var snapshot = new EvaluationFilesystemTimestampSnapshot(
                [
                    new EvaluationFilesystemTimestampEntry(
                        path,
                        zeroTimestamp,
                        EvaluationPathExistence.Create(
                            EvaluationPathKind.File,
                            exists: true),
                        EvaluationFilesystemTimestampSource.PathProbe),
                ],
                CreatePathComponents(path));

            EvaluationFilesystemTimestampValidationResult validation =
                snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ExistenceChanged);
            validation.ExpectedLastWriteTimeUtcTicks
                .ShouldBe(validation.ActualLastWriteTimeUtcTicks);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void OppositeKindProbesRemainIndependent(bool isDirectory)
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "opposite-kind");
            if (isDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }

            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                path,
                EvaluationPathKind.File,
                exists: !isDirectory);
            session.RecordProbe(
                path,
                EvaluationPathKind.Directory,
                exists: isDirectory);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.None);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            EvaluationFilesystemTimestampEntry entry =
                capture.Snapshot.Entries.ShouldHaveSingleItem();
            entry.Existence.FileExists.ShouldBe(!isDirectory);
            entry.Existence.DirectoryExists.ShouldBe(isDirectory);
            entry.Existence.FileOrDirectoryExists.ShouldBeNull();
            capture.Snapshot.Validate().Status.ShouldBe(
                EvaluationFilesystemTimestampValidationStatus.Valid);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void KindReplacementWithPreservedTimestampInvalidates(
            bool initiallyDirectory)
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "kind-replacement");
            if (initiallyDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }

            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                path,
                initiallyDirectory
                    ? EvaluationPathKind.Directory
                    : EvaluationPathKind.File,
                exists: true);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            DateTime timestamp = initiallyDirectory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);

            if (initiallyDirectory)
            {
                Directory.Delete(path);
                File.WriteAllText(path, string.Empty);
                File.SetLastWriteTimeUtc(path, timestamp);
            }
            else
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
                Directory.SetLastWriteTimeUtc(path, timestamp);
            }

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ExistenceChanged);
            validation.ExpectedLastWriteTimeUtcTicks.ShouldBe(
                validation.ActualLastWriteTimeUtcTicks);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void GenericExistenceAllowsKindReplacement(
            bool initiallyDirectory)
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "generic-kind-replacement");
            if (initiallyDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }

            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                path,
                EvaluationPathKind.FileOrDirectory,
                exists: true);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            DateTime timestamp = initiallyDirectory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);

            if (initiallyDirectory)
            {
                Directory.Delete(path);
                File.WriteAllText(path, string.Empty);
                File.SetLastWriteTimeUtc(path, timestamp);
            }
            else
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
                Directory.SetLastWriteTimeUtc(path, timestamp);
            }

            capture.Snapshot.Validate().Status.ShouldBe(
                EvaluationFilesystemTimestampValidationStatus.Valid);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void NegativeTypedProbeKindReplacementInvalidates(
            bool initiallyDirectory)
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "negative-kind-replacement");
            if (initiallyDirectory)
            {
                Directory.CreateDirectory(path);
            }
            else
            {
                File.WriteAllText(path, string.Empty);
            }

            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                path,
                initiallyDirectory
                    ? EvaluationPathKind.File
                    : EvaluationPathKind.Directory,
                exists: false);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            DateTime timestamp = initiallyDirectory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);

            if (initiallyDirectory)
            {
                Directory.Delete(path);
                File.WriteAllText(path, string.Empty);
                File.SetLastWriteTimeUtc(path, timestamp);
            }
            else
            {
                File.Delete(path);
                Directory.CreateDirectory(path);
                Directory.SetLastWriteTimeUtc(path, timestamp);
            }

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ExistenceChanged);
            validation.ExpectedLastWriteTimeUtcTicks.ShouldBe(
                validation.ActualLastWriteTimeUtcTicks);
        }

        [Fact]
        public void ContradictoryExistenceMergesAreRejected()
        {
            EvaluationPathExistence fileExists =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.File,
                    exists: true);
            fileExists.TryMerge(
                EvaluationPathKind.Directory,
                exists: true,
                out _).ShouldBeFalse();
            fileExists.TryMerge(
                EvaluationPathKind.File,
                exists: false,
                out _).ShouldBeFalse();

            EvaluationPathExistence pathMissing =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.FileOrDirectory,
                    exists: false);
            pathMissing.TryMerge(
                EvaluationPathKind.File,
                exists: true,
                out _).ShouldBeFalse();

            EvaluationPathExistence typedMissing =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.File,
                    exists: false);
            typedMissing.TryMerge(
                EvaluationPathKind.Directory,
                exists: false,
                out EvaluationPathExistence bothTypedMissing).ShouldBeTrue();
            bothTypedMissing.FileExists.ShouldBeNull();
            bothTypedMissing.DirectoryExists.ShouldBeNull();
            bothTypedMissing.FileOrDirectoryExists.ShouldBe(false);
            bothTypedMissing.TryMerge(
                EvaluationPathKind.FileOrDirectory,
                exists: true,
                out _).ShouldBeFalse();
        }

        [Fact]
        public void EntailedExistencePredicatesAreCollapsed()
        {
            EvaluationPathExistence fileExists =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.File,
                    exists: true);
            fileExists.TryMerge(
                EvaluationPathKind.FileOrDirectory,
                exists: true,
                out EvaluationPathExistence fileAndGeneric).ShouldBeTrue();
            fileAndGeneric.FileOrDirectoryExists.ShouldBeNull();
            fileAndGeneric.TryGet(
                EvaluationPathKind.FileOrDirectory,
                out bool genericExists).ShouldBeTrue();
            genericExists.ShouldBeTrue();

            EvaluationPathExistence pathMissing =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.FileOrDirectory,
                    exists: false);
            pathMissing.TryMerge(
                EvaluationPathKind.File,
                exists: false,
                out EvaluationPathExistence genericAndFileMissing).ShouldBeTrue();
            genericAndFileMissing.FileExists.ShouldBeNull();
            genericAndFileMissing.TryGet(
                EvaluationPathKind.File,
                out bool fileStillMissing).ShouldBeTrue();
            fileStillMissing.ShouldBeFalse();

            EvaluationPathExistence fileMissing =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.File,
                    exists: false);
            fileMissing.TryMerge(
                EvaluationPathKind.FileOrDirectory,
                exists: true,
                out EvaluationPathExistence existingDirectory).ShouldBeTrue();
            existingDirectory.TryGet(
                EvaluationPathKind.Directory,
                out bool directoryExists).ShouldBeTrue();
            directoryExists.ShouldBeTrue();

            EvaluationPathExistence directoryMissing =
                EvaluationPathExistence.Create(
                    EvaluationPathKind.Directory,
                    exists: false);
            directoryMissing.TryMerge(
                EvaluationPathKind.FileOrDirectory,
                exists: true,
                out EvaluationPathExistence existingFile).ShouldBeTrue();
            existingFile.TryGet(
                EvaluationPathKind.File,
                out bool inferredFileExists).ShouldBeTrue();
            inferredFileExists.ShouldBeTrue();
        }

        [Fact]
        public void TimestampAndGenericExistenceConflictIsRejected()
        {
            string path = _env.CreateFile(
                "timestamp-existence-conflict.txt",
                string.Empty).Path;
            var timestamp = new EvaluationFilesystemTimestampObservation(
                path,
                File.GetLastWriteTimeUtc(path).Ticks,
                EvaluationPathExistence.Create(
                    EvaluationPathKind.FileOrDirectory,
                    exists: false),
                EvaluationFilesystemTimestampSource.PathProbe,
                provider: null);
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(),
                filesystemTimestamps: [timestamp]);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ConflictingObservation);
            capture.Path.ShouldBe(path);
        }

        [Fact]
        public void MissingExistenceEvidenceIsUnsupported()
        {
            string path = _env.CreateFile(
                "missing-existence-evidence.txt",
                string.Empty).Path;
            var timestamp = new EvaluationFilesystemTimestampObservation(
                path,
                File.GetLastWriteTimeUtc(path).Ticks,
                existence: default,
                EvaluationFilesystemTimestampSource.FileRead,
                provider: null);
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(),
                filesystemTimestamps: [timestamp]);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.MissingExistenceObservation);
            capture.Path.ShouldBe(path);
        }

        [Fact]
        public void ObservationSessionRejectsTimestampExistenceConflictOnReuse()
        {
            string path = _env.CreateFile(
                "session-timestamp-existence-conflict.txt",
                string.Empty).Path;
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordFilesystemTimestamp(
                path,
                EvaluationFilesystemTimestampSource.PathProbe,
                kind: EvaluationPathKind.File,
                exists: true);
            session.RecordFilesystemTimestamp(
                path,
                EvaluationFilesystemTimestampSource.PathProbe,
                kind: EvaluationPathKind.FileOrDirectory,
                exists: false);

            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            (report.Reasons & EvaluationObservationReason.ConflictingObservation)
                .ShouldBe(EvaluationObservationReason.ConflictingObservation);
        }

        [Fact]
        public void IncompleteReparsePointCheckSetFailsValidation()
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "unchecked.props");
            string[] completeComponents = CreatePathComponents(path);
            var duplicateComponents =
                new string[completeComponents.Length + 1];
            Array.Copy(
                completeComponents,
                duplicateComponents,
                completeComponents.Length);
            duplicateComponents[duplicateComponents.Length - 1] =
                completeComponents[completeComponents.Length - 1];
            string[][] invalidCheckSets =
            [
                [],
                [path],
                duplicateComponents,
            ];

            foreach (string[] invalidCheckSet in invalidCheckSets)
            {
                var snapshot = new EvaluationFilesystemTimestampSnapshot(
                    [
                        new EvaluationFilesystemTimestampEntry(
                            path,
                            DateTime.FromFileTimeUtc(0).Ticks,
                            EvaluationPathExistence.Create(
                                EvaluationPathKind.File,
                                exists: false),
                            EvaluationFilesystemTimestampSource.PathProbe),
                    ],
                    invalidCheckSet);

                EvaluationFilesystemTimestampValidationResult validation =
                    snapshot.Validate();

                validation.Status.ShouldBe(
                    EvaluationFilesystemTimestampValidationStatus.Failed);
                validation.Failure.ShouldBe(
                    EvaluationFilesystemTimestampFailure.IncompleteReparsePointCheckSet);
                validation.CheckedReparsePointCount.ShouldBe(0);
                validation.CheckedTimestampCount.ShouldBe(0);
                validation.Path.ShouldNotBeNull();
            }
        }

        [Fact]
        public void MalformedSnapshotFailsValidation()
        {
            var snapshot = new EvaluationFilesystemTimestampSnapshot(
                entries: null,
                reparsePointCheckPaths: []);

            EvaluationFilesystemTimestampValidationResult validation =
                snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Failed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.MalformedSnapshot);
            validation.CheckedReparsePointCount.ShouldBe(0);
            validation.CheckedTimestampCount.ShouldBe(0);
        }

        [Fact]
        public void NonCanonicalPathFailsClosed()
        {
            string directory = _env.CreateFolder(
                Path.Combine(
                    _env.DefaultTestDirectory.Path,
                    "NonCanonical")).Path;
            string path = string.Concat(
                directory,
                Path.DirectorySeparatorChar);
            var timestamp = new EvaluationFilesystemTimestampObservation(
                path,
                Directory.GetLastWriteTimeUtc(directory).Ticks,
                EvaluationPathExistence.Create(
                    EvaluationPathKind.Directory,
                    exists: true),
                EvaluationFilesystemTimestampSource.PathProbe,
                provider: null);
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(),
                filesystemTimestamps: [timestamp]);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.NonCanonicalPath);
            capture.Path.ShouldBe(path);

            var snapshot = new EvaluationFilesystemTimestampSnapshot(
                [
                    new EvaluationFilesystemTimestampEntry(
                        path,
                        timestamp.LastWriteTimeUtcTicks,
                        EvaluationPathExistence.Create(
                            EvaluationPathKind.Directory,
                            exists: true),
                        EvaluationFilesystemTimestampSource.PathProbe),
                ],
                [path]);
            EvaluationFilesystemTimestampValidationResult validation =
                snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Failed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.NonCanonicalPath);
            validation.Path.ShouldBe(path);
        }

        [Fact]
        public void DotSegmentPathFailsClosed()
        {
            string path = Path.Combine(
                _env.DefaultTestDirectory.Path,
                "Child",
                "..",
                "input.props");
            var timestamp = new EvaluationFilesystemTimestampObservation(
                path,
                DateTime.FromFileTimeUtc(0).Ticks,
                EvaluationPathExistence.Create(
                    EvaluationPathKind.File,
                    exists: false),
                EvaluationFilesystemTimestampSource.PathProbe,
                provider: null);
            EvaluationObservationReport report = CreateReport(
                evaluationSucceeded: true,
                EvaluationObservationReason.None,
                CreateCompleteCategories(),
                filesystemTimestamps: [timestamp]);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.NonCanonicalPath);
            capture.Path.ShouldBe(path);

            var snapshot = new EvaluationFilesystemTimestampSnapshot(
                [
                    new EvaluationFilesystemTimestampEntry(
                        path,
                        timestamp.LastWriteTimeUtcTicks,
                        EvaluationPathExistence.Create(
                            EvaluationPathKind.File,
                            exists: false),
                        EvaluationFilesystemTimestampSource.PathProbe),
                ],
                CreatePathComponents(path));
            EvaluationFilesystemTimestampValidationResult validation =
                snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Failed);
            validation.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.NonCanonicalPath);
            validation.Path.ShouldBe(Path.GetDirectoryName(path));
        }

        [Fact]
        public void UnsupportedFilesystemMetadataIsRejected()
        {
            string path = _env.CreateFile("metadata.txt", string.Empty).Path;
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordMetadata(
                path,
                EvaluationMetadataKind.Attributes,
                (long)File.GetAttributes(path));
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.UnsupportedMetadata);
            FileUtilities.PathsEqual(capture.Path, path).ShouldBeTrue();
        }

        [Fact]
        public void DuplicateTimestampSourcesShareOneEntry()
        {
            string path = _env.CreateFile("deduplicated.txt", string.Empty).Path;
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(path, EvaluationPathKind.File, exists: true);
            session.RecordFilesystemTimestamp(
                path,
                EvaluationFilesystemTimestampSource.FileRead);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            EvaluationFilesystemTimestampEntry entry =
                capture.Snapshot.Entries.ShouldHaveSingleItem();
            (entry.Sources & EvaluationFilesystemTimestampSource.PathProbe)
                .ShouldBe(EvaluationFilesystemTimestampSource.PathProbe);
            (entry.Sources & EvaluationFilesystemTimestampSource.FileRead)
                .ShouldBe(EvaluationFilesystemTimestampSource.FileRead);
        }

        [Fact]
        public void TimestampPreservingContentChangeIsNotDetected()
        {
            string projectFile;
            EvaluationObservationReport report = Evaluate(
                "preserved-timestamp.proj",
                """
                <Project>
                  <PropertyGroup>
                    <Value>before</Value>
                  </PropertyGroup>
                </Project>
                """,
                out projectFile);
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);
            WriteCaptureFailure(capture, report);
            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly);
            DateTime originalTimestamp = File.GetLastWriteTimeUtc(projectFile);

            File.WriteAllText(
                projectFile,
                """
                <Project>
                  <PropertyGroup>
                    <Value>after</Value>
                  </PropertyGroup>
                </Project>
                """);
            File.SetLastWriteTimeUtc(projectFile, originalTimestamp);

            capture.Snapshot.Validate().Status
                .ShouldBe(EvaluationFilesystemTimestampValidationStatus.Valid);
        }

        [Fact]
        public void CustomFilesystemProviderIsUnsupported()
        {
            string path = _env.CreateFile("custom.txt", string.Empty).Path;
            EvaluationObservationSession session = EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                path,
                EvaluationPathKind.File,
                exists: true,
                provider: "CustomProvider");
            EvaluationObservationReport report = session.Complete(evaluationSucceeded: true);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.Failure.ShouldBe(EvaluationFilesystemTimestampFailure.UnsupportedProvider);
            FileUtilities.PathsEqual(capture.Path, path).ShouldBeTrue();
            capture.Snapshot.ShouldBeNull();
        }

        private EvaluationObservationReport Evaluate(string fileName, string projectXml)
        {
            return Evaluate(fileName, projectXml, out _);
        }

        private EvaluationObservationReport Evaluate(
            string fileName,
            string projectXml,
            out string projectFile)
        {
            projectFile = _env.CreateFile(fileName, projectXml.Cleanup()).Path;
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport,
                retainDetails: false);
            using ProjectCollection collection = new();

            _ = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = collection,
            });

            report.ShouldNotBeNull();
            return report;
        }

        private EvaluationObservationReport Evaluate(string projectFile)
        {
            EvaluationObservationReport report = null;
            using IDisposable scope = EvaluationObservationSession.TestOnlyConfigure(
                enabled: true,
                createdReport => report = createdReport,
                retainDetails: false);
            using ProjectCollection collection = new();

            _ = Project.FromFile(projectFile, new ProjectOptions
            {
                ProjectCollection = collection,
            });

            report.ShouldNotBeNull();
            return report;
        }

        private static void SetDistinctLastWriteTimeUtc(
            string path,
            bool isDirectory = false)
        {
            DateTime current = isDirectory
                ? Directory.GetLastWriteTimeUtc(path)
                : File.GetLastWriteTimeUtc(path);
            DateTime changed = current.AddSeconds(10);
            if (isDirectory)
            {
                Directory.SetLastWriteTimeUtc(path, changed);
            }
            else
            {
                File.SetLastWriteTimeUtc(path, changed);
            }
        }

        private void AssertReparsePointGlobTraversalIsRejected(
            TransientTestFolder project,
            string reparsePoint,
            string projectFileName,
            string include,
            int expectedResultCount)
        {
            string projectFile = _env.CreateFile(
                project,
                projectFileName,
                $"""
                <Project>
                  <ItemGroup>
                    <Compile Include="{include}" />
                  </ItemGroup>
                </Project>
                """.Cleanup()).Path;

            EvaluationObservationReport report = Evaluate(projectFile);

            report.Globs.ShouldContain(
                observation => observation.ResultCount == expectedResultCount);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ReparsePointTraversal);
            FileUtilities.PathsEqual(capture.Path, reparsePoint).ShouldBeTrue();
            capture.Snapshot.ShouldBeNull();
        }

        private void AssertMissingPathBeneathReparsePointIsRejected(
            string reparsePoint)
        {
            string missingPath = Path.Combine(reparsePoint, "Missing.props");
            EvaluationObservationSession session =
                EvaluationObservationSession.CreateForTests();
            session.RecordProbe(
                missingPath,
                EvaluationPathKind.File,
                exists: false);
            EvaluationObservationReport report =
                session.Complete(evaluationSucceeded: true);

            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ReparsePointTraversal);
            FileUtilities.PathsEqual(capture.Path, reparsePoint).ShouldBeTrue();
            capture.Snapshot.ShouldBeNull();
        }

        private void AssertProjectBeneathReparsePointIsRejected(
            TransientTestFolder target,
            string reparsePoint)
        {
            const string projectFileName = "project-under-reparse.proj";
            _env.CreateFile(target, projectFileName, "<Project />");
            string logicalProjectPath =
                Path.Combine(reparsePoint, projectFileName);
            EvaluationObservationReport report = Evaluate(logicalProjectPath);

            report.ProjectSources.ShouldContain(source =>
                FileUtilities.PathsEqual(source.Path, logicalProjectPath));
            EvaluationFilesystemTimestampCaptureResult capture =
                EvaluationFilesystemTimestampValidator.CaptureFilesystemSliceForAnalysis(report);

            capture.Status.ShouldBe(EvaluationFilesystemTimestampCaptureStatus.Unsupported);
            capture.IsFilesystemSnapshotAdmissible.ShouldBeFalse();
            capture.Failure.ShouldBe(
                EvaluationFilesystemTimestampFailure.ReparsePointTraversal);
            FileUtilities.PathsEqual(capture.Path, reparsePoint).ShouldBeTrue();
            capture.Snapshot.ShouldBeNull();
        }

        private void CreateJunction(string junction, string target)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe",
                Arguments = $"/d /c mklink /J \"{junction}\" \"{target}\"",
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            using Process process = Process.Start(startInfo)
                ?? throw new InvalidOperationException("Failed to start junction creation.");
            string standardOutput = process.StandardOutput.ReadToEnd();
            string standardError = process.StandardError.ReadToEnd();
            process.WaitForExit();

            process.ExitCode.ShouldBe(
                0,
                $"Output: {standardOutput}{Environment.NewLine}Error: {standardError}");
        }

        private static bool HasReparsePointComponent(string path)
        {
            string current = Path.GetFullPath(path);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    return true;
                }

                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) ||
                    FileUtilities.PathsEqual(parent, current))
                {
                    return false;
                }

                current = parent;
            }

            return false;
        }

        private static TestEnvironment CreateTestEnvironment(
            ITestOutputHelper output,
            string tempPathOverride = null)
        {
            string tempPath = tempPathOverride ?? Path.GetTempPath();
            TestEnvironment environment = TestEnvironment.Create(output);
            if (!HasReparsePointComponent(tempPath))
            {
                return environment;
            }

#if NET
            string resolvedTempPath = ResolveDirectoryLinks(tempPath);
            if (!HasReparsePointComponent(resolvedTempPath))
            {
                environment.SetTempPath(resolvedTempPath);
                return environment;
            }
#endif

            environment.Dispose();
            Assert.Skip(
                $"Timestamp validator tests require a temporary root without reparse components. Current root: '{tempPath}'.");
            throw new InvalidOperationException("Unreachable after Assert.Skip.");
        }

#if NET
        private static string ResolveDirectoryLinks(string path)
        {
            string fullPath = Path.GetFullPath(path);
            string root = Path.GetPathRoot(fullPath);
            string current = root;
            string[] components = fullPath.Substring(root.Length).Split(
                [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar],
                StringSplitOptions.RemoveEmptyEntries);
            foreach (string component in components)
            {
                current = Path.Combine(current, component);
                FileSystemInfo target =
                    Directory.ResolveLinkTarget(current, returnFinalTarget: true);
                if (target is not null)
                {
                    current = target.FullName;
                }
            }

            return current;
        }
#endif

        private static string[] CreatePathComponents(string path)
        {
            List<string> components = [];
            string current = path;
            while (!string.IsNullOrEmpty(current))
            {
                components.Add(current);
                string parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) ||
                    FileUtilities.PathsEqual(parent, current))
                {
                    break;
                }

                current = parent;
            }

            components.Reverse();
            return components.ToArray();
        }

        private static EvaluationFilesystemTimestampEntry GetEntry(
            EvaluationFilesystemTimestampSnapshot snapshot,
            string path,
            EvaluationFilesystemTimestampSource source)
        {
            foreach (EvaluationFilesystemTimestampEntry entry in snapshot.Entries)
            {
                if (FileUtilities.PathsEqual(entry.Path, path) &&
                    (entry.Sources & source) != 0)
                {
                    return entry;
                }
            }

            throw new InvalidOperationException(
                $"Timestamp entry '{path}' with source '{source}' was not found.");
        }

        private sealed class TransientDefaultFileSystem : TransientTestState
        {
            private readonly IFileSystem _original;

            internal TransientDefaultFileSystem(IFileSystem replacement)
            {
                _original = FileSystems.Default;
                FileSystems.Default = replacement;
            }

            public override void Revert()
            {
                FileSystems.Default = _original;
            }
        }

        private sealed class ThrowingAttributesFileSystem : IFileSystem
        {
            private readonly IFileSystem _inner;
            private readonly string _throwPath;

            internal ThrowingAttributesFileSystem(
                IFileSystem inner,
                string throwPath)
            {
                _inner = inner;
                _throwPath = throwPath;
            }

            public TextReader ReadFile(string path) => _inner.ReadFile(path);

            public Stream GetFileStream(
                string path,
                FileMode mode,
                System.IO.FileAccess access,
                FileShare share) =>
                _inner.GetFileStream(path, mode, access, share);

            public string ReadFileAllText(string path) =>
                _inner.ReadFileAllText(path);

            public byte[] ReadFileAllBytes(string path) =>
                _inner.ReadFileAllBytes(path);

            public IEnumerable<string> EnumerateFiles(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
                _inner.EnumerateFiles(path, searchPattern, searchOption);

            public IEnumerable<string> EnumerateDirectories(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
                _inner.EnumerateDirectories(path, searchPattern, searchOption);

            public IEnumerable<string> EnumerateFileSystemEntries(
                string path,
                string searchPattern = "*",
                SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
                _inner.EnumerateFileSystemEntries(path, searchPattern, searchOption);

            public FileAttributes GetAttributes(string path)
            {
                if (FileUtilities.PathsEqual(path, _throwPath))
                {
                    throw new UnauthorizedAccessException("Injected attribute failure.");
                }

                return _inner.GetAttributes(path);
            }

            public DateTime GetLastWriteTimeUtc(string path) =>
                _inner.GetLastWriteTimeUtc(path);

            public bool DirectoryExists(string path) =>
                _inner.DirectoryExists(path);

            public bool FileExists(string path) =>
                _inner.FileExists(path);

            public bool FileOrDirectoryExists(string path) =>
                _inner.FileOrDirectoryExists(path);
        }

        private static EvaluationCategoryObservation[] CreateCompleteCategories(
            params EvaluationObservationCategory[] additionallyObserved)
        {
            var values = (EvaluationObservationCategory[])Enum.GetValues(
                typeof(EvaluationObservationCategory));
            var categories = new EvaluationCategoryObservation[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                EvaluationObservationCategory category = values[i];
                bool observed =
                    category is EvaluationObservationCategory.Request or
                        EvaluationObservationCategory.Completion;
                for (int observedIndex = 0;
                    !observed && observedIndex < additionallyObserved.Length;
                    observedIndex++)
                {
                    observed = additionallyObserved[observedIndex] == category;
                }

                categories[i] = new EvaluationCategoryObservation(
                    category,
                    EvaluationObservationCoverage.Complete,
                    observed
                        ? EvaluationObservationCategoryState.Observed
                        : EvaluationObservationCategoryState.NotExercised);
            }

            return categories;
        }

        private static void ReplaceCategory(
            EvaluationCategoryObservation[] categories,
            EvaluationObservationCategory expectedCategory,
            EvaluationObservationCoverage coverage,
            EvaluationObservationCategoryState state)
        {
            for (int i = 0; i < categories.Length; i++)
            {
                if (categories[i].Category == expectedCategory)
                {
                    categories[i] = new EvaluationCategoryObservation(
                        expectedCategory,
                        coverage,
                        state);
                    return;
                }
            }

            throw new InvalidOperationException(
                $"Category '{expectedCategory}' was not found.");
        }

        private static EvaluationRequestObservation CreateRequest(string projectPath) =>
            new() { ProjectPath = projectPath };

        private static EvaluationProjectSourceObservation CreateProjectSource(
            EvaluationProjectSourceRole role,
            string path,
            EvaluationProjectSourceOutcome outcome =
                EvaluationProjectSourceOutcome.Parsed)
        {
            return new EvaluationProjectSourceObservation(
                role,
                outcome,
                path,
                version: 1,
                contentHash: "hash",
                EvaluationContentHashKind.RawBytes,
                encoding: null,
                provider: null,
                hasLastWriteTimeUtc: true,
                File.GetLastWriteTimeUtc(path).Ticks,
                timestampWasStableDuringRead: true);
        }

        private static EvaluationObservationReport CreateReport(
            bool evaluationSucceeded,
            EvaluationObservationReason reasons,
            EvaluationCategoryObservation[] categories,
            string projectPath = null,
            EvaluationProjectSourceObservation[] projectSources = null,
            EvaluationRequestObservation request = null,
            EvaluationFilesystemTimestampObservation[] filesystemTimestamps = null,
            int schemaVersion = EvaluationObservationSession.ObservationSchemaVersion,
            int propertyFunctionClassificationVersion =
                EvaluationObservationSession.PropertyFunctionClassificationVersion)
        {
            return new EvaluationObservationReport(
                evaluationId: 1,
                projectPath,
                evaluationSucceeded,
                reasons,
                schemaVersion,
                propertyFunctionClassificationVersion,
                categories,
                request,
                projectSources: projectSources ?? [],
                filesystemTimestamps: filesystemTimestamps ?? [],
                pathProbes: [],
                directoryEnumerations: [],
                metadataReads: [],
                fileReads: [],
                globs: [],
                searches: [],
                environment: [],
                externalInputs: [],
                propertyFunctions: [],
                sdkResolutions: [],
                taskRegistrations: [],
                sideEffects: [],
                operationFailures: []);
        }

        private void WriteCaptureFailure(
            EvaluationFilesystemTimestampCaptureResult capture,
            EvaluationObservationReport report)
        {
            if (capture.Status is not (EvaluationFilesystemTimestampCaptureStatus.Success or
                EvaluationFilesystemTimestampCaptureStatus.AnalysisOnly))
            {
                _output.WriteLine(
                    $"Capture={capture.Status}; Failure={capture.Failure}; Path={capture.Path}; Exception={capture.ExceptionType}; HResult={capture.HResult}; ReparseProbes={capture.ReparsePointProbeCount}; TimestampReads={capture.TimestampReadCount}");
                _output.WriteLine($"Reasons={report.Reasons}");
                foreach (EvaluationCategoryObservation category in report.Categories)
                {
                    if (category.State is EvaluationObservationCategoryState.Incomplete or
                        EvaluationObservationCategoryState.Unsupported)
                    {
                        _output.WriteLine($"Category={category.Category}:{category.State}");
                    }
                }
            }
        }
    }
}
