// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if FEATURE_WINDOWSINTEROP
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.FileSystem;
using Windows.Win32.System.Ioctl;

namespace Microsoft.Build.Shared;

[SupportedOSPlatform("windows6.1")]
internal sealed unsafe class DirectoryJournal
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, Volume> _volumes = new();
    private static long s_epoch;
    private const int BufferSize = 65536;
    private const uint MembershipReasons = PInvoke.USN_REASON_FILE_CREATE | PInvoke.USN_REASON_FILE_DELETE
        | PInvoke.USN_REASON_RENAME_OLD_NAME | PInvoke.USN_REASON_RENAME_NEW_NAME;
    private const uint GlobalReasons = PInvoke.USN_REASON_HARD_LINK_CHANGE | PInvoke.USN_REASON_REPARSE_POINT_CHANGE;

    internal bool TryRead(string path, out DirectoryChangeStamp stamp)
    {
        stamp = default;
        HANDLE directory;
        fixed (char* pointer = path)
        {
            directory = PInvoke.CreateFile(new PCWSTR(pointer),
                (uint)(FILE_ACCESS_RIGHTS.FILE_READ_ATTRIBUTES | FILE_ACCESS_RIGHTS.FILE_LIST_DIRECTORY),
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAGS_AND_ATTRIBUTES.FILE_FLAG_OPEN_REPARSE_POINT, HANDLE.Null);
        }
        if (directory == HANDLE.INVALID_HANDLE_VALUE) { TraceError(path); return false; }
        try
        {
            READ_FILE_USN_DATA request = new() { MinMajorVersion = 2, MaxMajorVersion = 3 };
            using BufferScope<byte> recordBuffer = new(stackalloc byte[1024]);
            uint returned;
            fixed (byte* pointer = recordBuffer)
            {
                if (!PInvoke.DeviceIoControl(directory, PInvoke.FSCTL_READ_FILE_USN_DATA,
                    &request, (uint)sizeof(READ_FILE_USN_DATA), pointer, (uint)recordBuffer.Length, &returned, null))
                {
                    TraceError(path);
                    return false;
                }
                if (returned > recordBuffer.Length || !TryParse(pointer, returned, out Record record)
                    || (record.Attributes & (uint)(FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY | FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_REPARSE_POINT))
                    != (uint)FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_DIRECTORY)
                {
                    DebugTrace.WriteLine($"Directory journal metadata is unsupported for '{path}'.", category: nameof(DirectoryJournal));
                    return false;
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
                if (length == 0 || length >= finalPath.Length) { TraceError(path); return false; }
                ReadOnlySpan<char> resolved = finalPath.AsSpan().Slice(0, (int)length);
                int end = resolved.IndexOf('}');
                if (!resolved.StartsWith(@"\\?\Volume{".AsSpan(), StringComparison.OrdinalIgnoreCase) || end < 0)
                {
                    DebugTrace.WriteLine($"Directory journal volume is unsupported for '{path}'.", category: nameof(DirectoryJournal));
                    return false;
                }
#if NET
                bool parsed = Guid.TryParse(resolved.Slice(11, end - 11), out Guid volumeId);
#else
                bool parsed = Guid.TryParse(resolved.Slice(11, end - 11).ToString(), out Guid volumeId);
#endif
                if (!parsed)
                {
                    DebugTrace.WriteLine($"Invalid directory journal volume for '{path}'.", category: nameof(DirectoryJournal));
                    return false;
                }
                lock (_gate)
                {
                    if (!_volumes.TryGetValue(volumeId, out Volume? volume))
                    {
                        volume = OpenVolume(resolved.Slice(0, end + 1).ToString());
                        if (volume is null) { return false; }
                        _volumes.Add(volumeId, volume);
                    }
                    if (!TryGetVersion(volume, record.Id, out long version)) { return false; }
                    stamp = new DirectoryChangeStamp(volume.Epoch, record.Id.Low, record.Id.High, version);
                    return true;
                }
            }
        }
        finally
        {
            if (!PInvoke.CloseHandle(directory)) { TraceError(path); }
        }
    }

    private static Volume? OpenVolume(string path)
    {
        HANDLE handle;
        fixed (char* pointer = path)
        {
            handle = PInvoke.CreateFile(new PCWSTR(pointer), (uint)FILE_ACCESS_RIGHTS.FILE_GENERIC_READ,
                FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE | FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null, FILE_CREATION_DISPOSITION.OPEN_EXISTING, 0, HANDLE.Null);
        }
        if (handle == HANDLE.INVALID_HANDLE_VALUE) { TraceError(path); return null; }
        SafeFileHandle owner = new((nint)handle.Value, ownsHandle: true);
        if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 info))
        {
            owner.Dispose();
            return null;
        }
        return new Volume(owner, info);
    }

    private static bool TryGetVersion(Volume volume, DirectoryId id, out long version)
    {
        version = 0;
        if (volume.Disabled) { return false; }
        HANDLE handle = (HANDLE)volume.Handle.DangerousGetHandle();
        if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 info)) { volume.Disable(); return false; }
        if (info.UsnJournalID != volume.JournalId || volume.Cursor < info.FirstUsn || volume.Cursor < info.LowestValidUsn || volume.Cursor > info.NextUsn)
        {
            if (volume.PendingLinks.Count != 0) { volume.Disable(); return false; }
            volume.Reset(info);
        }
        if (!volume.Versions.ContainsKey(id)) { volume.Versions.Add(id, 0); }
        long target = info.NextUsn;
        if (volume.Cursor < target)
        {
            READ_USN_JOURNAL_DATA_V1 request = new()
            {
                StartUsn = volume.Cursor,
                ReasonMask = MembershipReasons | GlobalReasons | PInvoke.USN_REASON_CLOSE,
                UsnJournalID = volume.JournalId,
                MinMajorVersion = 2,
                MaxMajorVersion = 3,
            };
            byte[] buffer = volume.Buffer;
            fixed (byte* pointer = buffer)
            {
                while (volume.Cursor < target)
                {
                    request.StartUsn = volume.Cursor;
                    uint returned;
                    if (!PInvoke.DeviceIoControl(handle, PInvoke.FSCTL_READ_USN_JOURNAL,
                        &request, (uint)sizeof(READ_USN_JOURNAL_DATA_V1), pointer, (uint)buffer.Length, &returned, null)
                        || returned < sizeof(long) || returned > buffer.Length)
                    {
                        TraceError("reading directory change journal");
                        volume.Disable();
                        return false;
                    }
                    long next = *(long*)pointer;
                    for (uint offset = sizeof(long); offset < returned;)
                    {
                        if (!TryParse(pointer + offset, returned - offset, out Record record))
                        {
                            DebugTrace.WriteLine("Unsupported directory change journal record.", category: nameof(DirectoryJournal));
                            volume.Disable();
                            return false;
                        }
                        if ((record.Reason & GlobalReasons) != 0)
                        {
                            // Hard-link records may not identify every affected parent.
                            volume.Epoch = NextEpoch();
                        }
                        if ((record.Reason & PInvoke.USN_REASON_HARD_LINK_CHANGE) != 0)
                        {
                            volume.PendingLinks.Add(record.Id);
                        }
                        if ((record.Reason & PInvoke.USN_REASON_CLOSE) != 0)
                        {
                            volume.PendingLinks.Remove(record.Id);
                        }
                        if ((record.Reason & MembershipReasons) != 0)
                        {
                            if (volume.Versions.ContainsKey(record.Parent)) { volume.Versions[record.Parent] = record.Usn; }
                            if (volume.Versions.ContainsKey(record.Id)) { volume.Versions[record.Id] = record.Usn; }
                        }
                        offset += record.Length;
                    }
                    if (next <= volume.Cursor)
                    {
                        DebugTrace.WriteLine("Directory change journal cursor made no progress.", category: nameof(DirectoryJournal));
                        volume.Disable();
                        return false;
                    }
                    volume.Cursor = next;
                }
            }
            if (!TryQuery(handle, out USN_JOURNAL_DATA_V0 after))
            {
                volume.Disable();
                return false;
            }
            if (after.UsnJournalID != volume.JournalId || volume.Cursor < after.FirstUsn || volume.Cursor < after.LowestValidUsn || volume.Cursor > after.NextUsn)
            {
                if (volume.PendingLinks.Count != 0) { volume.Disable(); return false; }
                volume.Reset(after);
                volume.Versions.Add(id, 0);
            }
        }
        if (volume.PendingLinks.Count != 0)
        {
            DebugTrace.WriteLine("Directory journal sharing is suspended until ambiguous hard-link changes close.", category: nameof(DirectoryJournal));
            return false;
        }
        version = volume.Versions[id];
        return true;
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

    private static bool TryParse(byte* data, uint available, out Record record)
    {
        record = default;
        if (available < 8) { return false; }
        uint length = *(uint*)data;
        ushort major = *(ushort*)(data + 4);
        uint minimum = major switch { 2 => 60, 3 => 76, _ => uint.MaxValue };
        if (length < minimum || length > available || (length & 7) != 0) { return false; }
        if (major == 2)
        {
            USN_RECORD_V2* entry = (USN_RECORD_V2*)data;
            record = new(length, new(entry->FileReferenceNumber, 0), new(entry->ParentFileReferenceNumber, 0),
                entry->Usn, entry->Reason, entry->FileAttributes);
        }
        else
        {
            USN_RECORD_V3* entry = (USN_RECORD_V3*)data;
            ulong* file = (ulong*)&entry->FileReferenceNumber;
            ulong* parent = (ulong*)&entry->ParentFileReferenceNumber;
            record = new(length, new(file[0], file[1]), new(parent[0], parent[1]),
                entry->Usn, entry->Reason, entry->FileAttributes);
        }
        return true;
    }

    internal void Clear()
    {
        lock (_gate)
        {
            foreach (Volume volume in _volumes.Values) { volume.Handle.Dispose(); }
            _volumes.Clear();
        }
    }

    private static ulong NextEpoch() => unchecked((ulong)Interlocked.Increment(ref s_epoch));

    private static void TraceError(string path)
        => DebugTrace.WriteLine($"Directory journal validation failed for '{path}': Win32 {Marshal.GetLastWin32Error()}.", category: nameof(DirectoryJournal));

    private readonly record struct DirectoryId(ulong Low, ulong High);
    private readonly record struct Record(uint Length, DirectoryId Id, DirectoryId Parent, long Usn, uint Reason, uint Attributes);

    private sealed class Volume
    {
        internal readonly SafeFileHandle Handle;
        internal readonly byte[] Buffer = new byte[BufferSize];
        internal readonly Dictionary<DirectoryId, long> Versions = new();
        internal readonly HashSet<DirectoryId> PendingLinks = new();
        internal ulong JournalId;
        internal long Cursor;
        internal ulong Epoch;
        internal bool Disabled;

        internal Volume(SafeFileHandle handle, USN_JOURNAL_DATA_V0 info)
        {
            Handle = handle;
            Reset(info);
        }

        internal void Reset(USN_JOURNAL_DATA_V0 info)
        {
            JournalId = info.UsnJournalID;
            Cursor = info.NextUsn;
            Epoch = NextEpoch();
            Versions.Clear();
        }

        internal void Disable()
        {
            Disabled = true;
            Handle.Dispose();
        }
    }
}
#endif
