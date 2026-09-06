// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using Microsoft.Build.Framework;
using SdkResult = Microsoft.Build.BackEnd.SdkResolution.SdkResult;

namespace Microsoft.Build.Evaluation.Context;

/// <summary>
/// Decides whether an evaluation result can still be reused by re-checking the inputs it recorded.
/// </summary>
internal static class EvaluationInputValidator
{
    /// <summary>
    /// Returns true when every recorded input still has the value evaluation consumed.
    /// </summary>
    /// <param name="inputs">The recorded inputs.</param>
    /// <param name="resolveSdk">Resolves an SDK reference again; null when the caller cannot, which rejects any SDK-bearing evaluation.</param>
    /// <param name="reason">The first input that differs, or the non-cacheable reason.</param>
    internal static bool IsCurrent(EvaluationInputs inputs, Func<SdkReference, SdkResult?>? resolveSdk, out string? reason)
    {
        if (!inputs.IsCacheable)
        {
            reason = $"{inputs.NonCacheable}: {inputs.NonCacheableDetail}";
            return false;
        }

        try
        {
            foreach (KeyValuePair<string, FileDependency> file in inputs.Files)
            {
                if (!EvaluationInputRecorder.TryStat(file.Key, out FileDependency current) || current != file.Value)
                {
                    reason = file.Key;
                    return false;
                }
            }

            foreach (KeyValuePair<string, string?> read in inputs.EnvironmentReads)
            {
                if (!string.Equals(Environment.GetEnvironmentVariable(read.Key), read.Value, StringComparison.Ordinal))
                {
                    reason = $"environment variable {read.Key}";
                    return false;
                }
            }

            foreach (SdkDependency sdk in inputs.SdkResolutions)
            {
                if (resolveSdk is null || !sdk.Result.Equals(resolveSdk(sdk.Reference)))
                {
                    reason = $"SDK {sdk.Reference}";
                    return false;
                }
            }
        }
        catch (Exception ex) when (!ExceptionHandling.IsCriticalException(ex))
        {
            // A failed check is a miss, never a failed build.
            reason = ex.Message;
            return false;
        }

        reason = null;
        return true;
    }
}
