// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

#if FEATURE_WINDOWSINTEROP
using Microsoft.Build.Framework;
#endif

namespace Microsoft.Build.Shared.FileSystem
{
    internal interface IFileSystemProviderIdentity
    {
        string ProviderIdentity { get; }
    }

    /// <summary>
    /// Factory for <see cref="IFileSystem"/>
    /// </summary>
    internal static class FileSystems
    {
        internal const string DiskProviderIdentity = "Disk";

        private static readonly IFileSystem s_systemDefault = GetFileSystem();
        private static readonly string? s_systemDefaultProviderIdentity =
            s_systemDefault.GetType().AssemblyQualifiedName;
        private static readonly string? s_cachingProviderIdentity =
            typeof(CachingFileSystemWrapper).AssemblyQualifiedName;

        public static IFileSystem Default = s_systemDefault;

        internal static string GetProviderIdentity(IFileSystem fileSystem) =>
            fileSystem is IFileSystemProviderIdentity provider
                ? NormalizeProviderIdentity(provider.ProviderIdentity)
                : NormalizeProviderIdentity(fileSystem.GetType().AssemblyQualifiedName);

        internal static string NormalizeProviderIdentity(string? provider)
        {
            return string.IsNullOrEmpty(provider) ||
                string.Equals(provider, DiskProviderIdentity, StringComparison.Ordinal) ||
                string.Equals(provider, s_systemDefaultProviderIdentity, StringComparison.Ordinal) ||
                string.Equals(provider, s_cachingProviderIdentity, StringComparison.Ordinal)
                    ? DiskProviderIdentity
                    : provider!;
        }

        internal static bool IsDiskProviderIdentity(string? provider) =>
            string.Equals(
                NormalizeProviderIdentity(provider),
                DiskProviderIdentity,
                StringComparison.Ordinal);

        private static IFileSystem GetFileSystem()
        {
#if FEATURE_WINDOWSINTEROP
            if (NativeMethods.IsWindows)
            {
                return MSBuildOnWindowsFileSystem.Singleton();
            }
            else
#endif
            {
                return ManagedFileSystem.Singleton();
            }
        }
    }
}
