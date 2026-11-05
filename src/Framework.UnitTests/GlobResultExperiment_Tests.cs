// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.Build.Shared;
using Shouldly;
using Xunit;
using Xunit.NetCore.Extensions;

namespace Microsoft.Build.UnitTests;

public class GlobResultExperimentTest
{
    private const string Key = "glob";
    private const string DirectoryPath = "scope";

    [WindowsOnlyTheory]
    [InlineData(0, 10_499_999, false)]
    [InlineData(0, 10_500_000, true)]
    [InlineData(123_000_000, 9_999, false)]
    [InlineData(123_000_000, 10_000, true)]
    [InlineData(123_456_700, 0, true)]
    [InlineData(123_456_700, -1, false)]
    [InlineData(999_000_000, 9_999, false)]
    [InlineData(999_000_000, 10_000, true)]
    public void CtimeAdmissionUsesBazelTimestampGranularity(int nanoseconds, int elapsedTicks, bool expected)
    {
        DateTime changeTime = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        changeTime = changeTime.AddTicks(nanoseconds / 100);
        var stamp = new DirectoryChangeStamp(0, 0, 0, changeTime.ToFileTimeUtc());

        stamp.IsOldEnoughToCache(changeTime.AddTicks(elapsedTicks)).ShouldBe(expected);
    }

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
