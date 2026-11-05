// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
#if NET
using System.Text;
using Microsoft.Build.Utilities;
#endif
#if NET
using System.Runtime.InteropServices;
using Microsoft.Build.Framework;
#endif

namespace Microsoft.Build.Shared;

internal enum DirectoryStampStatus { Unavailable, Available, Missing }
internal enum DirectorySnapshotStatus { Unavailable, Valid, Mismatch }
internal delegate DirectoryStampStatus DirectoryStampReader(string path, out DirectoryChangeStamp stamp);

internal readonly record struct DirectoryChangeStamp(
    ulong VolumeOrDevice,
    ulong IdentityLow,
    ulong IdentityHigh,
    long Value,
    uint Nanoseconds = 0)
{
    internal bool IsOldEnoughToCache(DateTime utcNow)
    {
        long minimumAgeTicks = GetMinimumAgeTicks(Nanoseconds);
        const long unixEpochTicks = 621355968000000000;
        long cutoff = utcNow.Ticks - unixEpochTicks - minimumAgeTicks;
        long seconds = Math.DivRem(cutoff, TimeSpan.TicksPerSecond, out long ticks);
        if (ticks < 0)
        {
            seconds--;
            ticks += TimeSpan.TicksPerSecond;
        }

        return Value < seconds || (Value == seconds && Nanoseconds <= ticks * 100);
    }

    // Follow Bazel's TimestampGranularityMonitor (bazelbuild/bazel@fad396b9a3d1):
    // whole seconds need 1 s + 50 ms slack, whole milliseconds need 1 ms, finer ctime needs no delay.
    // Instead of waiting at command end, age-rejected or future stamps use fresh enumeration.
    private static long GetMinimumAgeTicks(uint nanoseconds) => nanoseconds switch
    {
        0 => TimeSpan.TicksPerSecond + 50 * TimeSpan.TicksPerMillisecond,
        _ when nanoseconds % 1_000_000 == 0 => TimeSpan.TicksPerMillisecond,
        _ => 0,
    };
}

internal static partial class DirectoryChangeTime
{
    internal static bool TryRead(string path, out DirectoryChangeStamp stamp)
        => Read(path, out stamp, out _) == DirectoryStampStatus.Available;

    internal static DirectoryStampStatus Read(string path, out DirectoryChangeStamp stamp, out bool unsupportedFileSystem)
    {
        unsupportedFileSystem = false;
#if NET
        if (NativeMethods.IsLinux)
        {
            try
            {
                return ReadLinux(path, out stamp, out unsupportedFileSystem);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                DebugTrace.WriteLine($"Directory change-time validation is unavailable: {ex.Message}", category: nameof(DirectoryChangeTime));
            }
        }
#endif
        stamp = default;
        return DirectoryStampStatus.Unavailable;
    }

#if NET
    [Flags]
    private enum StatxFields : uint
    {
        Type = 0x1,
        ChangeTime = 0x80,
        Identity = 0x100,
        MountId = 0x1000,
    }

    [Flags]
    private enum StatxFlags
    {
        NoFollow = 0x100,
        ForceSync = 0x2000,
    }

    private enum UnixFileType : ushort
    {
        Directory = 0x4000,
        Mask = 0xF000,
    }

    // statx has a fixed 256-byte ABI; request type, identity and ctime, not mtime.
    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct StatxData
    {
        [FieldOffset(0)]
        internal StatxFields Fields;
        [FieldOffset(28)]
        internal UnixFileType FileType;
        [FieldOffset(32)]
        internal ulong Identity;
        [FieldOffset(96)]
        internal long ChangeSeconds;
        [FieldOffset(104)]
        internal uint ChangeNanoseconds;
        [FieldOffset(136)]
        internal uint DeviceMajor;
        [FieldOffset(140)]
        internal uint DeviceMinor;
        [FieldOffset(144)]
        internal ulong MountId;
    }

    [LibraryImport("libc", EntryPoint = "statx", SetLastError = true)]
    private static unsafe partial int Statx(int directoryDescriptor, byte* path, StatxFlags flags, StatxFields fields, StatxData* result);

    [LibraryImport("libc", EntryPoint = "statfs", SetLastError = true)]
    private static unsafe partial int Statfs(byte* path, void* result);

    private static unsafe DirectoryStampStatus ReadLinux(string path, out DirectoryChangeStamp stamp, out bool unsupportedFileSystem)
    {
        stamp = default;
        unsupportedFileSystem = false;
        const int currentDirectory = -100; // AT_FDCWD
        const StatxFields required = StatxFields.Type | StatxFields.ChangeTime | StatxFields.Identity | StatxFields.MountId;
        int length = Encoding.UTF8.GetByteCount(path);
        using BufferScope<byte> buffer = new(stackalloc byte[512], checked(length + 1));
        Encoding.UTF8.GetBytes(path.AsSpan(), buffer.AsSpan());
        buffer[length] = 0;
        StatxData data;
        int result;
        using BufferScope<byte> fileSystem = new(stackalloc byte[256]);
        fixed (byte* pointer = buffer)
        {
            fixed (byte* information = fileSystem)
            {
                if (Statfs(pointer, information) != 0)
                {
                    return MissingOrUnavailable(Marshal.GetLastPInvokeError());
                }

                if (!IsSupportedLinuxFileSystem(unchecked((uint)MemoryMarshal.Read<nint>(fileSystem.AsSpan()))))
                {
                    unsupportedFileSystem = true;
                    return DirectoryStampStatus.Unavailable;
                }
            }

            result = Statx(currentDirectory, pointer, StatxFlags.NoFollow | StatxFlags.ForceSync, required, &data);
        }
        if (result != 0)
        {
            int error = Marshal.GetLastPInvokeError();
            TraceFailure(path, error);
            return MissingOrUnavailable(error);
        }
        if ((data.Fields & required) != required
            || (data.FileType & UnixFileType.Mask) != UnixFileType.Directory
            || data.ChangeNanoseconds >= 1_000_000_000)
        {
            DebugTrace.WriteLine($"Directory change-time metadata is incomplete for '{path}'.", category: nameof(DirectoryChangeTime));
            return DirectoryStampStatus.Unavailable;
        }
        stamp = new DirectoryChangeStamp(
            ((ulong)data.DeviceMajor << 32) | data.DeviceMinor,
            data.Identity,
            data.MountId,
            data.ChangeSeconds,
            data.ChangeNanoseconds);
        return DirectoryStampStatus.Available;
    }

    private static DirectoryStampStatus MissingOrUnavailable(int error)
        => error is 2 or 20 ? DirectoryStampStatus.Missing : DirectoryStampStatus.Unavailable;

    // Filesystem identification is an admission policy, not proof of timestamp uniqueness.
    private static bool IsSupportedLinuxFileSystem(uint type) => type is
        0xEF53       // ext2/ext3/ext4
        or 0x58465342 // XFS
        or 0x9123683E // Btrfs
        or 0x01021994; // tmpfs
#endif

#if NET
    private static void TraceFailure(string path, int error)
        => DebugTrace.WriteLine($"Could not read directory change time for '{path}': native error {error}.", category: nameof(DirectoryChangeTime));
#endif
}
