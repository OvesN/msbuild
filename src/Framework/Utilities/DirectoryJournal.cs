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
        | PInvoke.USN_REASON_RENAME_OLD_NAME | PInvoke.USN_REASON_RENAME_NEW_NAME;
    private const uint GlobalReasons = PInvoke.USN_REASON_HARD_LINK_CHANGE | PInvoke.USN_REASON_REPARSE_POINT_CHANGE
        | PInvoke.USN_REASON_SECURITY_CHANGE | PInvoke.USN_REASON_FILE_DELETE
        | PInvoke.USN_REASON_RENAME_OLD_NAME | PInvoke.USN_REASON_RENAME_NEW_NAME;
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
    internal readonly record struct Statistics(long NativeReads, long Stamps, long NegativeHits, long AncestorWalks, long Deferrals);
    private readonly record struct Qualification(Guid Volume, DirectoryId Id, ulong Epoch);

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

            READ_FILE_USN_DATA request = new() { MinMajorVersion = 2, MaxMajorVersion = 3 };
            using BufferScope<byte> recordBuffer = new(stackalloc byte[1024]);
            uint returned;
            Record record;
            fixed (byte* pointer = recordBuffer)
            {
                if (!PInvoke.DeviceIoControl(directory, PInvoke.FSCTL_READ_FILE_USN_DATA,
                    &request, (uint)sizeof(READ_FILE_USN_DATA), pointer, (uint)recordBuffer.Length, &returned, null))
                {
                    TraceError(path);
                    return DirectoryStampStatus.Unavailable;
                }

                if (returned > recordBuffer.Length || !TryParse(recordBuffer.AsSpan().Slice(0, (int)returned), out record)
                    || (record.Attributes & (uint)(FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT))
                    != (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY)
                {
                    return DirectoryStampStatus.Unavailable;
                }
            }

            if (Qualify(path, volumeId, record.Id, volume.State, volume, HasOrdinaryAncestors, out long version) != DirectoryStampStatus.Available)
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
        Func<string, bool> walkAncestors, out long version)
    {
        int remainingReadBytes = MaximumReadBytes;
        if (CatchUp(state, reader, id, out version, ref remainingReadBytes) != DirectoryStampStatus.Available)
        {
            return DirectoryStampStatus.Unavailable;
        }

        ulong epoch = state.Epoch;
        var qualification = new Qualification(volumeId, id, epoch);
        if (_qualifications.TryGetValue(path, out Qualification current) && current == qualification)
        {
            return DirectoryStampStatus.Available;
        }

        _ancestorWalks++;
        if (!walkAncestors(path)
            || CatchUp(state, reader, id, out version, ref remainingReadBytes) != DirectoryStampStatus.Available
            || state.Epoch != epoch)
        {
            return DirectoryStampStatus.Unavailable;
        }

        if (_qualifications.Count >= MaximumQualifications) { _qualifications.Clear(); }
        _qualifications[path] = qualification;
        return DirectoryStampStatus.Available;
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
            handle = PInvoke.CreateFile(new PCWSTR(pointer), (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ,
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
            if (!JournalState.HasCompleteHistory(info))
            {
                rejection = RejectionReason.IncompleteHistory;
                return null;
            }
            if (info.NextUsn > MaximumReadBytes)
            {
                rejection = RejectionReason.InitialHistoryTooLarge;
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

    internal static DirectoryStampStatus CatchUp(JournalState state, IJournalReader reader, DirectoryId id, out long version,
        int maximumReadBytes = MaximumReadBytes)
        => CatchUp(state, reader, id, out version, ref maximumReadBytes);

    private static DirectoryStampStatus CatchUp(JournalState state, IJournalReader reader, DirectoryId id, out long version,
        ref int remainingReadBytes)
    {
        version = 0;
        if (state.Disabled) { return DirectoryStampStatus.Unavailable; }
        if (!reader.Query(out USN_JOURNAL_DATA_V0 info)) { return DirectoryStampStatus.Unavailable; }
        if (!state.ObserveHead(info)) { return DirectoryStampStatus.Unavailable; }
        state.Track(id);
        for (int pass = 0; pass < 3; pass++)
        {
            while (state.Cursor < info.NextUsn)
            {
                if (remainingReadBytes < sizeof(long) || !reader.Read(state.Cursor, out ReadOnlyMemory<byte> data))
                {
                    return DirectoryStampStatus.Unavailable;
                }
                remainingReadBytes -= data.Length;
                if (remainingReadBytes < 0) { return DirectoryStampStatus.Unavailable; }
                if (!state.ApplyBuffer(data.Span))
                {
                    state.Disable();
                    return DirectoryStampStatus.Unavailable;
                }
            }

            if (!reader.Query(out info)) { return DirectoryStampStatus.Unavailable; }
            if (!state.ObserveHead(info)) { return DirectoryStampStatus.Unavailable; }
            if (state.Cursor == info.NextUsn)
            {
                return state.TryGetVersion(id, out version) ? DirectoryStampStatus.Available : DirectoryStampStatus.Unavailable;
            }
        }

        return DirectoryStampStatus.Unavailable;
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
        private readonly Dictionary<DirectoryId, long> _versions = new();
        private readonly Dictionary<DirectoryId, DirectoryId> _pendingCreates = new();
        private readonly Dictionary<DirectoryId, int> _pendingParents = new();
        private readonly HashSet<DirectoryId> _globalPending = new();
        private bool _requiresCompleteHistory;
        internal readonly ulong JournalId;
        internal long Cursor { get; private set; }
        internal ulong Epoch { get; private set; } = NextEpoch();
        internal bool Disabled { get; private set; }

        internal JournalState(USN_JOURNAL_DATA_V0 info)
        {
            JournalId = info.UsnJournalID;
            Cursor = info.FirstUsn;
            Disabled = !HasCompleteHistory(info);
        }

        // Otherwise an unclosed, coalesced change may predate the first retained record.
        internal static bool HasCompleteHistory(USN_JOURNAL_DATA_V0 info)
            => info.FirstUsn == 0 && info.LowestValidUsn == 0 && info.NextUsn >= 0;

        internal bool IsContinuous(USN_JOURNAL_DATA_V0 info)
            => !Disabled && info.UsnJournalID == JournalId
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

        internal void Track(DirectoryId id)
        {
            if (!_versions.ContainsKey(id))
            {
                _versions.Add(id, 0);
            }
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

                if ((record.Reason & GlobalReasons) != 0)
                {
                    Epoch = NextEpoch();
                    RemovePendingCreate(record.Id);
                    _globalPending.Add(record.Id);
                }
                else if ((record.Reason & PInvoke.USN_REASON_FILE_CREATE) != 0 && !_globalPending.Contains(record.Id))
                {
                    if (!_pendingCreates.TryGetValue(record.Id, out DirectoryId parent))
                    {
                        _pendingCreates.Add(record.Id, record.Parent);
                        _pendingParents.TryGetValue(record.Parent, out int count);
                        _pendingParents[record.Parent] = count + 1;
                    }
                    else if (parent != record.Parent)
                    {
                        RemovePendingCreate(record.Id);
                        _globalPending.Add(record.Id);
                        Epoch = NextEpoch();
                    }
                }

                if ((record.Reason & (MembershipReasons | GlobalReasons)) != 0)
                {
                    if (_versions.ContainsKey(record.Parent))
                    {
                        _versions[record.Parent] = record.Usn + 1;
                    }

                    if (_versions.ContainsKey(record.Id))
                    {
                        _versions[record.Id] = record.Usn + 1;
                    }
                }

                if ((record.Reason & PInvoke.USN_REASON_CLOSE) != 0)
                {
                    RemovePendingCreate(record.Id);
                    _globalPending.Remove(record.Id);
                }

                previous = record.Usn;
                offset += (int)record.Length;
            }

            Cursor = next;
            return true;
        }

        internal bool TryGetVersion(DirectoryId id, out long version)
        {
            version = 0;
            return !Disabled && _globalPending.Count == 0 && !_pendingParents.ContainsKey(id)
                && _versions.TryGetValue(id, out version);
        }

        private void RemovePendingCreate(DirectoryId file)
        {
            if (_pendingCreates.TryGetValue(file, out DirectoryId parent))
            {
                _pendingCreates.Remove(file);
                int count = _pendingParents[parent];
                if (count == 1) { _pendingParents.Remove(parent); }
                else { _pendingParents[parent] = count - 1; }
            }
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
                ReasonMask = MembershipReasons | GlobalReasons | PInvoke.USN_REASON_CLOSE,
                UsnJournalID = State.JournalId,
                MinMajorVersion = 2,
                MaxMajorVersion = 3,
            };
            fixed (byte* pointer = Buffer)
            {
                uint returned;
                if (!PInvoke.DeviceIoControl((HANDLE)Handle.DangerousGetHandle(), PInvoke.FSCTL_READ_USN_JOURNAL,
                    &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1), pointer, (uint)Buffer.Length, &returned, null))
                {
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
