// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationNativeBridge
{
    internal static IDisposable Enable(
        bool enabled,
        EvaluationObservationNativeMetrics? metrics,
        bool collectPaths)
    {
        if (enabled)
        {
            throw new NotSupportedException("The evaluator-native observer is not available in this benchmark variant.");
        }

        return NoopDisposable.Instance;
    }

    private sealed class NoopDisposable : IDisposable
    {
        internal static NoopDisposable Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}
