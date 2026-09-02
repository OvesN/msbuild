// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Framework;
using Microsoft.Build.Shared;
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
            _env = TestEnvironment.Create(output);
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
            capture.Snapshot.Entries.ShouldContain(entry =>
                (entry.Sources & EvaluationFilesystemTimestampSource.Glob) != 0);

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Valid);
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

            File.WriteAllText(missingFile, "<Project />");

            EvaluationFilesystemTimestampValidationResult validation =
                capture.Snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            FileUtilities.PathsEqual(validation.Path, missingFile).ShouldBeTrue();
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
                        true,
                        EvaluationPathKind.File,
                        EvaluationFilesystemTimestampSource.PathProbe),
                ]);

            EvaluationFilesystemTimestampValidationResult validation =
                snapshot.Validate();

            validation.Status.ShouldBe(EvaluationFilesystemTimestampValidationStatus.Changed);
            validation.ExpectedLastWriteTimeUtcTicks
                .ShouldBe(validation.ActualLastWriteTimeUtcTicks);
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
                filesystemTimestamps: [],
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
                    $"Capture={capture.Status}; Failure={capture.Failure}; Path={capture.Path}; Exception={capture.ExceptionType}; HResult={capture.HResult}");
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
