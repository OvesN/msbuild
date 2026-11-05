// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if FEATURE_WINDOWSINTEROP
using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Build.Shared;
using Microsoft.Build.Shared.FileSystem;
using Shouldly;
using Windows.Win32;
using Windows.Win32.System.Ioctl;
using Xunit;

namespace Microsoft.Build.UnitTests;

[SupportedOSPlatform("windows6.1")]
public sealed class DirectoryJournal_Tests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;
    public static bool WindowsSymbolicLinksAvailable =>
        WindowsTemporaryDirectoryAvailable && new RequiresSymbolicLinksFactAttribute().Skip is null;
    public static bool WindowsTemporaryDirectoryAvailable =>
        Microsoft.Build.Framework.NativeMethods.IsWindows && DirectoryJournal.IsSupportedPath(Path.GetTempPath());
    public static bool WindowsJournalAvailable
    {
        get
        {
            if (!WindowsTemporaryDirectoryAvailable) { return false; }
            var journal = new DirectoryJournal();
            try { return journal.TryRead(Path.GetTempPath(), out _); }
            finally { journal.Clear(); }
        }
    }

    [WindowsOnlyFact]
    public void RenameInvalidatesOldAndNewParentsIncludingTheFirstUsn()
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId oldParent = new(10);
        DirectoryJournal.DirectoryId newParent = new(20);
        DirectoryJournal.DirectoryId file = new(30);
        state.Track(@"C:\old", oldParent);
        state.Track(@"C:\new", newParent);
        state.TryGetVersion(@"C:\old", out long oldVersion).ShouldBeTrue();
        state.TryGetVersion(@"C:\new", out long newVersion).ShouldBeTrue();

        state.ApplyBuffer(Buffer(128,
            Entry(0, file, oldParent, PInvoke.USN_REASON_RENAME_OLD_NAME),
            Entry(64, file, newParent, PInvoke.USN_REASON_RENAME_NEW_NAME | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();

        state.TryGetVersion(@"C:\old", out long currentOld).ShouldBeTrue();
        state.TryGetVersion(@"C:\new", out long currentNew).ShouldBeTrue();
        currentOld.ShouldNotBe(oldVersion);
        currentNew.ShouldNotBe(newVersion);
    }

    [WindowsOnlyTheory]
    [InlineData(PInvoke.USN_REASON_HARD_LINK_CHANGE)]
    [InlineData(PInvoke.USN_REASON_SECURITY_CHANGE)]
    [InlineData(PInvoke.USN_REASON_REPARSE_POINT_CHANGE)]
    [InlineData(PInvoke.USN_REASON_RENAME_OLD_NAME)]
    [InlineData(PInvoke.USN_REASON_FILE_DELETE)]
    [InlineData(PInvoke.USN_REASON_FILE_CREATE)]
    public void UnrelatedUnclosedChangesDoNotInvalidateTrackedPaths(uint reason)
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId directory = new(10);
        state.Track(@"C:\fixture", directory);
        state.TryGetVersion(@"C:\fixture", out long before).ShouldBeTrue();
        ulong epoch = state.Epoch;

        state.ApplyBuffer(Buffer(64, Entry(0, new(20), new(30), reason))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out long after).ShouldBeTrue();
        after.ShouldBe(before);
        state.Epoch.ShouldBe(epoch);

        state.ApplyBuffer(Buffer(128, Entry(64, new(20), new(30), reason | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out after).ShouldBeTrue();
        after.ShouldBe(before);
    }

    [WindowsOnlyTheory]
    [InlineData(PInvoke.USN_REASON_FILE_CREATE)]
    [InlineData(PInvoke.USN_REASON_FILE_DELETE)]
    [InlineData(PInvoke.USN_REASON_HARD_LINK_CHANGE)]
    [InlineData(PInvoke.USN_REASON_RENAME_OLD_NAME)]
    [InlineData(PInvoke.USN_REASON_RENAME_NEW_NAME)]
    public void MembershipChangesAffectOnlyEnumeratedParentsWithoutAVolumeWideCloseWait(uint reason)
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId parent = new(10);
        state.Track(@"C:\", new(1), enumerated: false);
        state.Track(@"C:\fixture", parent);
        state.Track(@"C:\fixture\child", new(11));
        state.TryGetVersion(@"C:\fixture", out long before).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture\child", out long childBefore).ShouldBeTrue();

        state.ApplyBuffer(Buffer(64, Entry(0, new(20), new(1), reason))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out long after).ShouldBeTrue();
        after.ShouldBe(before, "The volume root is a container, not an enumerated input.");
        state.ApplyBuffer(Buffer(128, Entry(64, new(20), parent, reason))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out after).ShouldBeTrue();
        after.ShouldNotBe(before);
        state.TryGetVersion(@"C:\fixture\child", out long childAfter).ShouldBeTrue();
        childAfter.ShouldBe(childBefore, "Parent membership changes do not replace the child directory.");
    }

    [WindowsOnlyTheory]
    [InlineData(PInvoke.USN_REASON_FILE_DELETE)]
    [InlineData(PInvoke.USN_REASON_REPARSE_POINT_CHANGE)]
    [InlineData(PInvoke.USN_REASON_DATA_OVERWRITE)]
    [InlineData(PInvoke.USN_REASON_DATA_EXTEND)]
    [InlineData(PInvoke.USN_REASON_DATA_TRUNCATION)]
    [InlineData(PInvoke.USN_REASON_STREAM_CHANGE)]
    [InlineData(PInvoke.USN_REASON_RENAME_OLD_NAME)]
    public void DirectAncestorChangesInvalidateOnlyTheirTrackedSubtree(uint reason)
    {
        var state = CreateState();
        state.Track(@"C:\", new(1), enumerated: false);
        state.Track(@"C:\fixture", new(10), enumerated: false);
        state.Track(@"C:\fixture\child", new(11));
        state.Track(@"C:\unrelated", new(12));
        state.TryGetVersion(@"C:\unrelated", out long before).ShouldBeTrue();
        state.ApplyBuffer(Buffer(64, Entry(0, new(10), new(1), reason))).ShouldBeTrue();
        state.IsAnchored(@"C:\fixture", new(10)).ShouldBeFalse();
        state.TryGetVersion(@"C:\fixture\child", out _).ShouldBeFalse();
        state.TryGetVersion(@"C:\unrelated", out long after).ShouldBeTrue();
        after.ShouldBe(before);
    }

    [WindowsOnlyFact]
    public void SingleLinkChangesAreFilteredByTrackedParentPath()
    {
        var state = CreateState();
        state.Track(@"C:\a", new(10), enumerated: false);
        state.Track(@"C:\b", new(20), enumerated: false);
        state.Track(@"C:\a\child", new(30));
        state.Track(@"C:\b\child", new(30));
        state.TryGetVersion(@"C:\a\child", out long aBefore).ShouldBeTrue();
        state.TryGetVersion(@"C:\b\child", out long bBefore).ShouldBeTrue();

        state.ApplyBuffer(Buffer(64, Entry(0, new(30), new(40), PInvoke.USN_REASON_HARD_LINK_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\a\child", out long aAfter).ShouldBeTrue();
        aAfter.ShouldBe(aBefore, "A link under an untracked parent is irrelevant.");
        state.ApplyBuffer(Buffer(128, Entry(64, new(30), new(10), PInvoke.USN_REASON_RENAME_OLD_NAME))).ShouldBeTrue();
        state.TryGetVersion(@"C:\a\child", out _).ShouldBeFalse();
        state.TryGetVersion(@"C:\b\child", out long bAfter).ShouldBeTrue();
        bAfter.ShouldBe(bBefore);
    }

    [WindowsOnlyFact]
    public void MultipleParentIncarnationsConservativelyAffectAllTrackedLinks()
    {
        var state = CreateState();
        state.Track(@"C:\a", new(10), enumerated: false);
        state.Track(@"C:\b", new(10), enumerated: false);
        state.Track(@"C:\a\child", new(30));
        state.Track(@"C:\b\child", new(30));
        state.ApplyBuffer(Buffer(64, Entry(0, new(30), new(10), PInvoke.USN_REASON_HARD_LINK_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\a\child", out _).ShouldBeFalse();
        state.TryGetVersion(@"C:\b\child", out _).ShouldBeFalse();
    }

    [WindowsOnlyFact]
    public void DirectChangesBeforeOrAtTheTrackedUsnDoNotInvalidateANewerSubscription()
    {
        var state = CreateState();
        state.Track(@"C:\fixture", new(10), usn: 64);
        state.TryGetVersion(@"C:\fixture", out long before).ShouldBeTrue();
        state.ApplyBuffer(Buffer(128,
            Entry(0, new(10), new(1), PInvoke.USN_REASON_REPARSE_POINT_CHANGE),
            Entry(64, new(10), new(1), PInvoke.USN_REASON_REPARSE_POINT_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out long after).ShouldBeTrue();
        after.ShouldBe(before);
        state.ApplyBuffer(Buffer(192, Entry(128, new(10), new(1), PInvoke.USN_REASON_REPARSE_POINT_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out _).ShouldBeFalse();
    }

    [WindowsOnlyFact]
    public void NativeZeroUsnDoesNotHideTheFirstDirectAncestorChange()
    {
        var state = CreateState();
        state.Track(@"C:\fixture", new(10), usn: 0, enumerated: false);
        state.Track(@"C:\fixture\child", new(11), usn: 0);
        state.TryGetVersion(@"C:\fixture\child", out _).ShouldBeTrue();
        state.ApplyBuffer(Buffer(64, Entry(0, new(10), new(1), PInvoke.USN_REASON_REPARSE_POINT_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture\child", out _).ShouldBeFalse();
    }

    [WindowsOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailedQualificationCannotLeaveAnIncompleteAnchorForADescendant(bool failDuringAncestorRegistration)
    {
        const string parent = @"C:\r\p";
        const string child = @"C:\r\p\q";
        var state = CreateState();
        var journal = new DirectoryJournal();
        var reader = new FakeReader { QuerySucceeds = _ => failDuringAncestorRegistration };
        bool failAtRoot = true;
        List<string> visited = [];
        bool ReadAncestor(string path, out DirectoryJournal.Record record)
        {
            visited.Add(path);
            record = path switch
            {
                @"C:\r\p" => new(64, new(30), new(20), 0, 0, 0x10),
                @"C:\r" => new(64, new(20), new(1), 0, 0, 0x10),
                @"C:\" => new(64, new(1), new(1), 0, 0, 0x10),
                _ => throw new InvalidOperationException(path),
            };
            return !failAtRoot || path != @"C:\";
        }
        bool WalkParent(string path) => DirectoryJournal.TrackAncestors(path, new(20), state, ReadAncestor);
        bool WalkChild(string path) => DirectoryJournal.TrackAncestors(path, new(30), state, ReadAncestor);

        journal.Qualify(parent, Guid.Empty, new(30), state, reader, WalkParent, out _, usn: 0)
            .ShouldBe(DirectoryStampStatus.Unavailable);
        state.IsAnchored(parent, new(30)).ShouldBeFalse();
        state.IsAnchored(@"C:\r", new(20)).ShouldBeFalse();

        visited.Clear();
        reader.QuerySucceeds = _ => true;
        failAtRoot = false;
        journal.Qualify(child, Guid.Empty, new(40), state, reader, WalkChild, out _, usn: 0)
            .ShouldBe(DirectoryStampStatus.Available);
        visited.ShouldBe([parent, @"C:\r", @"C:\"]);
        state.IsAnchored(parent, new(30)).ShouldBeTrue();
        state.IsAnchored(child, new(40)).ShouldBeTrue();

        state.ApplyBuffer(Buffer(128, Entry(64, new(20), new(1), PInvoke.USN_REASON_RENAME_OLD_NAME))).ShouldBeTrue();
        state.IsAnchored(child, new(40)).ShouldBeFalse();
        state.TryGetVersion(child, out _).ShouldBeFalse();
    }

    [WindowsOnlyFact]
    public void RetrackingAReplacedPathInvalidatesItsOldDescendantSubscriptions()
    {
        var state = CreateState();
        state.Track(@"C:\fixture", new(10), enumerated: false);
        state.Track(@"C:\fixture\child", new(11));
        state.TryGetVersion(@"C:\fixture\child", out long before).ShouldBeTrue();
        state.Track(@"C:\fixture", new(20), usn: 128, enumerated: false);
        state.TryGetVersion(@"C:\fixture\child", out _).ShouldBeFalse();
        state.Track(@"C:\fixture\child", new(21), usn: 128);
        state.TryGetVersion(@"C:\fixture\child", out long after).ShouldBeTrue();
        after.ShouldNotBe(before);
    }

    [WindowsOnlyFact]
    public void QualificationMemoIgnoresUnrelatedChangesButRechecksAnInvalidatedListing()
    {
        var journal = new DirectoryJournal();
        var state = CreateState();
        var reader = new FakeReader { Head = _ => new() { UsnJournalID = 1, NextUsn = state.Cursor } };
        int walks = 0;
        bool Walk(string _) { walks++; return true; }
        DirectoryStampStatus Qualify() => journal.Qualify(@"C:\fixture", Guid.Empty, new(10), state, reader, Walk, out _);

        Qualify().ShouldBe(DirectoryStampStatus.Available);
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(1);
        state.ApplyBuffer(Buffer(64, Entry(0, new(20), new(30), PInvoke.USN_REASON_SECURITY_CHANGE | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(1);
        state.ApplyBuffer(Buffer(128, Entry(64, new(20), new(10), PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(2);
    }

    [WindowsOnlyFact]
    public void PathInvalidationDuringQualificationCannotSeedTheMemo()
    {
        var journal = new DirectoryJournal();
        var state = CreateState();
        var reader = new FakeReader { Head = _ => new() { UsnJournalID = 1, NextUsn = state.Cursor } };
        int walks = 0;
        bool Walk(string _)
        {
            if (++walks == 1)
            {
                state.ApplyBuffer(Buffer(64, Entry(0, new(10), new(30),
                    PInvoke.USN_REASON_REPARSE_POINT_CHANGE | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
            }
            return true;
        }
        DirectoryStampStatus Qualify() => journal.Qualify(@"C:\fixture", Guid.Empty, new(10), state, reader, Walk, out _);

        Qualify().ShouldBe(DirectoryStampStatus.Unavailable);
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(2, "A path invalidated during the walk must not memoize the obsolete qualification.");
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(2);
    }

    [WindowsOnlyFact]
    public void FailedAncestorWalkDoesNotSeedTheMemo()
    {
        var journal = new DirectoryJournal();
        var state = CreateState();
        var reader = new FakeReader();
        int walks = 0;
        bool Walk(string _) => ++walks > 1;
        DirectoryStampStatus Qualify() => journal.Qualify(@"C:\fixture", Guid.Empty, new(10), state, reader, Walk, out _);
        Qualify().ShouldBe(DirectoryStampStatus.Unavailable);
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(2);
    }

    [WindowsOnlyFact]
    public void CurrentHeadBootstrapRechecksAncestorEligibilityWithoutNewJournalRecords()
    {
        var journal = new DirectoryJournal();
        var state = new DirectoryJournal.JournalState(new()
        {
            UsnJournalID = 1,
            FirstUsn = 64,
            NextUsn = 128,
        });
        var reader = new FakeReader
        {
            Head = _ => new() { UsnJournalID = 1, FirstUsn = 64, NextUsn = 128 },
        };
        int walks = 0;
        bool Walk(string _) => ++walks == 1;
        DirectoryStampStatus Qualify() => journal.Qualify(@"C:\fixture", Guid.Empty, new(10), state, reader, Walk, out _);

        Qualify().ShouldBe(DirectoryStampStatus.Available);
        Qualify().ShouldBe(DirectoryStampStatus.Unavailable);
        walks.ShouldBe(2);
    }

    [WindowsOnlyFact]
    public void BasicInformationChangesDoNotInvalidateNameOnlyListings()
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId directory = new(10);
        state.Track(@"C:\fixture", directory);
        state.TryGetVersion(@"C:\fixture", out long before).ShouldBeTrue();
        ulong epoch = state.Epoch;
        state.ApplyBuffer(Buffer(64, Entry(0, new(20), directory, PInvoke.USN_REASON_BASIC_INFO_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out long version).ShouldBeTrue();
        version.ShouldBe(before);
        state.Epoch.ShouldBe(epoch);
    }

    [WindowsOnlyTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroStartReplayRejectsConcurrentTrimmingIncludingAfterAnEmptyJournal(bool initiallyEmpty)
    {
        var state = CreateState();
        var reader = new FakeReader();
        if (initiallyEmpty)
        {
            reader.Head = _ => new() { UsnJournalID = 1 };
            DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Available);
        }
        int firstQuery = reader.Queries;
        reader.Head = query => new()
        {
            UsnJournalID = 1,
            NextUsn = 128,
            FirstUsn = query == firstQuery + 1 ? 0 : 32,
        };
        reader.ReadBuffer = _ => Buffer(128, Entry(64, new(20), new(10), PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Disabled.ShouldBeTrue();
    }

    [WindowsOnlyFact]
    public void ZeroStartCompletenessRequirementSurvivesTransientPostQueryFailure()
    {
        var state = CreateState();
        var reader = new FakeReader
        {
            Head = query => new() { UsnJournalID = 1, NextUsn = 64, FirstUsn = query <= 2 ? 0 : 32 },
            QuerySucceeds = query => query != 2,
        };
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(64);
        state.Disabled.ShouldBeFalse();
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Disabled.ShouldBeTrue();
    }

    [WindowsOnlyFact]
    public void ANewHeadIsDrainedBeforeIssuingAStamp()
    {
        var state = CreateState();
        var reader = new FakeReader
        {
            Head = query => new() { UsnJournalID = 1, NextUsn = query == 1 ? 64 : 128 },
            ReadBuffer = start => start == 0 ? Buffer(64) : Buffer(128, Entry(64, new(20), new(10), PInvoke.USN_REASON_FILE_CREATE)),
        };
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Available);
        state.Cursor.ShouldBe(128);
        state.Disabled.ShouldBeFalse();
        state.TryGetVersion(@"C:\fixture", out _).ShouldBeTrue();
    }

    [WindowsOnlyFact]
    public void ContinuousHeadGrowthAndReplayBudgetsDeferWithoutPoisoningTheVolume()
    {
        var state = CreateState();
        var reader = new FakeReader
        {
            Head = query => new() { UsnJournalID = 1, NextUsn = query * 64 },
            ReadBuffer = start => Buffer(start + 64),
        };
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        reader.Queries.ShouldBe(4);
        state.Disabled.ShouldBeFalse();
        reader.Head = _ => new() { UsnJournalID = 1, NextUsn = state.Cursor };
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Available);

        state = CreateState();
        reader.Head = _ => new() { UsnJournalID = 1, NextUsn = 128 };
        reader.ReadBuffer = start => Buffer(start + 64,
            Entry(start, new(20), new(10), PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _, maximumReadBytes: 72).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(64);
        state.Disabled.ShouldBeFalse();
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _, maximumReadBytes: 72).ShouldBe(DirectoryStampStatus.Available);
        state.Cursor.ShouldBe(128);
    }

    [WindowsOnlyFact]
    public void AnEmptyNonProgressingReadDefersWithoutDisablingTracking()
    {
        var state = CreateState();
        var reader = new FakeReader
        {
            Head = _ => new() { UsnJournalID = 1, NextUsn = 64 },
            ReadBuffer = start => Buffer(start),
        };
        DirectoryJournal.DirectoryId directory = new(10);
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", directory, out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(0);
        state.Disabled.ShouldBeFalse();

        reader.ReadBuffer = _ => Buffer(64, Entry(0, new(20), directory,
            PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", directory, out long version).ShouldBe(DirectoryStampStatus.Available);
        version.ShouldBeGreaterThan(0);
    }

    [WindowsOnlyFact]
    public void RejectedRootsAvoidNativeCallsUntilClearWithoutPoisoningOtherRoots()
    {
        int calls = 0;
        var journal = new DirectoryJournal((string path, out DirectoryChangeStamp stamp, out DirectoryJournal.RejectionReason reason) =>
        {
            calls++;
            stamp = default;
            reason = path.StartsWith("C:", StringComparison.OrdinalIgnoreCase)
                ? DirectoryJournal.RejectionReason.IncompleteHistory : DirectoryJournal.RejectionReason.None;
            return DirectoryStampStatus.Unavailable;
        });
        journal.TryRead(@"C:\first", out _).ShouldBeFalse();
        journal.CanAttemptSharing(@"C:\second").ShouldBeFalse();
        journal.TryRead(@"c:\second", out _).ShouldBeFalse();
        calls.ShouldBe(1);
        journal.GetStatistics().NegativeHits.ShouldBe(1);
        journal.TryRead(@"D:\missing", out _).ShouldBeFalse();
        journal.CanAttemptSharing(@"D:\other").ShouldBeTrue();
        journal.TryRead(@"D:\other", out _).ShouldBeFalse();
        calls.ShouldBe(3);
        journal.Clear();
        journal.TryRead(@"C:\first", out _).ShouldBeFalse();
        calls.ShouldBe(4);
    }

    [WindowsOnlyTheory]
    [InlineData(64, 0)]
    [InlineData(0, 64)]
    [InlineData(64, 64)]
    public void RetainedStartupHistoryTracksOnlyChangesAfterTheCurrentCheckpoint(long firstUsn, long lowestUsn)
    {
        USN_JOURNAL_DATA_V0 info = new()
        {
            UsnJournalID = 1,
            FirstUsn = firstUsn,
            LowestValidUsn = lowestUsn,
            NextUsn = 128,
        };
        var state = new DirectoryJournal.JournalState(info);
        state.Disabled.ShouldBeFalse();
        state.Cursor.ShouldBe(128);
        var reader = new FakeReader
        {
            Head = _ => info,
            ReadBuffer = _ => throw new InvalidOperationException("Historical records must not be replayed at startup."),
        };
        DirectoryJournal.DirectoryId directory = new(10);
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", directory, out long before).ShouldBe(DirectoryStampStatus.Available);

        info.NextUsn = 192;
        reader.ReadBuffer = _ => Buffer(192, Entry(128, new(20), directory,
            PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", directory, out long after).ShouldBe(DirectoryStampStatus.Available);
        after.ShouldBeGreaterThan(before);
    }

    [WindowsOnlyFact]
    public void TrimmingBeforeTheCurrentCheckpointIsAllowedButLosingUnreadChangesIsNot()
    {
        var state = new DirectoryJournal.JournalState(new()
        {
            UsnJournalID = 1,
            FirstUsn = 64,
            NextUsn = 128,
        });
        var reader = new FakeReader
        {
            Head = _ => new() { UsnJournalID = 1, FirstUsn = 96, LowestValidUsn = 64, NextUsn = 128 },
        };
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Available);
        reader.Head = _ => new() { UsnJournalID = 1, FirstUsn = 129, NextUsn = 192 };
        DirectoryJournal.CatchUp(state, reader, @"C:\fixture", new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Disabled.ShouldBeTrue();
    }

    [WindowsOnlyTheory]
    [InlineData(-1, 0, 128)]
    [InlineData(0, -1, 128)]
    [InlineData(129, 0, 128)]
    [InlineData(0, 129, 128)]
    public void InvalidStartupCheckpointsCannotSeedSnapshots(long firstUsn, long lowestUsn, long nextUsn)
    {
        var state = new DirectoryJournal.JournalState(new()
        {
            UsnJournalID = 1,
            FirstUsn = firstUsn,
            LowestValidUsn = lowestUsn,
            NextUsn = nextUsn,
        });
        state.Track(@"C:\fixture", new(10));
        state.TryGetVersion(@"C:\fixture", out _).ShouldBeFalse();
    }

    [WindowsOnlyFact]
    public void JournalReplacementTruncationAndRewindAreRejected()
    {
        var state = CreateState();
        state.ApplyBuffer(Buffer(64)).ShouldBeTrue();
        USN_JOURNAL_DATA_V0 info = new() { UsnJournalID = 1, NextUsn = 128 };
        state.IsContinuous(info).ShouldBeTrue();

        info.UsnJournalID = 2;
        state.IsContinuous(info).ShouldBeFalse();
        info.UsnJournalID = 1;
        info.FirstUsn = 65;
        state.IsContinuous(info).ShouldBeFalse();
        info.FirstUsn = 0;
        info.LowestValidUsn = 65;
        state.IsContinuous(info).ShouldBeFalse();
        info.LowestValidUsn = 0;
        info.NextUsn = 63;
        state.IsContinuous(info).ShouldBeFalse();

        state.Disable();
        info.NextUsn = 128;
        state.IsContinuous(info).ShouldBeFalse();
    }

    [WindowsOnlyTheory]
    [InlineData("YAAAAAMAAAD1CQAAAAABAAAAAAAAAAAABQAAAAAABQAAAAAAAAAAAFhcPeIDAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAARAAAADgBMAFcAaQBuAGQAbwB3AHMAAAAAAAAA")]
    [InlineData("UAAAAAIAAAD1CQAAAAABAAUAAAAAAAUAWFw94gMAAAAAAAAAAAAAAAAAAAAAAAAAAAAAABEAAAAOADwAVwBpAG4AZABvAHcAcwAAAAAAAAA=")]
    public void RecordParserReadsCapturedNtfsV2AndV3Records(string fixture)
    {
        // Captured with FSCTL_READ_FILE_USN_DATA, not the synthetic journal encoder below.
        DirectoryJournal.TryParse(Convert.FromBase64String(fixture), out DirectoryJournal.Record record).ShouldBeTrue();
        record.Id.ShouldBe(new(0x10000000009f5));
        record.Parent.ShouldBe(new(0x5000000000005));
        record.Usn.ShouldBe(16680574040);
        record.Reason.ShouldBe(0u);
        record.Attributes.ShouldBe(0x11u);
    }

    [WindowsOnlyFact]
    public void RecordParserPreservesCaptured128BitRefsIdentitiesWithoutAdmittingRefs()
    {
        byte[] fixture = Convert.FromBase64String("UAAAAAMAAAAAAAAAAAAAAAAGAAAAAAAAAAAAAAAAAAAABgAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAQAAAAAgBMAC4AAAA=");
        DirectoryJournal.TryParse(fixture, out DirectoryJournal.Record record).ShouldBeTrue();
        record.Id.ShouldBe(new(0, 0x600));
        record.Parent.ShouldBe(record.Id);
        record.Attributes.ShouldBe(0x10u);
    }

    [WindowsOnlyFact]
    public void UnprivilegedRecordsWithoutNamesInvalidateTheirParent()
    {
        // Captured from our file's CREATE|CLOSE record; identifying fields are normalized.
        byte[] fixture = Convert.FromBase64String("UAAAAAMAAAAKAAAAAAAAAAAAAAAAAAAAFAAAAAAAAAAAAAAAAAAAAEAAAAAAAAAAAAAAAAAAAAAAAQCAAAAAAAAAAAAgAAAAAABMAAAAAAA=");
        DirectoryJournal.TryParse(fixture, out DirectoryJournal.Record record).ShouldBeTrue();
        DirectoryJournal.DirectoryId parent = new(20);
        record.Parent.ShouldBe(parent);
        var state = CreateState();
        state.Track(@"C:\fixture", parent);
        state.TryGetVersion(@"C:\fixture", out long before).ShouldBeTrue();
        state.ApplyBuffer(Buffer(128, fixture)).ShouldBeTrue();
        state.TryGetVersion(@"C:\fixture", out long after).ShouldBeTrue();
        after.ShouldBeGreaterThan(before);
    }

    [WindowsOnlyFact]
    public void MalformedAndNonProgressingJournalBuffersAreRejected()
    {
        byte[] entry = Entry(0, new(10), new(20), PInvoke.USN_REASON_FILE_CREATE);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(58), 1000);
        CreateState().ApplyBuffer(Buffer(64, entry)).ShouldBeFalse();

        entry = Entry(0, new(10), new(20), PInvoke.USN_REASON_FILE_CREATE);
        BinaryPrimitives.WriteUInt16LittleEndian(entry.AsSpan(4), 4);
        CreateState().ApplyBuffer(Buffer(64, entry)).ShouldBeFalse();
        CreateState().ApplyBuffer(Buffer(0)).ShouldBeFalse();
        CreateState().ApplyBuffer(Buffer(64, Entry(64, new(10), new(20), PInvoke.USN_REASON_FILE_CREATE))).ShouldBeFalse();
        CreateState().ApplyBuffer(Buffer(128,
            Entry(64, new(10), new(20), PInvoke.USN_REASON_FILE_CREATE),
            Entry(32, new(30), new(20), PInvoke.USN_REASON_FILE_CREATE))).ShouldBeFalse();
    }

    [Fact(Skip = "Requires an accessible NTFS change journal.", SkipType = typeof(DirectoryJournal_Tests), SkipUnless = nameof(WindowsJournalAvailable))]
    public void NativeUnrelatedRenamePreservesSharedGlobButMembershipAndAncestorChangesInvalidateIt()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        env.SetEnvironmentVariable("MSBUILDGLOBRESULTEXPERIMENT", "validated");
        env.SetEnvironmentVariable("MSBUILDDIRECTORYCACHEEXCLUDEROOTS", null);
        string root = env.CreateFolder().Path;
        string input = Directory.CreateDirectory(Path.Combine(root, "input")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(input, "child")).FullName;
        string unrelated = Directory.CreateDirectory(Path.Combine(root, "unrelated")).FullName;
        File.WriteAllText(Path.Combine(child, "Old.cs"), "");
        string external = Path.Combine(unrelated, "external.txt");
        File.WriteAllText(external, "");
        var cache = new DirectoryListingCache();
        int enumerations = 0;
        IReadOnlyList<string> Enumerate(FileMatcher.FileSystemEntity kind, string path, out bool succeeded)
        {
            enumerations++;
            succeeded = true;
            return kind switch
            {
                FileMatcher.FileSystemEntity.Files => Directory.GetFiles(path),
                FileMatcher.FileSystemEntity.Directories => Directory.GetDirectories(path),
                _ => Directory.GetFileSystemEntries(path),
            };
        }
        string[] Query() => new FileMatcher(FileSystems.Default,
            (kind, path, _, _, _) => Enumerate(kind, path, out _),
            new ConcurrentDictionary<string, IReadOnlyList<string>>(),
            directoryListingCache: cache, getFileSystemEntriesWithStatus: Enumerate).GetFiles(child, "*.cs").FileList;

        try
        {
            Query().Length.ShouldBe(1);
            int warmedEnumerations = enumerations;
            long hits = GlobResultExperiment.GetStatistics()[1];
            Query().Length.ShouldBe(1);
            enumerations.ShouldBe(warmedEnumerations);
            GlobResultExperiment.GetStatistics()[1].ShouldBeGreaterThan(hits);

            File.Move(external, Path.Combine(unrelated, "renamed.txt"));
            hits = GlobResultExperiment.GetStatistics()[1];
            Query().Length.ShouldBe(1);
            enumerations.ShouldBe(warmedEnumerations, "An owned rename outside the input must not invalidate its listing.");
            GlobResultExperiment.GetStatistics()[1].ShouldBeGreaterThan(hits);

            File.WriteAllText(Path.Combine(child, "New.cs"), "");
            Query().Length.ShouldBe(2);
            enumerations.ShouldBeGreaterThan(warmedEnumerations);

            Directory.Move(input, input + "-old");
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "Replacement.cs"), "");
            string[] actual = Query();
            actual.Length.ShouldBe(1);
            Path.GetFileName(actual[0]).ShouldBe("Replacement.cs");
        }
        finally
        {
            cache.Clear();
        }
    }

    [Fact(Skip = "Requires an ordinary fixed-drive temporary directory.", SkipType = typeof(DirectoryJournal_Tests), SkipUnless = nameof(WindowsTemporaryDirectoryAvailable))]
    public void DirectoryCapabilityIsRecheckedAfterReplacement()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string path = env.CreateFolder().Path;
        DirectoryJournal.IsSupportedPath(path).ShouldBeTrue();
        Directory.Delete(path);
        File.WriteAllText(path, "");
        DirectoryJournal.IsSupportedPath(path).ShouldBeFalse();
        File.Delete(path);
        Directory.CreateDirectory(path);
        DirectoryJournal.IsSupportedPath(path).ShouldBeTrue();
    }

#if FEATURE_SYMLINK_TARGET
    [Fact(Skip = "Requires Windows symbolic-link support.", SkipType = typeof(DirectoryJournal_Tests), SkipUnless = nameof(WindowsSymbolicLinksAvailable))]
    public void ReparseAncestorsPreventSharingOnOtherwiseOrdinaryDirectories()
    {
        using TestEnvironment env = TestEnvironment.Create(_output);
        string root = env.CreateFolder().Path;
        string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        string child = Directory.CreateDirectory(Path.Combine(target, "child")).FullName;
        string link = Path.Combine(root, "link");
        Directory.CreateSymbolicLink(link, target);

        DirectoryJournal.IsSupportedPath(child).ShouldBeTrue();
        DirectoryJournal.IsSupportedPath(Path.Combine(link, "child")).ShouldBeFalse();
        DirectoryJournal.IsSupportedPath(link).ShouldBeFalse();
    }
#endif

    private static DirectoryJournal.JournalState CreateState()
        => new(new USN_JOURNAL_DATA_V0 { UsnJournalID = 1 });

    private sealed class FakeReader : DirectoryJournal.IJournalReader
    {
        internal int Queries;
        internal Func<int, USN_JOURNAL_DATA_V0> Head = _ => new() { UsnJournalID = 1 };
        internal Func<int, bool> QuerySucceeds = _ => true;
        internal Func<long, byte[]> ReadBuffer = start => Buffer(start + 64);
        public bool Query(out USN_JOURNAL_DATA_V0 info) { info = Head(++Queries); return QuerySucceeds(Queries); }
        public bool Read(long start, out ReadOnlyMemory<byte> data) { data = ReadBuffer(start); return true; }
    }

    private static byte[] Buffer(long nextUsn, params byte[][] entries)
    {
        int length = sizeof(long);
        foreach (byte[] entry in entries)
        {
            length += entry.Length;
        }

        byte[] buffer = new byte[length];
        BinaryPrimitives.WriteInt64LittleEndian(buffer, nextUsn);
        int offset = sizeof(long);
        foreach (byte[] entry in entries)
        {
            entry.CopyTo(buffer, offset);
            offset += entry.Length;
        }

        return buffer;
    }

    private static byte[] Entry(long usn, DirectoryJournal.DirectoryId file, DirectoryJournal.DirectoryId parent, uint reason, ushort major = 2)
    {
        int extra = major == 3 ? 16 : 0;
        byte[] data = new byte[64 + extra];
        BinaryPrimitives.WriteUInt32LittleEndian(data, (uint)data.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4), major);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(8), file.Low);
        if (major == 3)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16), file.High);
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(32), parent.High);
        }

        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(16 + extra / 2), parent.Low);
        BinaryPrimitives.WriteInt64LittleEndian(data.AsSpan(24 + extra), usn);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(40 + extra), reason);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(56 + extra), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(58 + extra), (ushort)(60 + extra));
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(60 + extra), 'x');
        return data;
    }
}
#endif
