// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Build.CommandLine;
using Shouldly;
using Xunit;

#nullable enable

namespace Microsoft.Build.UnitTests
{
    /// <summary>
    /// Tests for <see cref="BinaryMismatchDetector"/>, which turns the "two MSBuild binaries from different
    /// builds in one process" failure (DevDiv#2982222) into an actionable message instead of a raw
    /// MissingMethodException. Assertions target the data fed into the detector, not localized message text.
    /// </summary>
    public class BinaryMismatchDetector_Tests
    {
        [Fact]
        public void NonSkewException_IsNotDetected()
        {
            BinaryMismatchDetector.IsLikelyBinarySkewException(new InvalidOperationException()).ShouldBeFalse();
        }

        [Theory]
        [InlineData(typeof(MissingMethodException))]
        [InlineData(typeof(MissingFieldException))]
        [InlineData(typeof(MissingMemberException))]
        [InlineData(typeof(TypeLoadException))]
        public void MissingMemberAndTypeLoadExceptions_AreDetected(Type exceptionType)
        {
            Exception exception = (Exception)Activator.CreateInstance(exceptionType)!;
            BinaryMismatchDetector.IsLikelyBinarySkewException(exception).ShouldBeTrue();
        }

        [Fact]
        public void SkewWrappedInTypeInitializationException_IsDetected()
        {
            // A skew triggered from a static constructor surfaces as TypeInitializationException(MissingMethodException).
            Exception wrapped = new TypeInitializationException("SomeType", new MissingMethodException("Missing.Method"));
            BinaryMismatchDetector.IsLikelyBinarySkewException(wrapped).ShouldBeTrue();
        }

        [Fact]
        public void Unwrap_ReturnsRootCause()
        {
            MissingMethodException root = new("Missing.Method");
            Exception wrapped = new TargetInvocationException(new TypeInitializationException("T", root));
            BinaryMismatchDetector.Unwrap(wrapped).ShouldBeSameAs(root);
        }

        [Fact]
        public void MatchingVersions_AreNotReportedAsMismatch()
        {
            AssemblyVersionInfo executable = new("MSBuild", "18.5.4.18101", @"C:\VS\MSBuild\Current\Bin\MSBuild.exe");
            IReadOnlyList<AssemblyVersionInfo> core =
            [
                new("Microsoft.Build", "18.5.4.18101", @"C:\VS\MSBuild\Current\Bin\Microsoft.Build.dll"),
                new("Microsoft.Build.Framework", "18.5.4.18101", @"C:\VS\MSBuild\Current\Bin\Microsoft.Build.Framework.dll"),
            ];

            BinaryMismatchDetector.TryBuildMismatchReport(executable, core, out string? details).ShouldBeFalse();
            details.ShouldBeNull();
        }

        [Fact]
        public void DifferentVersions_AreReportedWithActionableDetails()
        {
            // Mirrors DevDiv#2982222: a new MSBuild.exe (18.5.4) loaded an old Microsoft.Build.Framework.dll (18.0.2).
            AssemblyVersionInfo executable = new("MSBuild", "18.5.4.18101", @"C:\VS\MSBuild\Current\Bin\MSBuild.exe");
            IReadOnlyList<AssemblyVersionInfo> core =
            [
                new("Microsoft.Build", "18.5.4.18101", @"C:\VS\MSBuild\Current\Bin\Microsoft.Build.dll"),
                new("Microsoft.Build.Framework", "18.0.2.52102", @"C:\Windows\Microsoft.NET\assembly\GAC_MSIL\Microsoft.Build.Framework\v4.0_15.1.0.0__b03f5f7f11d50a3a\Microsoft.Build.Framework.dll"),
            ];

            BinaryMismatchDetector.TryBuildMismatchReport(executable, core, out string? details).ShouldBeTrue();

            details.ShouldNotBeNull();
            details!.ShouldContain("18.5.4.18101");
            details.ShouldContain("18.0.2.52102");
            details.ShouldContain("Microsoft.Build.Framework");

            // The mismatched assembly's load path is surfaced so the user can locate and remove the stale copy.
            details.ShouldContain("GAC_MSIL");
        }

        [Fact]
        public void UnknownVersions_AreNotReportedAsMismatch()
        {
            // When file versions cannot be read, do not guess a mismatch.
            AssemblyVersionInfo executable = new("MSBuild", null, null);
            IReadOnlyList<AssemblyVersionInfo> core =
            [
                new("Microsoft.Build.Framework", null, null),
            ];

            BinaryMismatchDetector.TryBuildMismatchReport(executable, core, out string? details).ShouldBeFalse();
            details.ShouldBeNull();
        }

        [Fact]
        public void RealProcess_WithGenuineMissingMethod_DoesNotFalsePositive()
        {
            // In a correctly-installed process every Microsoft.Build* assembly shares one version, so a
            // MissingMethodException from an unrelated cause must NOT be misreported as a binary mismatch — it
            // should keep flowing through MSBuild's normal exception handling.
            BinaryMismatchDetector.TryDescribeMismatch(new MissingMethodException("Some.Unrelated.Method"), out string? details).ShouldBeFalse();
            details.ShouldBeNull();
        }
    }
}
