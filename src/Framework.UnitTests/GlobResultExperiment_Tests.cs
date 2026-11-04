// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;

namespace Microsoft.Build.UnitTests;

public class GlobResultExperimentTest(ITestOutputHelper output)
{
    private const string Key = "glob";
    private const string DirectoryPath = "scope";
    private readonly ITestOutputHelper _output = output;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingDirectoryInvalidatesCachedGlob(bool aggregateException)
    {
        GlobResultExperiment experiment = CreateCachedGlob();
        Exception failure = new DirectoryNotFoundException("The cached dependency disappeared.");
        if (aggregateException)
        {
            failure = new AggregateException(failure);
        }

        int listingCalls = 0;
        FileMatcher.GetFileSystemEntries unavailableEntries = (_, _, _, _, _) =>
        {
            listingCalls++;
            throw failure;
        };

        experiment.TryGet(Key, unavailableEntries, _ => true, out string[] files).ShouldBeFalse();
        files.ShouldBeEmpty();
        listingCalls.ShouldBe(1);

        experiment.TryGet(Key, unavailableEntries, _ => true, out files).ShouldBeFalse();
        listingCalls.ShouldBe(1, "The invalid glob must be evicted instead of probing it again.");

        var collector = new GlobResultExperiment.Collector();
        collector.RecordExistence(DirectoryPath, false);
        experiment.Store(Key, [], FileMatcher.SearchAction.ReturnEmptyList, null, collector);

        experiment.TryGet(Key, unavailableEntries, _ => false, out files).ShouldBeTrue();
        files.ShouldBeEmpty();
        listingCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NonIoValidationFailurePropagates(bool aggregateException)
    {
        GlobResultExperiment experiment = CreateCachedGlob();
        Exception failure = new InvalidOperationException("Unexpected validation failure.");
        if (aggregateException)
        {
            failure = new AggregateException(new DirectoryNotFoundException(), failure);
        }

        Exception? actual = Record.Exception(() =>
        {
            experiment.TryGet(Key, (_, _, _, _, _) => throw failure, _ => true, out _);
        });

        actual.ShouldBeSameAs(failure);
    }

    [Fact]
    public void UnsupportedExistenceDependenciesCannotSeedSharedGlobs()
    {
        GlobResultExperiment experiment = GlobResultExperiment.Create(new DirectoryListingCache())!;
        var collector = new GlobResultExperiment.Collector();
        experiment.RecordExistence(collector, "missing", false);
        collector.Admissible.ShouldBeFalse();
        experiment.Store(Key, [], FileMatcher.SearchAction.ReturnEmptyList, null, collector);
        experiment.TryGet(Key, (_, _, _, _, _) => [], _ => false, out _).ShouldBeFalse();
    }

    [Fact]
    public void ExcludedRootsRespectDirectoryBoundariesAndTheOwnerLifetime()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        env.SetEnvironmentVariable("MSBUILDDIRECTORYCACHEEXCLUDEROOTS", root + Path.DirectorySeparatorChar);
        var cache = new DirectoryListingCache();
        cache.IsExcluded(root).ShouldBeTrue();
        cache.IsExcluded(Path.Combine(root, "child")).ShouldBeTrue();
        cache.IsExcluded(root + "-sibling").ShouldBeFalse();

        env.SetEnvironmentVariable("MSBUILDDIRECTORYCACHEEXCLUDEROOTS", null);
        cache.IsExcluded(root).ShouldBeTrue();
        new DirectoryListingCache().IsExcluded(root).ShouldBeFalse();

        env.SetEnvironmentVariable("MSBUILDDIRECTORYCACHEEXCLUDEROOTS", " ");
        new DirectoryListingCache().IsExcluded(root).ShouldBeTrue();
    }

#if NET
    [LinuxOnlyFact]
    public void UnsupportedLinuxFilesystemDoesNotProduceAReusableStamp()
    {
        DirectoryChangeTime.TryRead("/proc", out _).ShouldBeFalse();
    }
#endif

    private static GlobResultExperiment CreateCachedGlob()
    {
        GlobResultExperiment? experiment = GlobResultExperiment.Create(new DirectoryListingCache());
        experiment.ShouldNotBeNull();

        string[] entries = ["Old.cs"];
        var collector = new GlobResultExperiment.Collector();
        collector.Record(
            (DirectoryPath, (int)FileMatcher.FileSystemEntity.Files),
            new GlobResultExperiment.Dependency(DirectoryPath, (int)FileMatcher.FileSystemEntity.Files, entries, true));
        experiment.Store(Key, entries, FileMatcher.SearchAction.RunSearch, null, collector);

        experiment.TryGet(Key, (_, _, _, _, _) => entries, _ => true, out string[] files).ShouldBeTrue();
        files.ShouldBeSameAs(entries);
        return experiment;
    }
}
