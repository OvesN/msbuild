// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Text;

namespace MSBuild.Benchmarks;

internal static class EvaluationObservationDetoursRunner
{
#if NETFRAMEWORK
    private const int BrokerTimeoutMilliseconds = 120_000;
#endif

    internal static EvaluationObservationBenchmarkResult Run(
        string executable,
        string arguments,
        string scenarioRoot)
    {
#if NETFRAMEWORK
        string resultFile = Path.GetTempFileName();
        try
        {
            File.Delete(resultFile);
            string brokerArguments = string.Join(
                " ",
                EvaluationObservationDetoursHost.HostSwitch,
                "--target-executable",
                Encode(executable),
                "--target-arguments",
                Encode(arguments),
                "--scenario-root",
                Encode(scenarioRoot),
                "--result-file",
                Encode(resultFile));

            ProcessStartInfo startInfo = new(executable, brokerArguments)
            {
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };

            using Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException($"Could not start Detours benchmark broker '{executable}'.");
            Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
            Task<string> standardError = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(BrokerTimeoutMilliseconds))
            {
                process.Kill();
                throw new TimeoutException($"Detours benchmark broker exceeded {BrokerTimeoutMilliseconds} ms.");
            }

            Task.WaitAll(standardOutput, standardError);
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Detours benchmark broker exited with code {process.ExitCode}.{Environment.NewLine}" +
                    $"{standardOutput.Result}{Environment.NewLine}{standardError.Result}");
            }

            if (!File.Exists(resultFile))
            {
                throw new InvalidOperationException(
                    $"Detours benchmark broker did not produce a result.{Environment.NewLine}" +
                    $"{standardOutput.Result}{Environment.NewLine}{standardError.Result}");
            }

            return EvaluationObservationBenchmarkResult.Parse(File.ReadAllText(resultFile));
        }
        finally
        {
            if (File.Exists(resultFile))
            {
                File.Delete(resultFile);
            }
        }
#else
        throw new PlatformNotSupportedException("The Detours observer benchmark requires .NET Framework on Windows.");
#endif
    }

    internal static string Encode(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
}
