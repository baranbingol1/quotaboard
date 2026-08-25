// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using AiLimits.Infrastructure.Providers.Antigravity;

namespace AiLimits.Tests;

public sealed class AgyProcessHostTests
{
    private const string BinaryPath = @"C:\agy\bin\agy.exe";

    [Fact]
    public void LaunchKeepsTheProcessOffScreenWithoutRedirectingItsStreams()
    {
        ProcessStartInfo startInfo = AgyProcessHost.CreateStartInfo(BinaryPath);

        Assert.False(startInfo.UseShellExecute);
        Assert.True(startInfo.CreateNoWindow);
        // agy is an interactive TUI: a redirected stdin leaves it waiting for
        // piped input forever, alive but never serving quota.
        Assert.False(startInfo.RedirectStandardInput);
        Assert.False(startInfo.RedirectStandardOutput);
        Assert.False(startInfo.RedirectStandardError);
        Assert.Equal(@"C:\agy\bin", startInfo.WorkingDirectory);
    }

    [Fact]
    public void LaunchDisablesTheAutoUpdaterThatWouldFlashATerminalWindow()
    {
        // CreateNoWindow covers agy alone. Left enabled, its updater spawns
        // `agy --bg-updater` -> `agy --version`, which takes a console of its own
        // and paints a real terminal window every time the CLI's fifteen-minute
        // update throttle expires.
        ProcessStartInfo startInfo = AgyProcessHost.CreateStartInfo(BinaryPath);

        Assert.Equal("true", startInfo.Environment[AgyProcessHost.DisableAutoUpdateVariable]);
    }

    [Fact]
    public void DisablingTheAutoUpdaterDoesNotLeakIntoTheUsersOwnEnvironment()
    {
        // The variable belongs to the child's environment block only, so an agy
        // the user starts themselves still updates itself normally.
        ProcessStartInfo startInfo = AgyProcessHost.CreateStartInfo(BinaryPath);

        Assert.Equal("true", startInfo.Environment[AgyProcessHost.DisableAutoUpdateVariable]);
        Assert.Null(Environment.GetEnvironmentVariable(AgyProcessHost.DisableAutoUpdateVariable));
    }
}
