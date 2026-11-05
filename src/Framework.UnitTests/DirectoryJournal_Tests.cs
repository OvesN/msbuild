// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if FEATURE_WINDOWSINTEROP
using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.Versioning;
using Microsoft.Build.Shared;
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

    [WindowsOnlyFact]
    public void RenameInvalidatesOldAndNewParentsIncludingTheFirstUsn()
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId oldParent = new(10);
        DirectoryJournal.DirectoryId newParent = new(20);
        DirectoryJournal.DirectoryId file = new(30);
        state.Track(oldParent);
        state.Track(newParent);
        state.TryGetVersion(oldParent, out long oldVersion).ShouldBeTrue();
        state.TryGetVersion(newParent, out long newVersion).ShouldBeTrue();

        state.ApplyBuffer(Buffer(128,
            Entry(0, file, oldParent, PInvoke.USN_REASON_RENAME_OLD_NAME),
            Entry(64, file, newParent, PInvoke.USN_REASON_RENAME_NEW_NAME | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();

        state.TryGetVersion(oldParent, out long currentOld).ShouldBeTrue();
        state.TryGetVersion(newParent, out long currentNew).ShouldBeTrue();
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
    public void UnclosedChangesPreventReuseUntilTheirCloseRecord(uint reason)
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId directory = new(10);
        DirectoryJournal.DirectoryId file = new(20);
        state.Track(directory);
        ulong epoch = state.Epoch;

        state.ApplyBuffer(Buffer(64, Entry(0, file, directory, reason))).ShouldBeTrue();
        state.TryGetVersion(directory, out _).ShouldBeFalse();
        state.TryGetVersion(directory, out _).ShouldBeFalse("A repeated change may have no additional journal record.");

        state.ApplyBuffer(Buffer(128, Entry(64, file, directory, reason | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        state.TryGetVersion(directory, out long version).ShouldBeTrue();
        version.ShouldBeGreaterThan(0);
        if (reason != PInvoke.USN_REASON_FILE_CREATE)
        {
            state.Epoch.ShouldNotBe(epoch);
        }
    }

    [WindowsOnlyFact]
    public void OpenCreatesOnlyBlockTheirOriginalParentAndAreCountedOnce()
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId parent = new(10);
        DirectoryJournal.DirectoryId unrelated = new(11);
        state.Track(parent);
        state.Track(unrelated);
        state.ApplyBuffer(Buffer(192,
            Entry(0, new(20), parent, PInvoke.USN_REASON_FILE_CREATE),
            Entry(64, new(20), parent, PInvoke.USN_REASON_FILE_CREATE),
            Entry(128, new(21), parent, PInvoke.USN_REASON_FILE_CREATE))).ShouldBeTrue();
        state.TryGetVersion(unrelated, out _).ShouldBeTrue();
        state.TryGetVersion(parent, out _).ShouldBeFalse();

        state.ApplyBuffer(Buffer(256, Entry(192, new(20), unrelated, PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        state.TryGetVersion(parent, out _).ShouldBeFalse("The other open file still blocks its original parent.");
        state.ApplyBuffer(Buffer(320, Entry(256, new(21), unrelated, PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        state.TryGetVersion(parent, out _).ShouldBeTrue();
    }

    [WindowsOnlyFact]
    public void CoalescedCreatePromotionInvalidatesEvenUnknownParents()
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId tracked = new(10);
        state.Track(tracked);
        state.TryGetVersion(tracked, out long version).ShouldBeTrue();
        ulong epoch = state.Epoch;
        state.ApplyBuffer(Buffer(64, Entry(0, new(20), new(30), PInvoke.USN_REASON_FILE_CREATE))).ShouldBeTrue();
        state.TryGetVersion(tracked, out _).ShouldBeTrue();
        state.ApplyBuffer(Buffer(128, Entry(64, new(20), new(40),
            PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_RENAME_NEW_NAME))).ShouldBeTrue();
        state.TryGetVersion(tracked, out _).ShouldBeFalse();
        state.Epoch.ShouldNotBe(epoch);
        state.ApplyBuffer(Buffer(192, Entry(128, new(20), new(40), PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        state.TryGetVersion(tracked, out long current).ShouldBeTrue();
        current.ShouldBe(version, "The epoch, not a known-parent version, must invalidate this snapshot.");
    }

    [WindowsOnlyFact]
    public void CreateOnlyRecordsWithDifferentParentsPromoteToGlobalPending()
    {
        var state = CreateState();
        DirectoryJournal.DirectoryId tracked = new(10);
        state.Track(tracked);
        ulong epoch = state.Epoch;
        state.ApplyBuffer(Buffer(64, Entry(0, new(20), tracked, PInvoke.USN_REASON_FILE_CREATE))).ShouldBeTrue();
        state.ApplyBuffer(Buffer(128, Entry(64, new(20), new(30), PInvoke.USN_REASON_FILE_CREATE))).ShouldBeTrue();
        state.Epoch.ShouldNotBe(epoch);
        state.TryGetVersion(tracked, out _).ShouldBeFalse();
        state.ApplyBuffer(Buffer(192, Entry(128, new(20), new(30), PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
        state.TryGetVersion(tracked, out _).ShouldBeTrue();
    }

    [WindowsOnlyFact]
    public void QualificationMemoSkipsStableWalksAndRechecksAfterAnEpochChange()
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
        walks.ShouldBe(2);
    }

    [WindowsOnlyFact]
    public void EpochChangeDuringQualificationCannotSeedTheMemo()
    {
        var journal = new DirectoryJournal();
        var state = CreateState();
        var reader = new FakeReader { Head = _ => new() { UsnJournalID = 1, NextUsn = state.Cursor } };
        int walks = 0;
        bool Walk(string _)
        {
            if (++walks == 1)
            {
                state.ApplyBuffer(Buffer(64, Entry(0, new(20), new(30),
                    PInvoke.USN_REASON_REPARSE_POINT_CHANGE | PInvoke.USN_REASON_CLOSE))).ShouldBeTrue();
            }
            return true;
        }
        DirectoryStampStatus Qualify() => journal.Qualify(@"C:\fixture", Guid.Empty, new(10), state, reader, Walk, out _);

        Qualify().ShouldBe(DirectoryStampStatus.Unavailable);
        Qualify().ShouldBe(DirectoryStampStatus.Available);
        walks.ShouldBe(2, "An epoch change during the walk must not memoize the obsolete qualification.");
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
            UsnJournalID = 1, FirstUsn = 64, NextUsn = 128,
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
        state.Track(directory);
        ulong epoch = state.Epoch;
        state.ApplyBuffer(Buffer(64, Entry(0, new(20), directory, PInvoke.USN_REASON_BASIC_INFO_CHANGE))).ShouldBeTrue();
        state.TryGetVersion(directory, out long version).ShouldBeTrue();
        version.ShouldBe(0);
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
            DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Available);
        }
        int firstQuery = reader.Queries;
        reader.Head = query => new()
        {
            UsnJournalID = 1, NextUsn = 128, FirstUsn = query == firstQuery + 1 ? 0 : 32,
        };
        reader.ReadBuffer = _ => Buffer(128, Entry(64, new(20), new(10), PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
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
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(64);
        state.Disabled.ShouldBeFalse();
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
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
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(128);
        state.Disabled.ShouldBeFalse();
        state.TryGetVersion(new(10), out _).ShouldBeFalse();
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
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
        reader.Queries.ShouldBe(4);
        state.Disabled.ShouldBeFalse();
        reader.Head = _ => new() { UsnJournalID = 1, NextUsn = state.Cursor };
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Available);

        state = CreateState();
        reader.Head = _ => new() { UsnJournalID = 1, NextUsn = 128 };
        reader.ReadBuffer = start => Buffer(start + 64,
            Entry(start, new(20), new(10), PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, new(10), out _, maximumReadBytes: 72).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(64);
        state.Disabled.ShouldBeFalse();
        DirectoryJournal.CatchUp(state, reader, new(10), out _, maximumReadBytes: 72).ShouldBe(DirectoryStampStatus.Available);
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
        DirectoryJournal.CatchUp(state, reader, directory, out _).ShouldBe(DirectoryStampStatus.Unavailable);
        state.Cursor.ShouldBe(0);
        state.Disabled.ShouldBeFalse();

        reader.ReadBuffer = _ => Buffer(64, Entry(0, new(20), directory,
            PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, directory, out long version).ShouldBe(DirectoryStampStatus.Available);
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
        DirectoryJournal.CatchUp(state, reader, directory, out long before).ShouldBe(DirectoryStampStatus.Available);

        info.NextUsn = 192;
        reader.ReadBuffer = _ => Buffer(192, Entry(128, new(20), directory,
            PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE));
        DirectoryJournal.CatchUp(state, reader, directory, out long after).ShouldBe(DirectoryStampStatus.Available);
        after.ShouldBeGreaterThan(before);
    }

    [WindowsOnlyFact]
    public void TrimmingBeforeTheCurrentCheckpointIsAllowedButLosingUnreadChangesIsNot()
    {
        var state = new DirectoryJournal.JournalState(new()
        {
            UsnJournalID = 1, FirstUsn = 64, NextUsn = 128,
        });
        var reader = new FakeReader
        {
            Head = _ => new() { UsnJournalID = 1, FirstUsn = 96, LowestValidUsn = 64, NextUsn = 128 },
        };
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Available);
        reader.Head = _ => new() { UsnJournalID = 1, FirstUsn = 129, NextUsn = 192 };
        DirectoryJournal.CatchUp(state, reader, new(10), out _).ShouldBe(DirectoryStampStatus.Unavailable);
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
            UsnJournalID = 1, FirstUsn = firstUsn, LowestValidUsn = lowestUsn, NextUsn = nextUsn,
        });
        state.Track(new(10));
        state.TryGetVersion(new(10), out _).ShouldBeFalse();
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
        state.Track(parent);
        state.TryGetVersion(parent, out long before).ShouldBeTrue();
        state.ApplyBuffer(Buffer(128, fixture)).ShouldBeTrue();
        state.TryGetVersion(parent, out long after).ShouldBeTrue();
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
