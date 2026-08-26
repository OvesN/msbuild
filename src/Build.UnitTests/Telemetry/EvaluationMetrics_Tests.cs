// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Engine.UnitTests.BackEnd;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Execution;
using Microsoft.Build.Framework;
using Microsoft.Build.TelemetryInfra;
using Microsoft.Build.UnitTests;
using Shouldly;
using Xunit;

namespace Microsoft.Build.Engine.UnitTests;

[CollectionDefinition(CollectionName, DisableParallelization = true)]
public sealed class EvaluationMetricsTestCollection
{
    public const string CollectionName = nameof(EvaluationMetricsTestCollection);
}

[Collection(EvaluationMetricsTestCollection.CollectionName)]
public sealed class EvaluationMetrics_Tests
{
    private readonly ITestOutputHelper _output;

    public EvaluationMetrics_Tests(ITestOutputHelper output)
    {
        _output = output;
        EvaluationMetrics.ResetForTests();
        EvaluationMetrics.IncludeSubmissionIdOverrideForTests = true;
    }

    [Theory]
    [InlineData(ProjectEvaluationStage.Properties, "properties")]
    [InlineData(ProjectEvaluationStage.ItemDefinitions, "item_definitions")]
    [InlineData(ProjectEvaluationStage.Items, "items")]
    [InlineData(ProjectEvaluationStage.UsingTasks, "using_tasks")]
    [InlineData(ProjectEvaluationStage.Full, "full")]
    public void EvaluationMetricsCaptureStageAndDuration(ProjectEvaluationStage stage, string expectedStage)
    {
        using MetricCollector collector = new();
        using EventSourceTestHelper eventSourceListener = new();
        using ProjectCollection collection = new();

        _ = ProjectInstance.FromProjectRootElement(
            CreateRootElement("<Project />"),
            new ProjectOptions
            {
                EvaluationStage = stage,
                ProjectCollection = collection,
            });

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationCountName &&
            measurement.Value == 1 &&
            measurement.HasTag(EvaluationMetrics.StageTagName, expectedStage) &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.OutsideBuildSubmissionOrigin) &&
            measurement.HasTag(EvaluationMetrics.SubmissionIdTagName, BuildEventContext.InvalidSubmissionId) &&
            measurement.HasTag(EvaluationMetrics.SucceededTagName, true));

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationDurationName &&
            measurement.Value >= 0 &&
            measurement.HasTag(EvaluationMetrics.StageTagName, expectedStage) &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.OutsideBuildSubmissionOrigin) &&
            measurement.HasTag(EvaluationMetrics.SubmissionIdTagName, BuildEventContext.InvalidSubmissionId) &&
            measurement.HasTag(EvaluationMetrics.SucceededTagName, true));

        string[] expectedPasses = GetExpectedPasses(stage);
        List<string> metricPasses = [];
        foreach (MetricMeasurement measurement in collector.Measurements)
        {
            if (measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationPassDurationName)
            {
                measurement.Value.ShouldBeGreaterThanOrEqualTo(0);
                measurement.HasTag(EvaluationMetrics.StageTagName, expectedStage).ShouldBeTrue();
                measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.OutsideBuildSubmissionOrigin).ShouldBeTrue();
                measurement.HasTag(EvaluationMetrics.SubmissionIdTagName, BuildEventContext.InvalidSubmissionId).ShouldBeTrue();
                metricPasses.Add(measurement.Tags[EvaluationMetrics.PassTagName].ShouldBeOfType<string>());
            }
        }

        List<string> eventSourcePasses = [];
        foreach (EventWrittenEventArgs eventData in eventSourceListener.GetEvents())
        {
            string? pass = eventData.EventId switch
            {
                14 => "initial_properties",
                16 => "properties",
                18 => "item_definitions",
                20 => "items",
                22 => "using_tasks",
                24 => "targets",
                _ => null,
            };

            if (pass is not null)
            {
                eventSourcePasses.Add(pass);
            }
        }

        metricPasses.ShouldBe(expectedPasses);
        eventSourcePasses.ShouldBe(expectedPasses);
        metricPasses.ShouldBe(eventSourcePasses);
    }

    [Fact]
    public void EvaluationMetricsCaptureBuildSubmissionOrigin()
    {
        using MetricCollector collector = new();
        using TestEnvironment env = TestEnvironment.Create(_output);

        env.CreateFile("evaluation-metrics.cs", string.Empty);
        TransientTestFile buildProject = env.CreateFile(
            "evaluation-metrics.proj",
            """
            <Project>
              <ItemGroup>
                <Compile Include="*.cs" />
              </ItemGroup>
              <Target Name="Build" />
            </Project>
            """);
        MockLogger logger = new(_output);
        using (BuildManager buildManager = new())
        {
            BuildResult result = buildManager.Build(
                new BuildParameters { Loggers = [logger] },
                new BuildRequestData(
                    buildProject.Path,
                    new Dictionary<string, string?>(),
                    null,
                    ["Build"],
                    null));
            result.ShouldHaveSucceeded();
        }

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationCountName &&
            measurement.HasTag(EvaluationMetrics.StageTagName, "full") &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.BuildSubmissionOrigin) &&
            measurement.Tags[EvaluationMetrics.SubmissionIdTagName].ShouldBeOfType<int>() >= 0 &&
            measurement.HasTag(EvaluationMetrics.SucceededTagName, true));

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationPassDurationName &&
            measurement.HasTag(EvaluationMetrics.PassTagName, "targets") &&
            measurement.HasTag(EvaluationMetrics.StageTagName, "full") &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.BuildSubmissionOrigin) &&
            measurement.Tags[EvaluationMetrics.SubmissionIdTagName].ShouldBeOfType<int>() >= 0);

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobRequestCountName &&
            measurement.Value == 1 &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.BuildSubmissionOrigin) &&
            measurement.HasTag(EvaluationMetrics.RecursiveTagName, false) &&
            measurement.Tags[EvaluationMetrics.SubmissionIdTagName].ShouldBeOfType<int>() >= 0);

        int[] submissionIds = collector.Measurements
            .Where(measurement =>
                measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.BuildSubmissionOrigin))
            .Select(measurement => measurement.Tags[EvaluationMetrics.SubmissionIdTagName].ShouldBeOfType<int>())
            .Distinct()
            .ToArray();
        submissionIds.Length.ShouldBe(1);
    }

    [Fact]
    public void EvaluationMetricsDistinguishBuildSubmissions()
    {
        using MetricCollector collector = new();
        using TestEnvironment env = TestEnvironment.Create(_output);

        TransientTestFile firstProject = env.CreateFile(
            "evaluation-metrics-first.proj",
            """
            <Project>
              <Target Name="Build" />
            </Project>
            """);
        TransientTestFile secondProject = env.CreateFile(
            "evaluation-metrics-second.proj",
            """
            <Project>
              <Target Name="Build" />
            </Project>
            """);
        MockLogger logger = new(_output);

        foreach (string projectPath in new[] { firstProject.Path, secondProject.Path })
        {
            using BuildManager buildManager = new();
            BuildResult result = buildManager.Build(
                new BuildParameters { Loggers = [logger] },
                new BuildRequestData(
                    projectPath,
                    new Dictionary<string, string?>(),
                    null,
                    ["Build"],
                    null));
            result.ShouldHaveSucceeded();
        }

        int[] submissionIds = collector.Measurements
            .Where(measurement =>
                measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationCountName &&
                measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.BuildSubmissionOrigin))
            .Select(measurement => measurement.Tags[EvaluationMetrics.SubmissionIdTagName].ShouldBeOfType<int>())
            .Distinct()
            .ToArray();

        submissionIds.Length.ShouldBe(2);
        submissionIds.ShouldAllBe(submissionId => submissionId >= 0);
        submissionIds[1].ShouldBeGreaterThan(submissionIds[0]);
    }

    [Fact]
    public void EvaluationMetricsCaptureFailedEvaluation()
    {
        using MetricCollector collector = new();
        using ProjectCollection collection = new();

        Should.Throw<InvalidProjectFileException>(() =>
            ProjectInstance.FromProjectRootElement(
                CreateRootElement(
                    """
                    <Project>
                      <PropertyGroup Condition="'invalid' ==">
                        <Value>1</Value>
                      </PropertyGroup>
                    </Project>
                    """),
                new ProjectOptions { ProjectCollection = collection }));

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationCountName &&
            measurement.HasTag(EvaluationMetrics.StageTagName, "full") &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.OutsideBuildSubmissionOrigin) &&
            measurement.HasTag(EvaluationMetrics.SubmissionIdTagName, BuildEventContext.InvalidSubmissionId) &&
            measurement.HasTag(EvaluationMetrics.SucceededTagName, false));
    }

    [Fact]
    public void EvaluationMetricsCaptureItemGlobShapeAndDuration()
    {
        using MetricCollector collector = new();
        using TestEnvironment env = TestEnvironment.Create(_output);
        using ProjectCollection collection = new();

        TransientTestFolder sourceDirectory = env.DefaultTestDirectory.CreateDirectory("src");
        sourceDirectory.CreateFile("one.cs", string.Empty);
        sourceDirectory.CreateFile("two.cs", string.Empty);
        sourceDirectory.CreateDirectory("generated").CreateFile("generated.cs", string.Empty);
        sourceDirectory.CreateDirectory("temp").CreateFile("temporary.cs", string.Empty);
        TransientTestFile projectFile = env.CreateFile(
            "evaluation-item-glob-metrics.proj",
            """
            <Project>
              <ItemGroup>
                <Compile Include="src/**/*.cs" Exclude="src/generated/**;src/temp/**" />
                <None Include="literal.txt" />
              </ItemGroup>
            </Project>
            """);

        ProjectInstance project = ProjectInstance.FromFile(
            projectFile.Path,
            new ProjectOptions { ProjectCollection = collection });

        project.GetItems("Compile").Count.ShouldBe(2);

        MetricMeasurement[] globMeasurements = collector.Measurements
            .Where(measurement => measurement.InstrumentName is
                EvaluationMetrics.ItemGlobRequestCountName or
                EvaluationMetrics.ItemGlobDurationName or
                EvaluationMetrics.ItemGlobFileCountName or
                EvaluationMetrics.ItemGlobExcludeCountName or
                EvaluationMetrics.ItemGlobConcurrencyName)
            .ToArray();

        globMeasurements.Length.ShouldBe(5);
        globMeasurements.ShouldAllBe(measurement =>
            measurement.HasTag(EvaluationMetrics.StageTagName, "full") &&
            measurement.HasTag(EvaluationMetrics.OriginTagName, EvaluationMetrics.OutsideBuildSubmissionOrigin) &&
            measurement.HasTag(EvaluationMetrics.SubmissionIdTagName, BuildEventContext.InvalidSubmissionId) &&
            measurement.HasTag(EvaluationMetrics.RecursiveTagName, true));

        globMeasurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobRequestCountName &&
            measurement.Value == 1);
        globMeasurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobDurationName &&
            measurement.Value >= 0);
        globMeasurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobFileCountName &&
            measurement.Value == 2);
        globMeasurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobExcludeCountName &&
            measurement.Value == 2);
        globMeasurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobConcurrencyName &&
            measurement.Value == 1);
    }

    [Fact]
    public void EvaluationDurationDoesNotIncludeMetricsListenerTime()
    {
        using MeterListener listener = new();
        double? recordedDuration = null;
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EvaluationMetrics.MeterName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, _, _, _) =>
        {
            if (instrument.Name == EvaluationMetrics.ProjectEvaluationCountName)
            {
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        });
        listener.SetMeasurementEventCallback<double>((instrument, value, _, _) =>
        {
            if (instrument.Name == EvaluationMetrics.ProjectEvaluationDurationName)
            {
                recordedDuration = value;
            }
        });
        listener.Start();

        long startTimestamp = EvaluationMetrics.EvaluateStart();
        EvaluationMetrics.EvaluateStop(
            startTimestamp,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            succeeded: true);

        recordedDuration.ShouldNotBeNull();
        recordedDuration.Value.ShouldBeLessThan(0.5);
    }

    [Fact]
    public void ItemGlobConcurrencyCapturesOverlappingExpansions()
    {
        using MetricCollector collector = new(EvaluationMetrics.ItemGlobConcurrencyName);

        EvaluationMetrics.ItemGlobMetricState first = EvaluationMetrics.ItemGlobStart();
        EvaluationMetrics.ItemGlobMetricState second = EvaluationMetrics.ItemGlobStart();

        EvaluationMetrics.ItemGlobStop(
            second,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            recursive: true,
            excludeCount: 0,
            fileCount: 1);
        EvaluationMetrics.ItemGlobStop(
            first,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            recursive: true,
            excludeCount: 0,
            fileCount: 1);

        double[] concurrency = collector.Measurements
            .Where(measurement => measurement.InstrumentName == EvaluationMetrics.ItemGlobConcurrencyName)
            .Select(measurement => measurement.Value)
            .OrderBy(value => value)
            .ToArray();

        concurrency.ShouldBe([1, 2]);
    }

    [Fact]
    public void ItemGlobDurationCanBeEnabledIndependently()
    {
        using MetricCollector collector = new(EvaluationMetrics.ItemGlobDurationName);

        EvaluationMetrics.ItemGlobMetricState state = EvaluationMetrics.ItemGlobStart();
        EvaluationMetrics.ItemGlobStop(
            state,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            recursive: false,
            excludeCount: 0,
            fileCount: 1);

        collector.Measurements.ShouldHaveSingleItem()
            .InstrumentName.ShouldBe(EvaluationMetrics.ItemGlobDurationName);
    }

    [Fact]
    public void CancelledItemGlobDoesNotEmitMeasurementsOrLeakConcurrency()
    {
        using MetricCollector collector = new();

        EvaluationMetrics.ItemGlobMetricState cancelled = EvaluationMetrics.ItemGlobStart();
        EvaluationMetrics.ItemGlobCancel(cancelled);

        EvaluationMetrics.ItemGlobMetricState completed = EvaluationMetrics.ItemGlobStart();
        EvaluationMetrics.ItemGlobStop(
            completed,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            recursive: true,
            excludeCount: 0,
            fileCount: 1);

        collector.Measurements.Count(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobRequestCountName).ShouldBe(1);
        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ItemGlobConcurrencyName &&
            measurement.Value == 1);
    }

    [Fact]
    public void SubmissionIdTagIsOptIn()
    {
        EvaluationMetrics.IncludeSubmissionIdOverrideForTests = false;
        try
        {
            using MetricCollector collector = new();
            using ProjectCollection collection = new();

            _ = ProjectInstance.FromProjectRootElement(
                CreateRootElement("<Project />"),
                new ProjectOptions { ProjectCollection = collection });

            EvaluationMetrics.ItemGlobMetricState state = EvaluationMetrics.ItemGlobStart();
            EvaluationMetrics.ItemGlobStop(
                state,
                ProjectEvaluationStage.Full,
                BuildEventContext.InvalidSubmissionId,
                recursive: false,
                excludeCount: 0,
                fileCount: 1);

            collector.Measurements.ShouldAllBe(measurement =>
                !measurement.Tags.ContainsKey(EvaluationMetrics.SubmissionIdTagName));
        }
        finally
        {
            EvaluationMetrics.IncludeSubmissionIdOverrideForTests = true;
        }
    }

    [Fact]
    public void BuildManagerSubmissionIdsRemainPerManagerWithoutMetricsCorrelation()
    {
        EvaluationMetrics.IncludeSubmissionIdOverrideForTests = false;
        try
        {
            using TestEnvironment env = TestEnvironment.Create(_output);
            TransientTestFile project = env.CreateFile(
                "evaluation-metrics-per-manager.proj",
                """
                <Project>
                  <Target Name="Build" />
                </Project>
                """);
            MockLogger logger = new(_output);
            List<int> submissionIds = [];

            for (int i = 0; i < 2; i++)
            {
                using BuildManager buildManager = new();
                BuildResult result = buildManager.Build(
                    new BuildParameters { Loggers = [logger] },
                    new BuildRequestData(
                        project.Path,
                        new Dictionary<string, string?>(),
                        null,
                        ["Build"],
                        null));
                result.ShouldHaveSucceeded();
                submissionIds.Add(result.SubmissionId);
            }

            submissionIds.ShouldBe([0, 0]);
        }
        finally
        {
            EvaluationMetrics.IncludeSubmissionIdOverrideForTests = true;
        }
    }

    [Fact]
    public void ThrowingMetricsListenerDoesNotBreakEvaluation()
    {
        using ResetMetricsOnDispose reset = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EvaluationMetrics.MeterName &&
                instrument.Name == EvaluationMetrics.ProjectEvaluationCountName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("Test listener failure"));
        listener.Start();

        using ProjectCollection collection = new();
        Should.NotThrow(() =>
            ProjectInstance.FromProjectRootElement(
                CreateRootElement("<Project />"),
                new ProjectOptions { ProjectCollection = collection }));
    }

    [Fact]
    public void ThrowingPassMetricsListenerDoesNotBreakEvaluation()
    {
        using ResetMetricsOnDispose reset = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EvaluationMetrics.MeterName &&
                instrument.Name == EvaluationMetrics.ProjectEvaluationPassDurationName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<double>((_, _, _, _) => throw new InvalidOperationException("Test listener failure"));
        listener.Start();

        using ProjectCollection collection = new();
        Should.NotThrow(() =>
            ProjectInstance.FromProjectRootElement(
                CreateRootElement("<Project />"),
                new ProjectOptions { ProjectCollection = collection }));
    }

    [Fact]
    public void ThrowingItemGlobMetricsListenerDoesNotBreakEvaluation()
    {
        using ResetMetricsOnDispose reset = new();
        using MeterListener listener = new();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == EvaluationMetrics.MeterName &&
                instrument.Name == EvaluationMetrics.ItemGlobRequestCountName)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, _, _) => throw new InvalidOperationException("Test listener failure"));
        listener.Start();

        EvaluationMetrics.ItemGlobMetricState state = EvaluationMetrics.ItemGlobStart();
        Should.NotThrow(() =>
            EvaluationMetrics.ItemGlobStop(
                state,
                ProjectEvaluationStage.Full,
                BuildEventContext.InvalidSubmissionId,
                recursive: true,
                excludeCount: 0,
                fileCount: 1));

        using MetricCollector collector = new();
        long startTimestamp = EvaluationMetrics.EvaluateStart();
        EvaluationMetrics.EvaluateStop(
            startTimestamp,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            succeeded: true);

        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationCountName);
        collector.Measurements.ShouldContain(measurement =>
            measurement.InstrumentName == EvaluationMetrics.ProjectEvaluationDurationName);

        EvaluationMetrics.ItemGlobMetricState nextState = EvaluationMetrics.ItemGlobStart();
        EvaluationMetrics.ItemGlobStop(
            nextState,
            ProjectEvaluationStage.Full,
            BuildEventContext.InvalidSubmissionId,
            recursive: true,
            excludeCount: 0,
            fileCount: 1);

        collector.Measurements.ShouldNotContain(measurement =>
            measurement.InstrumentName.StartsWith("msbuild.project.evaluation.item.glob", StringComparison.Ordinal));
    }

    private static string[] GetExpectedPasses(ProjectEvaluationStage stage) => stage switch
    {
        ProjectEvaluationStage.Properties => ["initial_properties", "properties"],
        ProjectEvaluationStage.ItemDefinitions => ["initial_properties", "properties", "item_definitions"],
        ProjectEvaluationStage.Items => ["initial_properties", "properties", "item_definitions", "items"],
        ProjectEvaluationStage.UsingTasks => ["initial_properties", "properties", "item_definitions", "items", "using_tasks"],
        ProjectEvaluationStage.Full => ["initial_properties", "properties", "item_definitions", "items", "using_tasks", "targets"],
        _ => [],
    };

    private static ProjectRootElement CreateRootElement(string projectXml)
    {
        using StringReader stringReader = new(projectXml);
        using XmlReader xmlReader = XmlReader.Create(stringReader);
        return ProjectRootElement.Create(xmlReader);
    }

    private sealed class MetricCollector : IDisposable
    {
        private readonly MeterListener _listener = new();

        public MetricCollector(params string[] enabledInstruments)
        {
            HashSet<string>? enabledInstrumentSet = enabledInstruments.Length == 0
                ? null
                : new HashSet<string>(enabledInstruments, StringComparer.Ordinal);
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EvaluationMetrics.MeterName &&
                    (enabledInstrumentSet is null || enabledInstrumentSet.Contains(instrument.Name)))
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Add(instrument, value, tags));
            _listener.Start();
        }

        public ConcurrentQueue<MetricMeasurement> Measurements { get; } = new();

        public void Dispose()
        {
            _listener.Dispose();
        }

        private void Add<T>(
            Instrument instrument,
            T value,
            ReadOnlySpan<KeyValuePair<string, object?>> tags)
            where T : struct
        {
            Dictionary<string, object?> copiedTags = new(tags.Length, StringComparer.Ordinal);
            foreach (KeyValuePair<string, object?> tag in tags)
            {
                copiedTags.Add(tag.Key, tag.Value);
            }

            Measurements.Enqueue(new MetricMeasurement(
                instrument.Name,
                Convert.ToDouble(value, System.Globalization.CultureInfo.InvariantCulture),
                copiedTags));
        }
    }

    private sealed record MetricMeasurement(
        string InstrumentName,
        double Value,
        Dictionary<string, object?> Tags)
    {
        public bool HasTag(string name, object expected) =>
            Tags.TryGetValue(name, out object? actual) && Equals(actual, expected);
    }

    private sealed class ResetMetricsOnDispose : IDisposable
    {
        public void Dispose()
        {
            EvaluationMetrics.ResetForTests();
        }
    }
}
