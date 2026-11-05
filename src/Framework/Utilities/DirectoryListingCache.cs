// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using Microsoft.Build.Framework;

namespace Microsoft.Build.Shared;

/// <summary>
/// Caches directory listings for reuse across project evaluations and builds.
/// </summary>
internal sealed class DirectoryListingCache
{
    private const int MaximumUnsupportedDirectories = 65536;
    private readonly ConcurrentDictionary<(string Directory, FileMatcher.FileSystemEntity Kind), Listing> _listings = new();
    private readonly string[] _excludedRoots;
    private readonly DirectoryStampReader _readStamp;
    private readonly ConcurrentDictionary<string, byte> _unsupportedDirectories = new(FileUtilities.PathComparer);
#if FEATURE_WINDOWSINTEROP
    private readonly DirectoryJournal? _journal;
#endif

    internal DirectoryListingCache() : this(null)
    {
    }

    internal DirectoryListingCache(DirectoryStampReader? readStamp)
    {
        string? excludedRoots = Environment.GetEnvironmentVariable("MSBUILDDIRECTORYCACHEEXCLUDEROOTS");
        _excludedRoots = string.IsNullOrEmpty(excludedRoots)
            ? []
            : excludedRoots.Split([Path.PathSeparator], StringSplitOptions.RemoveEmptyEntries);
        try
        {
            for (int i = 0; i < _excludedRoots.Length; i++)
            {
                _excludedRoots[i] = Path.GetFullPath(_excludedRoots[i].Trim())
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            }
        }
        catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
        {
            _excludedRoots = [""];
        }

#if FEATURE_WINDOWSINTEROP
        if (NativeMethods.IsWindows)
        {
            _journal = new DirectoryJournal();
        }
#endif
        _readStamp = readStamp ?? ReadNativeStamp;
    }

    internal bool CanStore(DirectoryChangeStamp stamp, DateTime utcNow)
        => NativeMethods.IsWindows || stamp.IsOldEnoughToCache(utcNow);

    internal IReadOnlyList<string> Store(string directory, FileMatcher.FileSystemEntity kind, IReadOnlyList<string> entries, DirectoryChangeStamp changeTime)
    {
        var key = (directory, kind);
        IReadOnlyList<string>? snapshot = null;
        while (true)
        {
            bool exists = _listings.TryGetValue(key, out Listing current);
            if (exists && current.ChangeTime == changeTime)
            {
                return current.Entries;
            }

            snapshot ??= entries is ImmutableArray<string> ? entries : ImmutableArray.CreateRange(entries);
            var replacement = new Listing(snapshot, changeTime);
            if (exists
                ? _listings.TryUpdate(key, replacement, current)
                : _listings.TryAdd(key, replacement))
            {
                return snapshot;
            }
        }
    }

    internal bool TryGet(string directory, FileMatcher.FileSystemEntity kind, [NotNullWhen(true)] out IReadOnlyList<string>? entries)
    {
        if (_listings.TryGetValue((directory, kind), out Listing listing)
            && IsUpToDate(directory, listing))
        {
            entries = listing.Entries;
            return true;
        }

        entries = null;
        return false;
    }

    internal bool IsSnapshot(string directory, FileMatcher.FileSystemEntity kind, IReadOnlyList<string> entries)
        => _listings.TryGetValue((directory, kind), out Listing listing) && ReferenceEquals(listing.Entries, entries);

    internal DirectorySnapshotStatus TryValidate(string directory, FileMatcher.FileSystemEntity kind, IReadOnlyList<string> entries)
    {
        if (!_listings.TryGetValue((directory, kind), out Listing listing) || !ReferenceEquals(listing.Entries, entries))
        {
            return DirectorySnapshotStatus.Mismatch;
        }

        return Validate(directory, listing);
    }

    private bool IsUpToDate(string directory, Listing listing)
        => Validate(directory, listing) == DirectorySnapshotStatus.Valid;

    private DirectorySnapshotStatus Validate(string directory, Listing listing)
    {
        DirectoryStampStatus status = ReadStamp(directory, out DirectoryChangeStamp current);
        if (status == DirectoryStampStatus.Unavailable)
        {
            return DirectorySnapshotStatus.Unavailable;
        }

        if (status == DirectoryStampStatus.Available && listing.ChangeTime == current)
        {
            return DirectorySnapshotStatus.Valid;
        }

        _listings.TryRemove((directory, FileMatcher.FileSystemEntity.Files), out _);
        _listings.TryRemove((directory, FileMatcher.FileSystemEntity.Directories), out _);
        _listings.TryRemove((directory, FileMatcher.FileSystemEntity.FilesAndDirectories), out _);
        return DirectorySnapshotStatus.Mismatch;
    }

    internal bool TryReadDirectoryChangeTime(string directory, out DirectoryChangeStamp changeTime)
        => ReadStamp(directory, out changeTime) == DirectoryStampStatus.Available;

    internal bool CanAttemptSharing(string directory)
    {
        if (_unsupportedDirectories.ContainsKey(directory) || IsExcluded(directory))
        {
            return false;
        }
#if FEATURE_WINDOWSINTEROP
        if (NativeMethods.IsWindows && _journal is not null) { return _journal.CanAttemptSharing(directory); }
#endif
        return true;
    }

    private DirectoryStampStatus ReadStamp(string directory, out DirectoryChangeStamp changeTime)
    {
        try
        {
            if (!CanAttemptSharing(directory))
            {
                changeTime = default;
                return DirectoryStampStatus.Unavailable;
            }

            return _readStamp(directory, out changeTime);
        }
        catch (Exception ex) when (ExceptionHandling.IsIoRelatedException(ex))
        {
            DebugTrace.WriteLine($"Could not read directory change time for '{directory}': {ex.Message}", category: nameof(DirectoryListingCache));
            changeTime = default;
            return DirectoryStampStatus.Unavailable;
        }
    }

    private DirectoryStampStatus ReadNativeStamp(string directory, out DirectoryChangeStamp stamp)
    {
#if FEATURE_WINDOWSINTEROP
        if (NativeMethods.IsWindows && _journal is not null) { return _journal.Read(directory, out stamp); }
#endif
        DirectoryStampStatus status = DirectoryChangeTime.Read(directory, out stamp, out bool unsupportedFileSystem);
        if (unsupportedFileSystem && _unsupportedDirectories.Count < MaximumUnsupportedDirectories)
        {
            _unsupportedDirectories.TryAdd(directory, 0);
        }
        return status;
    }

    internal bool IsExcluded(string directory)
    {
        if (_excludedRoots.Length == 0)
        {
            return false;
        }

        string fullPath = Path.GetFullPath(directory);
        foreach (string root in _excludedRoots)
        {
            if (fullPath.StartsWith(root, FileUtilities.PathComparison)
                && (root.Length == 0 || fullPath.Length == root.Length || FileUtilities.IsAnySlash(fullPath[root.Length])))
            {
                return true;
            }
        }

        return false;
    }

    internal void Clear()
    {
        _listings.Clear();
        _unsupportedDirectories.Clear();
        GlobResultExperiment.Clear(this);
#if FEATURE_WINDOWSINTEROP
        if (NativeMethods.IsWindows) { _journal?.Clear(); }
#endif
    }

    private readonly record struct Listing(IReadOnlyList<string> Entries, DirectoryChangeStamp ChangeTime);
}
