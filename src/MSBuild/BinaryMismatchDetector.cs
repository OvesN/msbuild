// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;

#nullable enable

namespace Microsoft.Build.CommandLine
{
    /// <summary>Name, file version and on-disk location of a single loaded assembly.</summary>
    internal readonly record struct AssemblyVersionInfo(string Name, string? Version, string? Location);

    /// <summary>
    /// Converts the "two MSBuild binaries from different builds loaded into one process" failure mode into an
    /// actionable message instead of an opaque <see cref="MissingMethodException"/>/<see cref="TypeLoadException"/>.
    /// </summary>
    /// <remarks>
    /// MSBuild keeps a frozen strong-name <c>AssemblyVersion</c> (15.1.0.0) across every release while the file
    /// version tracks the real product version, and the desktop <c>MSBuild.exe.config</c> wildcard-redirects the
    /// core assemblies to that identity (see <c>eng/Versions.props</c> and <c>src/MSBuild/app.config</c>). The CLR
    /// therefore binds an older-build <c>Microsoft.Build*.dll</c> next to a newer executable without any load
    /// error; the skew only surfaces the first time the newer code calls a member the older assembly lacks, as a
    /// <see cref="MissingMethodException"/> thrown while JIT-compiling startup.
    /// <para>
    /// This type is intentionally BCL-only and never throws. It runs solely on the failure path of
    /// <see cref="MSBuildApp.Main"/> — after such an exception has already escaped — so it must not risk a
    /// secondary fault by calling into any potentially-skewed <c>Microsoft.Build*</c> code (resource lookup,
    /// logging, ...). Because it executes only after a failure, it adds no overhead — and no reflection — to the
    /// normal startup path.
    /// </para>
    /// </remarks>
    internal static class BinaryMismatchDetector
    {
        /// <summary>Core MSBuild assemblies that must all share the running executable's file version.</summary>
        private static readonly string[] s_coreAssemblyNames =
        [
            "Microsoft.Build",
            "Microsoft.Build.Framework",
            "Microsoft.Build.Tasks.Core",
            "Microsoft.Build.Utilities.Core",
        ];

        /// <summary>
        /// Determines whether <paramref name="exception"/> is a binary-version mismatch between loaded MSBuild
        /// assemblies and, if so, produces a human-readable description.
        /// </summary>
        /// <param name="exception">The exception that escaped startup.</param>
        /// <param name="details">On success, a multi-line description naming the mismatched assemblies and paths.</param>
        /// <returns>
        /// <see langword="true"/> with a populated <paramref name="details"/> when a mismatch is detected;
        /// <see langword="false"/> for every other failure (including a genuine missing member when all MSBuild
        /// assemblies are at the same version) so the exception keeps flowing through MSBuild's normal handling.
        /// </returns>
        public static bool TryDescribeMismatch(Exception exception, [NotNullWhen(true)] out string? details)
        {
            details = null;
            try
            {
                if (!IsLikelyBinarySkewException(exception))
                {
                    return false;
                }

                // The executable that owns this code defines the expected version. Reflection is acceptable here
                // because this runs only after a startup failure, never during a normal build.
                Assembly self = typeof(BinaryMismatchDetector).Assembly;
                AssemblyVersionInfo executable = new(self.GetName().Name ?? "MSBuild", GetFileVersion(self), TryGetLocation(self));

                List<AssemblyVersionInfo> core = new();
                foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
                {
                    string? name = assembly.GetName().Name;
                    if (name is null || Array.IndexOf(s_coreAssemblyNames, name) < 0)
                    {
                        continue;
                    }

                    core.Add(new AssemblyVersionInfo(name, GetFileVersion(assembly), TryGetLocation(assembly)));
                }

                return TryBuildMismatchReport(executable, core, out details);
            }
            catch
            {
                // Diagnosis is best-effort: on any failure, fall back to MSBuild's normal exception handling.
                details = null;
                return false;
            }
        }

        /// <summary>
        /// Pure, reflection-free decision and message core, separated from <see cref="TryDescribeMismatch"/> so it
        /// can be unit tested deterministically. Reports a mismatch when the executable and at least one core
        /// assembly have different, known file versions.
        /// </summary>
        internal static bool TryBuildMismatchReport(
            AssemblyVersionInfo executable,
            IReadOnlyList<AssemblyVersionInfo> coreAssemblies,
            [NotNullWhen(true)] out string? details)
        {
            details = null;

            bool mismatch = false;
            foreach (AssemblyVersionInfo info in coreAssemblies)
            {
                if (executable.Version is not null && info.Version is not null &&
                    !string.Equals(executable.Version, info.Version, StringComparison.Ordinal))
                {
                    mismatch = true;
                    break;
                }
            }

            if (!mismatch)
            {
                return false;
            }

            details = BuildDetails(executable, coreAssemblies);
            return true;
        }

        /// <summary>
        /// Returns <see langword="true"/> when the (unwrapped) exception is the kind the runtime raises for a
        /// member/type that is absent from a loaded assembly — the signature of a binary skew.
        /// MissingMethodException and MissingFieldException both derive from MissingMemberException.
        /// </summary>
        internal static bool IsLikelyBinarySkewException(Exception exception) =>
            Unwrap(exception) is MissingMemberException or TypeLoadException;

        /// <summary>
        /// Writes the actionable mismatch message to standard error, makes a best-effort telemetry record, and
        /// returns the process exit code.
        /// </summary>
        public static int ReportAndGetExitCode(Exception exception, string details)
        {
            try
            {
                Console.Error.WriteLine(details);
                Console.Error.WriteLine();
                Console.Error.WriteLine($"Underlying error: {exception.GetType().FullName}: {exception.Message}");
            }
            catch
            {
                // The console may be unavailable (redirected or broken pipe); the exit code still signals failure.
            }

            // Best-effort telemetry. Guarded at the call site as well, because the call target lives in
            // Microsoft.Build.Framework — the very assembly that may be skewed — so even resolving it can throw.
            try
            {
                RecordTelemetry(exception);
            }
            catch
            {
                // Telemetry must never turn a clean diagnostic into a secondary crash.
            }

            return 1;
        }

        /// <summary>
        /// Records crash telemetry. Isolated and non-inlined so that, if the loaded
        /// <c>Microsoft.Build.Framework</c> is the skewed assembly and lacks this API, the resulting
        /// <see cref="MissingMethodException"/> is thrown while JIT-compiling this method and is caught by the
        /// guard in <see cref="ReportAndGetExitCode"/>, leaving the user-facing message intact.
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RecordTelemetry(Exception exception) =>
            Microsoft.Build.Framework.Telemetry.CrashTelemetryRecorder.RecordAndFlushCrashTelemetry(
                exception,
                Microsoft.Build.Framework.Telemetry.CrashExitType.UnhandledException,
                isUnhandled: false,
                isCritical: false);

        /// <summary>
        /// Unwraps wrapper exceptions (a skew inside a static constructor surfaces as
        /// <see cref="TypeInitializationException"/> wrapping the real cause) to reach the root failure.
        /// </summary>
        internal static Exception Unwrap(Exception exception)
        {
            Exception current = exception;
            for (int depth = 0;
                 depth < 8 && current.InnerException is not null && current is TypeInitializationException or TargetInvocationException;
                 depth++)
            {
                current = current.InnerException;
            }

            return current;
        }

        private static string? GetFileVersion(Assembly assembly)
        {
            try
            {
                string? location = TryGetLocation(assembly);
                if (!string.IsNullOrEmpty(location))
                {
                    return FileVersionInfo.GetVersionInfo(location!).FileVersion;
                }
            }
            catch
            {
                // Fall through to the assembly version below.
            }

            try
            {
                return assembly.GetName().Version?.ToString();
            }
            catch
            {
                return null;
            }
        }

        private static string? TryGetLocation(Assembly assembly)
        {
            try
            {
                return assembly.IsDynamic ? null : assembly.Location;
            }
            catch
            {
                return null;
            }
        }

        private static string BuildDetails(AssemblyVersionInfo executable, IReadOnlyList<AssemblyVersionInfo> coreAssemblies)
        {
            StringBuilder builder = new();
            builder.AppendLine("MSBuild could not start: mismatched MSBuild binaries were loaded into the same process.");
            builder.AppendLine("The executable and one or more Microsoft.Build* assemblies come from different builds, which leads to errors such as MissingMethodException.");
            builder.AppendLine();
            builder.AppendLine("Loaded MSBuild assemblies:");

            AppendAssemblyLine(builder, $"{executable.Name} (executable)", executable.Version, executable.Location);
            foreach (AssemblyVersionInfo info in coreAssemblies)
            {
                AppendAssemblyLine(builder, info.Name, info.Version, info.Location);
            }

            builder.AppendLine();
            builder.AppendLine("All of these must come from the same MSBuild build. A common cause is an older Microsoft.Build* assembly on the assembly load path, for example a stale copy in the Global Assembly Cache (.NET Framework), a partial or interrupted install, or a tool that supplies its own copy. Use the paths above to locate and remove the mismatched assembly.");
            return builder.ToString();
        }

        private static void AppendAssemblyLine(StringBuilder builder, string name, string? version, string? location)
        {
            builder.Append("  ").Append(name).Append(": ").Append(version ?? "<unknown>");
            if (!string.IsNullOrEmpty(location))
            {
                builder.Append("  [").Append(location).Append(']');
            }

            builder.AppendLine();
        }
    }
}
