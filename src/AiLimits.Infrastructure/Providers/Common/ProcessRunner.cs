// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Text;

namespace AiLimits.Infrastructure.Providers.Common;

public sealed class ProcessRunner : IProcessRunner
{
    /// <summary>How long a killed process is given to actually die.</summary>
    private static readonly TimeSpan KillGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Per-stream capture cap. A runaway CLI must not grow the process heap
    /// without bound; 4 MiB of UTF-16 text is far beyond any real provider
    /// response while still bounding a looping or compromised binary.
    /// </summary>
    internal const int MaxStreamChars = 4 * 1024 * 1024;

    /// <summary>
    /// Reads the stream to its end, keeping at most <see cref="MaxStreamChars"/>
    /// characters. Output past the cap is drained and discarded so the child
    /// never deadlocks on a full pipe, and the result is flagged as truncated.
    /// </summary>
    private static async Task<CappedStream> ReadCappedAsync(
        StreamReader reader,
        int maxChars,
        CancellationToken cancellationToken
    )
    {
        char[] buffer = new char[8192];
        StringBuilder builder = new StringBuilder();
        bool truncated = false;
        while (true)
        {
            int read = await reader
                .ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                .ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            int remaining = maxChars - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(remaining, read));
            }
            truncated |= read > remaining;
        }
        return new CappedStream(builder.ToString(), truncated);
    }

    private readonly record struct CappedStream(string Text, bool Truncated);

    public Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken
    ) => RunAsync(executable, arguments, timeout, MaxStreamChars, cancellationToken);

    public async Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxOutputChars, 1);
        ProcessStartInfo startInfo = new ProcessStartInfo
        {
            // Fully qualified, so the OS never gets to pick the binary for us.
            FileName = ExecutableResolver.Resolve(executable),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }
        using Process process = new Process { StartInfo = startInfo };
        long startedAt = Stopwatch.GetTimestamp();
        if (!process.Start())
        {
            throw new InvalidOperationException("The provider process could not be started.");
        }
        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutSource.CancelAfter(timeout);
        Task<CappedStream> outputTask = ReadCappedAsync(process.StandardOutput, maxOutputChars, timeoutSource.Token);
        // stderr keeps the default bound: a command may legitimately emit a
        // large payload on stdout, but a large diagnostic stream is noise.
        Task<CappedStream> errorTask = ReadCappedAsync(process.StandardError, MaxStreamChars, timeoutSource.Token);
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
            catch { }
            throw;
        }
        int exitCode = process.ExitCode;
        CappedStream output = await outputTask.ConfigureAwait(false);
        CappedStream error = await errorTask.ConfigureAwait(false);
        return new ProcessResult(
            exitCode,
            output.Text,
            error.Text,
            Stopwatch.GetElapsedTime(startedAt),
            output.Truncated,
            error.Truncated
        );
    }
}

public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken
    );

    /// <summary>
    /// Runs with an explicit per-stream capture cap, for the few commands whose
    /// legitimate output is far larger than the default bound (a full Amp
    /// conversation transcript runs to several MB). Defaults to the two-argument
    /// overload so existing implementations — including test fakes, which are
    /// never near any cap — need no change.
    /// </summary>
    Task<ProcessResult> RunAsync(
        string executable,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        int maxOutputChars,
        CancellationToken cancellationToken
    ) => RunAsync(executable, arguments, timeout, cancellationToken);
}

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool OutputTruncated = false,
    bool ErrorTruncated = false
);
