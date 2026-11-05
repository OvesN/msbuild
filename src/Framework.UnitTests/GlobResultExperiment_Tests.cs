// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
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
        GlobResultExperiment.SnapshotValidator unavailableEntries = (_, _, _) =>
        {
            listingCalls++;
            throw failure;
        };

        experiment.TryGet(Key, unavailableEntries, _ => true, out string[] files).ShouldBeFalse();
        files.ShouldBeEmpty();
        listingCalls.ShouldBe(1);

        experiment.TryGet(Key, unavailableEntries, _ => true, out files).ShouldBeFalse();
        listingCalls.ShouldBe(1, "The invalid glob must be evicted instead of probing it again.");
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
            experiment.TryGet(Key, (_, _, _) => throw failure, _ => true, out _);
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
        experiment.TryGet(Key, (_, _, _) => DirectorySnapshotStatus.Valid, _ => false, out _).ShouldBeFalse();
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
    public static bool LinuxTmpfsAvailable =>
        Microsoft.Build.Framework.NativeMethods.IsLinux && Environment.OSVersion.Version >= new Version(5, 8) && Directory.Exists("/dev/shm");

    [Fact(Skip = "Requires Linux 5.8 or newer with /dev/shm available.", SkipType = typeof(GlobResultExperimentTest), SkipUnless = nameof(LinuxTmpfsAvailable))]
    public void LinuxTmpfsProducesAnIdentityAndChangeTimeStamp()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder(Path.Combine("/dev/shm", "msbuild-glob-" + Guid.NewGuid().ToString("N"))).Path;
        DirectoryChangeTime.TryRead(root, out DirectoryChangeStamp stamp).ShouldBeTrue();
        stamp.IdentityLow.ShouldNotBe(0ul);
    }

    [LinuxOnlyFact]
    public void UnsupportedLinuxFilesystemDoesNotProduceAReusableStamp()
    {
        DirectoryChangeTime.TryRead("/proc", out _).ShouldBeFalse();
        var cache = new DirectoryListingCache();
        cache.TryReadDirectoryChangeTime("/proc", out _).ShouldBeFalse();
        cache.CanAttemptSharing("/proc").ShouldBeFalse();
        cache.Clear();
        cache.CanAttemptSharing("/proc").ShouldBeTrue();
    }
#endif

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void MissingProjectDirectoryPreservesUncachedGlobBehavior(string? projectDirectory)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        env.SetCurrentDirectory(root);
        File.WriteAllText(Path.Combine(root, "Old.cs"), "");
        var source = new StampSource();
        var matcher = new FileMatcher(FileSystems.Default, new ConcurrentDictionary<string, IReadOnlyList<string>>(),
            directoryListingCache: new DirectoryListingCache(source.Read));
        var expected = new FileMatcher(FileSystems.Default).GetFiles(projectDirectory, "*.cs");
        var actual = matcher.GetFiles(projectDirectory, "*.cs");
        actual.FileList.ShouldBe(expected.FileList);
        actual.Action.ShouldBe(expected.Action);
    }

    [WindowsOnlyTheory]
    [InlineData(@"\\bad|server\share\*.cs")]
    [InlineData(@"\\bad<server\share\*.cs")]
    [InlineData("bad\0*.cs")]
    public void InvalidUncGlobPreservesTheReturnedFileSpecAndAction(string filespec)
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        var source = new StampSource();
        var matcher = new FileMatcher(FileSystems.Default, new ConcurrentDictionary<string, IReadOnlyList<string>>(),
            directoryListingCache: new DirectoryListingCache(source.Read));
        var expected = new FileMatcher(FileSystems.Default).GetFiles(root, filespec);
        var actual = matcher.GetFiles(root, filespec);
        actual.FileList.ShouldBe(expected.FileList);
        actual.Action.ShouldBe(expected.Action);
    }

    [Fact]
    public void ExcludedRootGlobsAvoidStampReadsButPreserveEvaluationLocalReuse()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        env.SetEnvironmentVariable("MSBUILDDIRECTORYCACHEEXCLUDEROOTS", root);
        File.WriteAllText(Path.Combine(root, "Old.cs"), "");
        var source = new StampSource();
        var cache = new DirectoryListingCache(source.Read);
        FileMatcher Matcher() => new(FileSystems.Default, new ConcurrentDictionary<string, IReadOnlyList<string>>(), directoryListingCache: cache);
        FileMatcher first = Matcher();
        first.GetFiles(root, "*.cs").FileList.Length.ShouldBe(1);
        File.WriteAllText(Path.Combine(root, "New.cs"), "");
        first.GetFiles(root, "*.cs").FileList.Length.ShouldBe(1);
        Matcher().GetFiles(root, "*.cs").FileList.Length.ShouldBe(2);
        source.Reads.ShouldBe(0);
    }

    [Fact]
    public void IoFailureRetainsASharedGlobWhoseSnapshotStillExists()
    {
        var source = new StampSource();
        var cache = new DirectoryListingCache(source.Read);
        source.Read(DirectoryPath, out var stamp);
        IReadOnlyList<string> entries = cache.Store(DirectoryPath, FileMatcher.FileSystemEntity.Files, ["Old.cs"], stamp);
        GlobResultExperiment experiment = GlobResultExperiment.Create(cache)!;
        var collector = new GlobResultExperiment.Collector();
        experiment.RecordListing(collector, DirectoryPath, FileMatcher.FileSystemEntity.Files, entries);
        experiment.Store(Key, ["Old.cs"], FileMatcher.SearchAction.RunSearch, null, collector);
        experiment.TryGet(Key, (_, _, _) => throw new IOException("Temporary validation failure."), _ => true, out _).ShouldBeFalse();
        experiment.TryGet(Key, (_, _, _) => DirectorySnapshotStatus.Valid, _ => true, out string[] files).ShouldBeTrue();
        files.ShouldBe(["Old.cs"]);
    }

    [Fact]
    public void RealGlobsRetainSnapshotsDuringTemporaryDeferralAndInvalidateChangedOrMissingDirectories()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(root, "Old.cs"), "");
        var source = new StampSource();
        var cache = new DirectoryListingCache(source.Read);
        int enumerations = 0;
        IReadOnlyList<string> Enumerate(FileMatcher.FileSystemEntity kind, string path, out bool succeeded)
        {
            enumerations++;
            succeeded = Directory.Exists(path);
            if (!succeeded) { return []; }
            return kind switch
            {
                FileMatcher.FileSystemEntity.Files => Directory.GetFiles(path),
                FileMatcher.FileSystemEntity.Directories => Directory.GetDirectories(path),
                _ => Directory.GetFileSystemEntries(path),
            };
        }
        FileMatcher Matcher() => new(FileSystems.Default,
            (kind, path, _, _, _) => Enumerate(kind, path, out _),
            new ConcurrentDictionary<string, IReadOnlyList<string>>(),
            directoryListingCache: cache, getFileSystemEntriesWithStatus: Enumerate);
        string[] Query() => Matcher().GetFiles(root, "*.cs").FileList.Select(Path.GetFileName).OrderBy(name => name).ToArray()!;

        Query().ShouldBe(["Old.cs"]);
        cache.TryGet(root, FileMatcher.FileSystemEntity.Files, out var snapshot).ShouldBeTrue();
        int firstEnumerations = enumerations;
        long hits = GlobResultExperiment.GetStatistics()[1];
        int reads = source.Reads;
        Query().ShouldBe(["Old.cs"]);
        enumerations.ShouldBe(firstEnumerations);
        source.Reads.ShouldBe(reads + 1, "A shared glob validates its directory only once per evaluation.");
        GlobResultExperiment.GetStatistics()[1].ShouldBeGreaterThan(hits);

        source.Status = DirectoryStampStatus.Unavailable;
        File.WriteAllText(Path.Combine(root, "New.cs"), "");
        Query().ShouldBe(["New.cs", "Old.cs"]);
        enumerations.ShouldBeGreaterThan(firstEnumerations);
        cache.IsSnapshot(root, FileMatcher.FileSystemEntity.Files, snapshot!).ShouldBeTrue();
        File.Delete(Path.Combine(root, "New.cs"));
        source.Status = DirectoryStampStatus.Available;
        int deferredEnumerations = enumerations;
        hits = GlobResultExperiment.GetStatistics()[1];
        Query().ShouldBe(["Old.cs"]);
        enumerations.ShouldBe(deferredEnumerations);
        GlobResultExperiment.GetStatistics()[1].ShouldBeGreaterThan(hits, "Temporary deferral must retain the old shared glob.");

        File.WriteAllText(Path.Combine(root, "New.cs"), "");
        source.Version++;
        Query().ShouldBe(["New.cs", "Old.cs"]);
        cache.IsSnapshot(root, FileMatcher.FileSystemEntity.Files, snapshot!).ShouldBeFalse();

        Directory.Delete(root, recursive: true);
        source.Status = DirectoryStampStatus.Missing;
        Query().ShouldBeEmpty();
        Query().ShouldBeEmpty();
    }

    [Fact]
    public void SnapshotValidationPrefillsTheEvaluationLocalDirectoryCache()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        File.WriteAllText(Path.Combine(root, "Old.cs"), "");
        var source = new StampSource();
        var cache = new DirectoryListingCache(source.Read);
        FileMatcher Matcher() => new(FileSystems.Default,
            new ConcurrentDictionary<string, IReadOnlyList<string>>(), directoryListingCache: cache);
        FileMatcher first = Matcher();
        first.GetFiles(root, "*.cs").FileList.Length.ShouldBe(1);
        first.GetFiles(root, "O*.cs").FileList.Length.ShouldBe(1);

        FileMatcher second = Matcher();
        second.GetFiles(root, "*.cs").FileList.Length.ShouldBe(1);
        int reads = source.Reads;
        second.GetFiles(root, "O*.cs").FileList.Length.ShouldBe(1);
        source.Reads.ShouldBe(reads);
    }

    private sealed class StampSource
    {
        internal DirectoryStampStatus Status = DirectoryStampStatus.Available;
        internal long Version = 1;
        internal int Reads;

        internal DirectoryStampStatus Read(string path, out DirectoryChangeStamp stamp)
        {
            Reads++;
            stamp = new(1, 2, 3, Version);
            return Status;
        }
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

        experiment.TryGet(Key, (_, _, _) => DirectorySnapshotStatus.Valid, _ => true, out string[] files).ShouldBeTrue();
        files.ShouldBeSameAs(entries);
        return experiment;
    }
}
