// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if FEATURE_WINDOWSINTEROP
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Microsoft.Build.Shared;

[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class DirectoryJournal
{
    private const int BufferSize = 65536;
    private const int MaximumReadBytes = 16 * 1024 * 1024;
    private const int MaximumQualifications = 65536;
    private const uint MembershipReasons = PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_FILE_DELETE
        | PInvoke.USN_REASON_RENAME_OLD_NAME | PInvoke.USN_REASON_RENAME_NEW_NAME | PInvoke.USN_REASON_HARD_LINK_CHANGE;
    private const uint AllLinkReasons = PInvoke.USN_REASON_FILE_DELETE | PInvoke.USN_REASON_DATA_EXTEND
        | PInvoke.USN_REASON_DATA_OVERWRITE | PInvoke.USN_REASON_DATA_TRUNCATION
        | PInvoke.USN_REASON_STREAM_CHANGE | PInvoke.USN_REASON_REPARSE_POINT_CHANGE;
    private const uint SingleLinkReasons = PInvoke.USN_REASON_HARD_LINK_CHANGE | PInvoke.USN_REASON_RENAME_OLD_NAME;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Volume> _volumes = new();
    private readonly Dictionary<char, RejectionReason> _rejectedRoots = new();
    private readonly Dictionary<string, Qualification> _qualifications = new(StringComparer.OrdinalIgnoreCase);
    private readonly NativeReader _reader;
    private long _nativeReads;
    private long _stamps;
    private long _negativeHits;
    private long _ancestorWalks;
    private long _deferrals;
    private static long s_epoch;

    internal enum RejectionReason { None, UnsupportedVolume, AccessDenied, JournalInactive, IncompleteHistory, InitialHistoryTooLarge, InvalidJournal }
    internal delegate DirectoryStampStatus NativeReader(string fullPath, out DirectoryChangeStamp stamp, out RejectionReason rejection);
    internal delegate bool AncestorReader(string path, out Record record);
    internal readonly record struct Statistics(long NativeReads, long Stamps, long NegativeHits, long AncestorWalks, long Deferrals);
    private readonly record struct Qualification(Guid Volume, DirectoryId Id, ulong Epoch, long Version);

    internal DirectoryJournal() : this(null)
    {
    }

    internal DirectoryJournal(NativeReader? reader) => _reader = reader ?? ReadCore;

    internal bool TryRead(string path, out DirectoryChangeStamp stamp)
        => Read(path, out stamp) == DirectoryStampStatus.Available;

    internal bool CanAttemptSharing(string path)
    {
        if (!Path.IsPathRooted(path)) { path = Path.GetFullPath(path); }
        char root = GetRoot(path);
        lock (_gate)
        {
            return root != '\0' && !_rejectedRoots.ContainsKey(root);
        }
    }

    internal Statistics GetStatistics()
    {
        lock (_gate) { return new(_nativeReads, _stamps, _negativeHits, _ancestorWalks, _deferrals); }
    }

    internal DirectoryStampStatus Read(string path, out DirectoryChangeStamp stamp)
    {
        stamp = default;
        string? fullPath = null;
        if (!Path.IsPathRooted(path)) { path = fullPath = Path.GetFullPath(path); }
        char root = GetRoot(path);
        if (root == '\0') { return DirectoryStampStatus.Unavailable; }
        lock (_gate)
        {
            if (_rejectedRoots.ContainsKey(root))
            {
                _negativeHits++;
                return DirectoryStampStatus.Unavailable;
            }

            fullPath ??= Path.GetFullPath(path);
            _nativeReads++;
            DirectoryStampStatus status = _reader(fullPath, out stamp, out RejectionReason rejection);
            if (rejection != RejectionReason.None)
            {
                _rejectedRoots[root] = rejection;
                DebugTrace.WriteLine($"Directory journal sharing disabled for '{root}:': {rejection}.", category: nameof(DirectoryJournal));
            }
            if (status == DirectoryStampStatus.Available) { _stamps++; }
            else { _deferrals++; }
            return status;
        }
    }

    private static char GetRoot(string path)
    {
        return path.Length >= 3 && path[1] == ':' && FileUtilities.IsAnySlash(path[2])
            ? char.ToUpperInvariant(path[0]) : '\0';
    }

    private DirectoryStampStatus ReadCore(string path, out DirectoryChangeStamp stamp, out RejectionReason rejection)
    {
        stamp = default;
        rejection = RejectionReason.None;
        HANDLE directory;
        fixed (char* pointer = path)
        {
            directory = PInvoke.CreateFile(new PCWSTR(pointer),
                (uint)(FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES | FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY),
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, HANDLE.Null);
        }

        if (directory == HANDLE.INVALID_HANDLE_VALUE)
        {
            int error = Marshal.GetLastWin32Error();
            TraceError(path);
            return error is 2 or 3 ? DirectoryStampStatus.Missing : DirectoryStampStatus.Unavailable;
        }

        try
        {
            using BufferScope<char> finalPath = new(stackalloc char[256]);
            uint length;
            fixed (char* name = finalPath)
            {
                length = PInvoke.GetFinalPathNameByHandle(directory, new PWSTR(name),
                    (uint)finalPath.Length, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID);
            }

            if (length >= finalPath.Length)
            {
                finalPath.EnsureCapacity(checked((int)length + 1));
                fixed (char* name = finalPath)
                {
                    length = PInvoke.GetFinalPathNameByHandle(directory, new PWSTR(name),
                        (uint)finalPath.Length, GETFINALPATHNAMEBYHANDLE_FLAGS.VOLUME_NAME_GUID);
                }
            }

            if (length == 0 || length >= finalPath.Length)
            {
                TraceError(path);
                return DirectoryStampStatus.Unavailable;
            }

            ReadOnlySpan<char> resolved = finalPath.AsSpan().Slice(0, (int)length);
            int end = resolved.IndexOf('}');
            if (!resolved.StartsWith(@"\\?\Volume{".AsSpan(), StringComparison.OrdinalIgnoreCase) || end < 0
                || !resolved.Slice(end + 1).TrimEnd('\\').Equals(path.AsSpan().Slice(2).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return DirectoryStampStatus.Unavailable;
            }

#if NET
            bool parsed = Guid.TryParse(resolved.Slice(11, end - 11), out Guid volumeId);
#else
            bool parsed = Guid.TryParse(resolved.Slice(11, end - 11).ToString(), out Guid volumeId);
#endif
            if (!parsed)
            {
                return DirectoryStampStatus.Unavailable;
            }

            if (!_volumes.TryGetValue(volumeId, out Volume? volume))
            {
                if (!IsFixedDrive(path))
                {
                    rejection = RejectionReason.UnsupportedVolume;
                    return DirectoryStampStatus.Unavailable;
                }

                Volume? opened = null;
                try
                {
                    opened = OpenVolume(resolved.Slice(0, end + 1).ToString(), directory, out rejection);
                    if (opened is null) { return DirectoryStampStatus.Unavailable; }
                    _volumes.Add(volumeId, opened);
                    volume = opened;
                    opened = null;
                }
                finally
                {
                    opened?.Dispose();
                }
            }

            if (!TryReadDirectoryRecord(directory, out Record record))
            {
                return DirectoryStampStatus.Unavailable;
            }

            if (Qualify(path, volumeId, record.Id, volume.State, volume, null, out long version,
                record.Usn, record.Parent) != DirectoryStampStatus.Available)
            {
                if (volume.State.Disabled)
                {
                    rejection = RejectionReason.InvalidJournal;
                    volume.Dispose();
                }
                return DirectoryStampStatus.Unavailable;
            }

            stamp = new DirectoryChangeStamp(volume.State.Epoch, record.Id.Low, record.Id.High, version);
            return DirectoryStampStatus.Available;
        }
        finally
        {
            if (!PInvoke.CloseHandle(directory))
            {
                TraceError(path);
            }
        }
    }

    internal DirectoryStampStatus Qualify(string path, Guid volumeId, DirectoryId id, JournalState state, IJournalReader reader,
        Func<string, bool>? walkAncestors, out long version, long usn = -1, DirectoryId parent = default)
    {
        int remainingReadBytes = MaximumReadBytes;
        state.Track(path, id, usn);
        version = 0;
        if (!CatchUp(state, reader, ref remainingReadBytes) || !state.TryGetVersion(path, out version))
        {
            return DirectoryStampStatus.Unavailable;
        }

        ulong epoch = state.Epoch;
        var qualification = new Qualification(volumeId, id, epoch, version);
        if (!state.RequiresAncestorValidation
            && _qualifications.TryGetValue(path, out Qualification current) && current == qualification)
        {
            return DirectoryStampStatus.Available;
        }

        _ancestorWalks++;
        bool qualified = walkAncestors is not null
            ? walkAncestors(path)
            : HasOrdinaryAncestors(path) && TrackAncestors(path, parent, state);
        if (!qualified || !CatchUp(state, reader, ref remainingReadBytes)
            || !state.TryGetVersion(path, out version) || state.Epoch != epoch || version != qualification.Version)
        {
            return DirectoryStampStatus.Unavailable;
        }

        if (!state.RequiresAncestorValidation)
        {
            if (_qualifications.Count >= MaximumQualifications) { _qualifications.Clear(); }
            _qualifications[path] = qualification;
        }
        return DirectoryStampStatus.Available;
    }

    private static bool TryReadDirectoryRecord(HANDLE directory, out Record record)
    {
        record = default;
        READ_FILE_USN_DATA request = new() { MinMajorVersion = 2, MaxMajorVersion = 3 };
        using BufferScope<byte> recordBuffer = new(stackalloc byte[1024]);
        uint returned;
        fixed (byte* pointer = recordBuffer)
        {
            if (!PInvoke.DeviceIoControl(directory, PInvoke.FSCTL_READ_FILE_USN_DATA,
                &request, (uint)sizeof(READ_FILE_USN_DATA), pointer, (uint)recordBuffer.Length, &returned, null))
            {
                TraceError("reading directory identity");
                return false;
            }

            return returned <= recordBuffer.Length
                && TryParse(recordBuffer.AsSpan().Slice(0, (int)returned), out record)
                && record.Usn >= 0
                && (record.Attributes & (uint)(FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT))
                    == (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY;
        }
    }

    internal static bool TrackAncestors(string path, DirectoryId parent, JournalState state, AncestorReader? readAncestor = null)
    {
        string? ancestor = Path.GetDirectoryName(path.TrimEnd('\\'));
        while (ancestor is not null)
        {
            if (state.IsAnchored(ancestor, parent)) { return state.TryAnchor(path); }
            Record record;
            bool read = readAncestor is null ? TryReadAncestor(ancestor, out record) : readAncestor(ancestor, out record);
            if (!read || record.Id != parent) { return false; }
            state.Track(ancestor, record.Id, record.Usn, enumerated: false);
            parent = record.Parent;

            ancestor = Path.GetDirectoryName(ancestor);
        }
        return state.TryAnchor(path);
    }

    private static bool TryReadAncestor(string path, out Record record)
    {
        record = default;
        HANDLE directory;
        fixed (char* pointer = path)
        {
            directory = PInvoke.CreateFile(new PCWSTR(pointer), 0,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, HANDLE.Null);
        }
        if (directory == HANDLE.INVALID_HANDLE_VALUE) { return false; }
        try { return TryReadDirectoryRecord(directory, out record); }
        finally { PInvoke.CloseHandle(directory); }
    }

    internal static bool IsSupportedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        return GetRoot(fullPath) != '\0' && IsFixedDrive(fullPath) && HasOrdinaryAncestors(fullPath);
    }

    private static bool IsFixedDrive(string fullPath)
    {
        fixed (char* pointer = fullPath.Substring(0, 3))
        {
            return PInvoke.GetDriveType(new PCWSTR(pointer)) == PInvoke.DRIVE_FIXED;
        }
    }

    private static bool HasOrdinaryAncestors(string fullPath)
    {
        string? ancestor = fullPath.TrimEnd('\\');
        while (ancestor is not null)
        {
            string current = ancestor.Length == 2 ? ancestor + "\\" : ancestor;
            uint attributes;
            fixed (char* pointer = current)
            {
                attributes = PInvoke.GetFileAttributes(new PCWSTR(pointer));
            }

            if (attributes == PInvoke.INVALID_FILE_ATTRIBUTES
                || (attributes & (uint)(FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT))
                    != (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY)
            {
                return false;
            }

            ancestor = Path.GetDirectoryName(current);
        }

        return true;
    }

    private static Volume? OpenVolume(string path, HANDLE directory, out RejectionReason rejection)
    {
        rejection = RejectionReason.None;
        using BufferScope<char> fileSystem = new(stackalloc char[32]);
        uint flags;
        fixed (char* name = fileSystem)
        {
            if (!PInvoke.GetVolumeInformationByHandle(directory, null, 0, null, null, &flags,
                new PWSTR(name), (uint)fileSystem.Length))
            {
                return null;
            }
            if ((flags & PInvoke.FILE_SUPPORTS_USN_JOURNAL) == 0
                || !fileSystem.AsSpan().Slice(0, 5).SequenceEqual("NTFS\0".AsSpan()))
            {
                rejection = RejectionReason.UnsupportedVolume;
                return null;
            }
        }

        HANDLE handle;
        fixed (char* pointer = path)
        {
            handle = PInvoke.CreateFile(new PCWSTR(pointer), (uint)FILE_ACCESS_RIGHTS.FILE_TRAVERSE,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, HANDLE.Null);
        }

        if (handle == HANDLE.INVALID_HANDLE_VALUE)
        {
            int error = Marshal.GetLastWin32Error();
            if (error == 5) { rejection = RejectionReason.AccessDenied; }
            TraceError(path, error);
            return null;
        }

        SafeFileHandle? owner = null;
        try
        {
            owner = new((nint)handle.Value, ownsHandle: true);
            if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 info, out int error))
            {
                rejection = error switch { 1179 => RejectionReason.JournalInactive, 5 => RejectionReason.AccessDenied, _ => RejectionReason.None };
                return null;
            }

            DebugTrace.WriteLine($"Directory journal '{path}': initial NextUsn={info.NextUsn}.", category: nameof(DirectoryJournal));
            if (!JournalState.HasValidCheckpoint(info))
            {
                rejection = RejectionReason.InvalidJournal;
                return null;
            }

            var volume = new Volume(info) { Handle = owner };
            owner = null;
            return volume;
        }
        finally
        {
            owner?.Dispose();
        }
    }

    internal interface IJournalReader
    {
        bool Query(out USN_JOURNAL_DATA_V0 info);
        bool Read(long start, out ReadOnlyMemory<byte> data);
    }

    internal static DirectoryStampStatus CatchUp(JournalState state, IJournalReader reader, string path, DirectoryId id, out long version,
        int maximumReadBytes = MaximumReadBytes)
    {
        state.Track(path, id);
        version = 0;
        return CatchUp(state, reader, ref maximumReadBytes) && state.TryGetVersion(path, out version)
            ? DirectoryStampStatus.Available : DirectoryStampStatus.Unavailable;
    }

    private static bool CatchUp(JournalState state, IJournalReader reader, ref int remainingReadBytes)
    {
        if (state.Disabled || !reader.Query(out USN_JOURNAL_DATA_V0 info) || !state.ObserveHead(info)) { return false; }
        for (int pass = 0; pass < 3; pass++)
        {
            while (state.Cursor < info.NextUsn)
            {
                if (remainingReadBytes < sizeof(long) || !reader.Read(state.Cursor, out ReadOnlyMemory<byte> data))
                {
                    return false;
                }
                remainingReadBytes -= data.Length;
                if (remainingReadBytes < 0) { return false; }
                if (data.Length == sizeof(long) && BinaryPrimitives.ReadInt64LittleEndian(data.Span) == state.Cursor)
                {
                    return false;
                }
                if (!state.ApplyBuffer(data.Span))
                {
                    state.Disable();
                    return false;
                }
            }

            if (!reader.Query(out info) || !state.ObserveHead(info)) { return false; }
            if (state.Cursor == info.NextUsn)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryQuery(HANDLE handle, out USN_JOURNAL_DATA_V0 info, out int error)
    {
        info = default;
        error = 0;
        uint returned;
        fixed (USN_JOURNAL_DATA_V0* pointer = &info)
        {
            if (!PInvoke.DeviceIoControl(handle, PInvoke.FSCTL_QUERY_USN_JOURNAL, null, 0,
                pointer, (uint)sizeof(USN_JOURNAL_DATA_V0), &returned, null)
                || returned < sizeof(USN_JOURNAL_DATA_V0))
            {
                error = Marshal.GetLastWin32Error();
                TraceError("querying existing directory change journal", error);
                return false;
            }
        }

        return true;
    }

    internal static bool TryParse(ReadOnlySpan<byte> data, out Record record)
    {
        record = default;
        if (data.Length < 8)
        {
            return false;
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(data);
        ushort major = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(4));
        int minimum = major switch { 2 => 60, 3 => 76, _ => int.MaxValue };
        if (length < minimum || length > data.Length || (length & 7) != 0)
        {
            return false;
        }

        int extra = major == 3 ? 16 : 0;
        ushort nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(56 + extra));
        ushort nameOffset = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(58 + extra));
        if (nameOffset < minimum || (nameOffset & 1) != 0 || (nameLength & 1) != 0
            || (uint)nameOffset + nameLength > length)
        {
            return false;
        }

        record = new Record(
            length,
            new DirectoryId(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(8)),
                major == 3 ? BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16)) : 0),
            new DirectoryId(BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(16 + extra / 2)),
                major == 3 ? BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(32)) : 0),
            BinaryPrimitives.ReadInt64LittleEndian(data.Slice(24 + extra)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(40 + extra)),
            BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(52 + extra)));
        return true;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            foreach (Volume volume in _volumes.Values)
            {
                volume.Dispose();
            }

            _volumes.Clear();
            _rejectedRoots.Clear();
            _qualifications.Clear();
            _nativeReads = _stamps = _negativeHits = _ancestorWalks = _deferrals = 0;
        }
    }

    private static ulong NextEpoch() => unchecked((ulong)Interlocked.Increment(ref s_epoch));

    private static void TraceError(string path) => TraceError(path, Marshal.GetLastWin32Error());
    private static void TraceError(string path, int error)
        => DebugTrace.WriteLine($"Directory journal validation failed for '{path}': Win32 {error}.", category: nameof(DirectoryJournal));

    internal readonly record struct DirectoryId(ulong Low, ulong High = 0);
    internal readonly record struct Record(uint Length, DirectoryId Id, DirectoryId Parent, long Usn, uint Reason, uint Attributes);

    internal sealed class JournalState
    {
        private readonly Dictionary<string, Tracking> _paths = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<DirectoryId, List<Tracking>> _recordsByFileId = new();
        private readonly Queue<Tracking> _invalidations = new();
        private long _version;
        private bool _requiresCompleteHistory;
        internal readonly ulong JournalId;
        internal readonly bool RequiresAncestorValidation;
        internal long Cursor { get; private set; }
        internal ulong Epoch { get; private set; } = NextEpoch();
        internal bool Disabled { get; private set; }

        internal JournalState(USN_JOURNAL_DATA_V0 info)
        {
            JournalId = info.UsnJournalID;
            Cursor = info.NextUsn;
            // Pre-checkpoint coalesced reparse changes cannot safely qualify the ancestor memo.
            RequiresAncestorValidation = Cursor != 0;
            Disabled = !HasValidCheckpoint(info);
        }

        internal static bool HasValidCheckpoint(USN_JOURNAL_DATA_V0 info)
            => info.FirstUsn >= 0 && info.LowestValidUsn >= 0
                && info.NextUsn >= info.FirstUsn && info.NextUsn >= info.LowestValidUsn;

        // A zero-start read can skip records silently if the journal is trimmed during the read.
        internal static bool HasCompleteHistory(USN_JOURNAL_DATA_V0 info)
            => info.FirstUsn == 0 && info.LowestValidUsn == 0 && info.NextUsn >= 0;

        internal bool IsContinuous(USN_JOURNAL_DATA_V0 info)
            => !Disabled && HasValidCheckpoint(info) && info.UsnJournalID == JournalId
                && Cursor >= info.FirstUsn && Cursor >= info.LowestValidUsn && Cursor <= info.NextUsn
                && (!(Cursor == 0 || _requiresCompleteHistory) || HasCompleteHistory(info));

        internal bool ObserveHead(USN_JOURNAL_DATA_V0 info)
        {
            if (!IsContinuous(info))
            {
                Disable();
                return false;
            }
            _requiresCompleteHistory = false;
            return true;
        }

        internal void Track(string path, DirectoryId id, long usn = -1, bool enumerated = true)
        {
            path = Normalize(path);
            if (!_paths.TryGetValue(path, out Tracking? tracking))
            {
                if (_paths.Count >= MaximumQualifications)
                {
                    _paths.Clear();
                    _recordsByFileId.Clear();
                    Epoch = NextEpoch();
                }

                tracking = new Tracking(path);
                _paths.Add(path, tracking);
                Tracking child = tracking;
                string? parentPath = Path.GetDirectoryName(path);
                while (parentPath is not null)
                {
                    if (_paths.TryGetValue(parentPath, out Tracking? existing))
                    {
                        child.Parent = existing;
                        (existing.Children ??= []).Add(child);
                        break;
                    }

                    var container = new Tracking(parentPath);
                    _paths.Add(parentPath, container);
                    child.Parent = container;
                    (container.Children ??= []).Add(child);
                    child = container;
                    parentPath = Path.GetDirectoryName(parentPath);
                }
            }

            if (tracking.Registered && tracking.Id != id)
            {
                Invalidate(tracking);
                List<Tracking> oldRecords = _recordsByFileId[tracking.Id];
                oldRecords.Remove(tracking);
                if (oldRecords.Count == 0) { _recordsByFileId.Remove(tracking.Id); }
                tracking.Registered = false;
            }
            if (!tracking.Registered)
            {
                if (!_recordsByFileId.TryGetValue(id, out List<Tracking>? records))
                {
                    records = [];
                    _recordsByFileId.Add(id, records);
                }
                records.Add(tracking);
                tracking.Registered = true;
            }
            if (!tracking.Active)
            {
                tracking.Id = id;
                // Zero also means no prior record, so it cannot supersede the journal's first event.
                tracking.Usn = usn == 0 ? -1 : usn;
                tracking.Version = ++_version;
                tracking.Active = true;
            }
            tracking.Enumerated |= enumerated;
        }

        internal bool IsAnchored(string path, DirectoryId id)
            => _paths.TryGetValue(Normalize(path), out Tracking? tracking)
                && tracking.Active && tracking.Anchored && tracking.Id == id;

        internal bool TryAnchor(string path)
        {
            if (!_paths.TryGetValue(Normalize(path), out Tracking? tracking)) { return false; }
            for (Tracking? current = tracking; current is not null; current = current.Parent)
            {
                if (!current.Active) { return false; }
                if (current.Anchored) { break; }
            }
            for (Tracking? current = tracking; current is not null && !current.Anchored; current = current.Parent)
            {
                current.Anchored = true;
            }
            return true;
        }

        internal bool ApplyBuffer(ReadOnlySpan<byte> data)
        {
            if (Disabled || data.Length < sizeof(long))
            {
                return false;
            }

            long next = BinaryPrimitives.ReadInt64LittleEndian(data);
            long previous = Cursor - 1;
            if (next <= Cursor)
            {
                return false;
            }
            _requiresCompleteHistory |= Cursor == 0;

            for (int offset = sizeof(long); offset < data.Length;)
            {
                if (!TryParse(data.Slice(offset), out Record record) || record.Usn <= previous || record.Usn >= next)
                {
                    return false;
                }

                if ((record.Reason & (AllLinkReasons | SingleLinkReasons)) != 0)
                {
                    ApplyLinkImpact(record);
                }
                if ((record.Reason & MembershipReasons) != 0
                    && _recordsByFileId.TryGetValue(record.Parent, out List<Tracking>? parents))
                {
                    foreach (Tracking container in parents)
                    {
                        if (container.Active && container.Enumerated) { container.Version = ++_version; }
                    }
                }

                previous = record.Usn;
                offset += (int)record.Length;
            }

            Cursor = next;
            return true;
        }

        internal bool TryGetVersion(string path, out long version)
        {
            version = 0;
            if (Disabled || !_paths.TryGetValue(Normalize(path), out Tracking? tracking)
                || !tracking.Active || !tracking.Enumerated) { return false; }
            version = tracking.Version;
            return true;
        }

        private void ApplyLinkImpact(Record record)
        {
            if (!_recordsByFileId.TryGetValue(record.Id, out List<Tracking>? records)) { return; }
            Tracking? parentFilter = null;
            if ((record.Reason & AllLinkReasons) == 0)
            {
                if (!_recordsByFileId.TryGetValue(record.Parent, out List<Tracking>? parents)) { return; }
                foreach (Tracking parent in parents)
                {
                    if (!parent.Active) { continue; }
                    if (parentFilter is not null && parentFilter != parent)
                    {
                        parentFilter = null;
                        break;
                    }
                    parentFilter = parent;
                }
                if (parentFilter is null && !HasActiveRecord(parents)) { return; }
            }

            foreach (Tracking tracking in records)
            {
                if (tracking.Active && tracking.Usn < record.Usn
                    && (parentFilter is null || tracking.Parent == parentFilter))
                {
                    Invalidate(tracking);
                }
            }
        }

        private static bool HasActiveRecord(List<Tracking> records)
        {
            foreach (Tracking tracking in records)
            {
                if (tracking.Active) { return true; }
            }
            return false;
        }

        private void Invalidate(Tracking root)
        {
            _invalidations.Enqueue(root);
            while (_invalidations.Count != 0)
            {
                Tracking tracking = _invalidations.Dequeue();
                tracking.Active = tracking.Enumerated = tracking.Anchored = false;
                tracking.Version = ++_version;
                if (tracking.Children is not null)
                {
                    foreach (Tracking child in tracking.Children) { _invalidations.Enqueue(child); }
                }
            }
        }

        private static string Normalize(string path) => path.Length > 3 ? path.TrimEnd('\\') : path;

        private sealed class Tracking(string path)
        {
            internal readonly string Path = path;
            internal DirectoryId Id;
            internal long Usn;
            internal long Version;
            internal bool Active;
            internal bool Anchored;
            internal bool Enumerated;
            internal bool Registered;
            internal Tracking? Parent;
            internal List<Tracking>? Children;
        }

        internal void Disable() => Disabled = true;
    }

    private sealed class Volume(USN_JOURNAL_DATA_V0 info) : IJournalReader, IDisposable
    {
        internal SafeFileHandle Handle = null!;
        internal readonly byte[] Buffer = new byte[BufferSize];
        internal readonly JournalState State = new(info);

        public void Dispose() => Handle.Dispose();

        public bool Query(out USN_JOURNAL_DATA_V0 info) => TryQuery((HANDLE)Handle.DangerousGetHandle(), out info, out _);

        public bool Read(long start, out ReadOnlyMemory<byte> data)
        {
            READ_USN_JOURNAL_DATA_V1 request = new()
            {
                StartUsn = start,
                // BASIC_INFO does not change membership; the raw name enumerators do not filter these attributes.
                ReasonMask = MembershipReasons | AllLinkReasons | SingleLinkReasons | PInvoke.USN_REASON_CLOSE,
                UsnJournalID = State.JournalId,
                MinMajorVersion = 2,
                MaxMajorVersion = 3,
            };
            fixed (byte* pointer = Buffer)
            {
                uint returned;
                if (!PInvoke.DeviceIoControl((HANDLE)Handle.DangerousGetHandle(), PInvoke.FSCTL_READ_UNPRIVILEGED_USN_JOURNAL,
                    &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1), pointer, (uint)Buffer.Length, &returned, null))
                {
                    int error = Marshal.GetLastWin32Error();
                    if (error is (int)WIN32_ERROR.ERROR_INVALID_FUNCTION or (int)WIN32_ERROR.ERROR_NOT_SUPPORTED)
                    {
                        State.Disable();
                    }
                    TraceError("reading unprivileged directory change journal", error);
                    data = default;
                    return false;
                }
                if (returned < sizeof(long) || returned > Buffer.Length)
                {
                    data = ReadOnlyMemory<byte>.Empty;
                    return true;
                }
                data = Buffer.AsMemory(0, (int)returned);
                return true;
            }
        }
    }
}
#endif
