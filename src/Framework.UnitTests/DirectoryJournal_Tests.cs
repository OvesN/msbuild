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
        Microsoft.Build.Framework.NativeMethods.IsWindows && new RequiresSymbolicLinksFactAttribute().Skip is null;

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
    [InlineData(PInvoke.USN_REASON_BASIC_INFO_CHANGE)]
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

    [WindowsOnlyTheory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    public void IncompleteStartupHistoryCannotSeedSnapshots(long firstUsn, long lowestUsn)
    {
        USN_JOURNAL_DATA_V0 info = new()
        {
            UsnJournalID = 1,
            FirstUsn = firstUsn,
            LowestValidUsn = lowestUsn,
            NextUsn = 128,
        };
        DirectoryJournal.JournalState.HasCompleteHistory(info).ShouldBeFalse();
        var state = new DirectoryJournal.JournalState(info);
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
    [InlineData(2)]
    [InlineData(3)]
    public void RecordParserPreservesTheFullFileAndParentIdentity(int major)
    {
        DirectoryJournal.DirectoryId file = new(20, major == 3 ? 21UL : 0);
        DirectoryJournal.DirectoryId parent = new(30, major == 3 ? 31UL : 0);
        byte[] entry = Entry(123, file, parent, PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE, (ushort)major);
        DirectoryJournal.TryParse(entry, out DirectoryJournal.Record record).ShouldBeTrue();
        record.Id.ShouldBe(file);
        record.Parent.ShouldBe(parent);
        record.Usn.ShouldBe(123);
        record.Reason.ShouldBe(PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_CLOSE);
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

    [WindowsOnlyFact]
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
