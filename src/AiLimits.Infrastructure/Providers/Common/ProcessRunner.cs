// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Text;

namespace AiLimits.Infrastructure.Providers.Common;

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>How long a killed process is given to actually die.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            // Fully qualified, so the OS never gets to pick the binary for us.
            FileName = ExecutableResolver.Resolve(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = new Process
        {
            StartInfo = startInfo
        };
        long startedAt = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("The provider process could not be started.");
        }
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);
        Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
        Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                // Kill only requests termination. Wait for it to land, on a
                // token of our own so the already-cancelled one cannot make
                // this return before the process is really gone.
                using CancellationTokenSource killWait = new(KillGrace);
                try
                {
                    await process.WaitForExitAsync(killWait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // The process outlived the grace period. Nothing further
                    // to do; the handle is released when `process` disposes.
                }
            }
            // Await the pending reads (raced against a short fallback) so the
            // process is not disposed while reads are still in flight, which
            // would fault unobserved tasks. Swallow read exceptions here.
            try
            {
                await Task.WhenAny(Task.WhenAll(outputTask, errorTask), Task.Delay(KillGrace)).ConfigureAwait(false);
            }
            catch
            {
            }
            throw;
        }
        int exitCode = process.ExitCode;
        return new ProcessResult(exitCode, await outputTask.ConfigureAwait(false), await errorTask.ConfigureAwait(false), Stopwatch.GetElapsedTime(startedAt));
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, TimeSpan Duration);
