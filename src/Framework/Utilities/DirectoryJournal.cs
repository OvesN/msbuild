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
    private const uint MembershipReasons = PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_FILE_DELETE
        | PInvoke.USN_REASON_RENAME_OLD_NAME | PInvoke.USN_REASON_RENAME_NEW_NAME;
    private const uint GlobalReasons = PInvoke.USN_REASON_HARD_LINK_CHANGE | PInvoke.USN_REASON_REPARSE_POINT_CHANGE
        | PInvoke.USN_REASON_SECURITY_CHANGE | PInvoke.USN_REASON_BASIC_INFO_CHANGE;
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Volume?> _volumes = new();
    private static long s_epoch;

    internal bool TryRead(string path, out DirectoryChangeStamp stamp)
    {
        stamp = default;
        if (!IsSupportedPath(path))
        {
            return false;
        }

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
            TraceError(path);
            return false;
        }

        try
        {
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
                    return false;
                }

                if (returned > recordBuffer.Length || !TryParse(recordBuffer.AsSpan().Slice(0, (int)returned), out record)
                    || (record.Attributes & (uint)(FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT))
                    != (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY)
                {
                    return false;
                }
            }

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
                return false;
            }

            ReadOnlySpan<char> resolved = finalPath.AsSpan().Slice(0, (int)length);
            int end = resolved.IndexOf('}');
            if (!resolved.StartsWith(@"\\?\Volume{".AsSpan(), StringComparison.OrdinalIgnoreCase) || end < 0
                || !resolved.Slice(end + 1).TrimEnd('\\').Equals(Path.GetFullPath(path).AsSpan().Slice(2).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

#if NET
            bool parsed = Guid.TryParse(resolved.Slice(11, end - 11), out Guid volumeId);
#else
            bool parsed = Guid.TryParse(resolved.Slice(11, end - 11).ToString(), out Guid volumeId);
#endif
            if (!parsed)
            {
                return false;
            }

            lock (_gate)
            {
                if (!_volumes.TryGetValue(volumeId, out Volume? volume))
                {
                    volume = OpenVolume(resolved.Slice(0, end + 1).ToString(), directory);
                    _volumes.Add(volumeId, volume);
                }

                if (volume is null || !TryGetVersion(volume, record.Id, out long version))
                {
                    return false;
                }

                stamp = new DirectoryChangeStamp(volume.State.Epoch, record.Id.Low, record.Id.High, version);
                return true;
            }
        }
        finally
        {
            if (!PInvoke.CloseHandle(directory))
            {
                TraceError(path);
            }
        }
    }

    internal static bool IsSupportedPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        if (fullPath.Length < 3 || fullPath[1] != ':' || fullPath[2] != '\\')
        {
            return false;
        }

        fixed (char* pointer = fullPath.Substring(0, 3))
        {
            if (PInvoke.GetDriveType(new PCWSTR(pointer)) != PInvoke.DRIVE_FIXED)
            {
                return false;
            }
        }

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

    private static Volume? OpenVolume(string path, HANDLE directory)
    {
        using BufferScope<char> fileSystem = new(stackalloc char[32]);
        uint flags;
        fixed (char* name = fileSystem)
        {
            if (!PInvoke.GetVolumeInformationByHandle(directory, null, 0, null, null, &flags,
                new PWSTR(name), (uint)fileSystem.Length)
                || (flags & PInvoke.FILE_SUPPORTS_USN_JOURNAL) == 0
                || !fileSystem.AsSpan().Slice(0, 5).SequenceEqual("NTFS\0".AsSpan()))
            {
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
            TraceError(path);
            return null;
        }

        SafeFileHandle? owner = null;
        try
        {
            owner = new((nint)handle.Value, ownsHandle: true);
            if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 info) || !JournalState.HasCompleteHistory(info))
            {
                return null;
            }

            var volume = new Volume(owner, info);
            owner = null;
            return volume;
        }
        finally
        {
            owner?.Dispose();
        }
    }

    private static bool TryGetVersion(Volume volume, DirectoryId id, out long version)
    {
        version = 0;
        JournalState state = volume.State;
        if (state.Disabled)
        {
            return false;
        }

        HANDLE handle = (HANDLE)volume.Handle.DangerousGetHandle();
        if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 info) || !state.IsContinuous(info))
        {
            volume.Disable();
            return false;
        }

        state.Track(id);
        long target = info.NextUsn;
        int readBytes = 0;
        READ_USN_JOURNAL_DATA_V1 request = new()
        {
            ReasonMask = MembershipReasons | GlobalReasons | PInvoke.USN_REASON_CLOSE,
            UsnJournalID = state.JournalId,
            MinMajorVersion = 2,
            MaxMajorVersion = 3,
        };
        fixed (byte* pointer = volume.Buffer)
        {
            while (state.Cursor < target)
            {
                request.StartUsn = state.Cursor;
                uint returned;
                if (!PInvoke.DeviceIoControl(handle, PInvoke.FSCTL_READ_USN_JOURNAL,
                    &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1), pointer, (uint)volume.Buffer.Length, &returned, null)
                    || returned < sizeof(long) || returned > volume.Buffer.Length
                    || (readBytes += (int)returned) > MaximumReadBytes
                    || !state.ApplyBuffer(volume.Buffer.AsSpan(0, (int)returned)))
                {
                    volume.Disable();
                    return false;
                }
            }
        }

        if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 after) || !state.IsContinuous(after))
        {
            volume.Disable();
            return false;
        }

        return state.TryGetVersion(id, out version);
    }

    private static bool TryQuery(HANDLE handle, out USN_JOURNAL_DATA_V0 info)
    {
        info = default;
        uint returned;
        fixed (USN_JOURNAL_DATA_V0* pointer = &info)
        {
            if (!PInvoke.DeviceIoControl(handle, PInvoke.FSCTL_QUERY_USN_JOURNAL, null, 0,
                pointer, (uint)sizeof(USN_JOURNAL_DATA_V0), &returned, null)
                || returned < sizeof(USN_JOURNAL_DATA_V0))
            {
                TraceError("querying existing directory change journal");
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
            foreach (Volume? volume in _volumes.Values)
            {
                volume?.Handle.Dispose();
            }

            _volumes.Clear();
        }
    }

    private static ulong NextEpoch() => unchecked((ulong)Interlocked.Increment(ref s_epoch));

    private static void TraceError(string path)
        => DebugTrace.WriteLine($"Directory journal validation failed for '{path}': Win32 {Marshal.GetLastWin32Error()}.", category: nameof(DirectoryJournal));

    internal readonly record struct DirectoryId(ulong Low, ulong High = 0);
    internal readonly record struct Record(uint Length, DirectoryId Id, DirectoryId Parent, long Usn, uint Reason, uint Attributes);

    internal sealed class JournalState
    {
        private readonly Dictionary<DirectoryId, long> _versions = new();
        private readonly HashSet<DirectoryId> _pending = new();
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
                && Cursor >= info.FirstUsn && Cursor >= info.LowestValidUsn && Cursor <= info.NextUsn;

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

            for (int offset = sizeof(long); offset < data.Length;)
            {
                if (!TryParse(data.Slice(offset), out Record record) || record.Usn <= previous || record.Usn >= next)
                {
                    return false;
                }

                if ((record.Reason & GlobalReasons) != 0)
                {
                    Epoch = NextEpoch();
                }

                if ((record.Reason & (MembershipReasons | GlobalReasons)) != 0)
                {
                    _pending.Add(record.Id);
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
                    _pending.Remove(record.Id);
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
            return !Disabled && _pending.Count == 0 && _versions.TryGetValue(id, out version);
        }

        internal void Disable() => Disabled = true;
    }

    private sealed class Volume(SafeFileHandle handle, USN_JOURNAL_DATA_V0 info)
    {
        internal readonly SafeFileHandle Handle = handle;
        internal readonly byte[] Buffer = new byte[BufferSize];
        internal readonly JournalState State = new(info);

        internal void Disable()
        {
            State.Disable();
            Handle.Dispose();
        }
    }
}
#endif
