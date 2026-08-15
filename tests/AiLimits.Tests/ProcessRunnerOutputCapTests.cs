// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

public sealed class ProcessRunnerOutputCapTests
{
    // 80 x 64 KiB = 5 MiB, comfortably past the 4 MiB per-stream cap.
    private const string FloodStdout = "$s='x'*65536; for($i=0;$i -lt 80;$i++){ [Console]::Out.Write($s) }";
    private const string FloodStderr = "$s='e'*65536; for($i=0;$i -lt 80;$i++){ [Console]::Error.Write($s) }";

    [Fact]
    public async Task Output_beyond_the_cap_is_truncated_without_deadlocking_the_child()
    {
        var runner = new ProcessRunner();

        // A reader that stopped at the cap would leave the pipe full and the
        // child blocked; the generous timeout only fails if draining broke.
        ProcessResult result = await runner.RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", FloodStdout],
            TimeSpan.FromSeconds(60),
            default
        );

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.OutputTruncated);
        Assert.False(result.ErrorTruncated);
        // The retained text never grows past the cap, so memory stays bounded.
        Assert.Equal(ProcessRunner.MaxStreamChars, result.StandardOutput.Length);
        Assert.All(result.StandardOutput, character => Assert.Equal('x', character));
    }

    [Fact]
    public async Task Error_beyond_the_cap_is_flagged_independently()
    {
        var runner = new ProcessRunner();

        ProcessResult result = await runner.RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", FloodStderr],
            TimeSpan.FromSeconds(60),
            default
        );

        Assert.True(result.ErrorTruncated);
        Assert.False(result.OutputTruncated);
        Assert.Equal(ProcessRunner.MaxStreamChars, result.StandardError.Length);
    }

    [Fact]
    public async Task Output_under_the_cap_is_captured_whole()
    {
        var runner = new ProcessRunner();

        ProcessResult result = await runner.RunAsync(
            "powershell",
            ["-NoProfile", "-NonInteractive", "-Command", "[Console]::Out.Write('hello-cap')"],
            TimeSpan.FromSeconds(30),
            default
        );

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputTruncated);
        Assert.Equal("hello-cap", result.StandardOutput);
    }
}
