// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Infrastructure.Providers.Antigravity;

namespace AiLimits.Tests;

public sealed class AgyCliSessionTests
{
    [Fact]
    public async Task RunningCliIsReusedWithoutStartingAnother()
    {
        FakeAgyProcessHost host = new();
        host.AddListener(processId: 7, port: 4100);
        using AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe");

        AgyAcquireResult<string> result = await session.AcquireAsync(
            (port, _) => Task.FromResult<string?>($"port {port}"),
            default
        );

        Assert.Equal("port 4100", result.Payload);
        Assert.Equal(AgyAcquireFailure.None, result.Failure);
        Assert.Equal(0, host.LaunchCount);
    }

    [Fact]
    public async Task CliIsStartedAndPolledUntilItAnswers()
    {
        FakeAgyProcessHost host = new();
        // The real CLI binds its ports about a second before the quota service
        // behind them can answer, so a bound port alone must not end the wait.
        host.OnLaunch = launchedPid => host.AddListener(launchedPid, port: 4200);
        using AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe");
        int attempts = 0;

        AgyAcquireResult<string> result = await session.AcquireAsync(
            (port, _) =>
            {
                attempts++;
                return Task.FromResult(attempts < 3 ? null : $"port {port}");
            },
            default
        );

        Assert.Equal("port 4200", result.Payload);
        Assert.Equal(1, host.LaunchCount);
        Assert.Empty(host.KilledProcessIds);
    }

    [Fact]
    public async Task MissingCliIsReportedWithoutLaunchAttempt()
    {
        FakeAgyProcessHost host = new();
        using AgyCliSession session = Session(host, binaryPath: null);

        AgyAcquireResult<string> result = await session.AcquireAsync((_, _) => Task.FromResult<string?>(null), default);

        Assert.Null(result.Payload);
        Assert.Equal(AgyAcquireFailure.NotInstalled, result.Failure);
        Assert.Equal(0, host.LaunchCount);
    }

    [Fact]
    public async Task CliThatNeverAnswersIsShutDownInsteadOfKeptWarm()
    {
        FakeAgyProcessHost host = new();
        host.OnLaunch = launchedPid => host.AddListener(launchedPid, port: 4300);
        using AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe");

        AgyAcquireResult<string> result = await session.AcquireAsync((_, _) => Task.FromResult<string?>(null), default);

        Assert.Null(result.Payload);
        Assert.Equal(AgyAcquireFailure.NotReady, result.Failure);
        Assert.Equal(host.LaunchedProcessIds, host.KilledProcessIds);
    }

    [Fact]
    public async Task IdleShutdownNeverTouchesACliTheUserStarted()
    {
        FakeAgyProcessHost host = new();
        host.AddListener(processId: 11, port: 4400);
        MutableClock clock = new();
        using AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe", clock: clock);
        await session.AcquireAsync((port, _) => Task.FromResult<string?>($"port {port}"), default);

        clock.UtcNow += TimeSpan.FromHours(1);
        session.StopIfIdle();

        Assert.Empty(host.KilledProcessIds);
    }

    [Fact]
    public async Task IdleShutdownStopsTheCliTheSessionStarted()
    {
        FakeAgyProcessHost host = new();
        host.OnLaunch = launchedPid => host.AddListener(launchedPid, port: 4500);
        MutableClock clock = new();
        using AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe", clock: clock);
        await session.AcquireAsync((port, _) => Task.FromResult<string?>($"port {port}"), default);
        Assert.Empty(host.KilledProcessIds);

        clock.UtcNow += TimeSpan.FromHours(1);
        session.StopIfIdle();

        Assert.Equal(host.LaunchedProcessIds, host.KilledProcessIds);
    }

    [Fact]
    public async Task WarmCliIsReusedByTheNextRefresh()
    {
        FakeAgyProcessHost host = new();
        host.OnLaunch = launchedPid => host.AddListener(launchedPid, port: 4600);
        using AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe");

        await session.AcquireAsync((port, _) => Task.FromResult<string?>($"port {port}"), default);
        await session.AcquireAsync((port, _) => Task.FromResult<string?>($"port {port}"), default);

        Assert.Equal(1, host.LaunchCount);
    }

    [Fact]
    public async Task DisposeStopsTheCliTheSessionStarted()
    {
        FakeAgyProcessHost host = new();
        host.OnLaunch = launchedPid => host.AddListener(launchedPid, port: 4700);
        AgyCliSession session = Session(host, binaryPath: @"C:\agy\agy.exe");
        await session.AcquireAsync((port, _) => Task.FromResult<string?>($"port {port}"), default);

        session.Dispose();

        Assert.Equal(host.LaunchedProcessIds, host.KilledProcessIds);
    }

    [Fact]
    public void InstalledCliCountsAsUsableBeforeAnythingIsRunning()
    {
        FakeAgyProcessHost host = new();

        Assert.True(Session(host, binaryPath: @"C:\agy\agy.exe").IsUsable());
        Assert.False(Session(host, binaryPath: null).IsUsable());
    }

    private static AgyCliSession Session(FakeAgyProcessHost host, string? binaryPath, MutableClock? clock = null)
    {
        MutableClock sessionClock = clock ?? new MutableClock();
        return new AgyCliSession(
            host,
            new AgyCliLocator(_ => null, path => path == binaryPath, _ => binaryPath),
            sessionClock,
            (delay, _) =>
            {
                // Advancing the clock in step with the awaited delay lets the
                // readiness deadline expire without any real waiting.
                sessionClock.UtcNow += delay;
                return Task.CompletedTask;
            },
            idleWindow: TimeSpan.FromMinutes(3),
            readinessTimeout: TimeSpan.FromSeconds(20),
            sweepIdleInBackground: false
        );
    }

    private sealed class FakeAgyProcessHost : IAgyProcessHost
    {
        private readonly List<AgyListener> _listeners = new();
        private readonly HashSet<int> _exited = new();
        private int _nextProcessId = 100;

        public int LaunchCount { get; private set; }

        public List<int> LaunchedProcessIds { get; } = new();

        public List<int> KilledProcessIds { get; } = new();

        public Action<int>? OnLaunch { get; set; }

        public void AddListener(int processId, int port) => _listeners.Add(new AgyListener(processId, port));

        public IReadOnlyList<AgyListener> FindListeners() =>
            _listeners.Where(listener => !_exited.Contains(listener.ProcessId)).ToArray();

        public int? Launch(string binaryPath)
        {
            LaunchCount++;
            int processId = _nextProcessId++;
            LaunchedProcessIds.Add(processId);
            OnLaunch?.Invoke(processId);
            return processId;
        }

        public bool IsAlive(int processId) => !_exited.Contains(processId);

        public void Kill(int processId)
        {
            KilledProcessIds.Add(processId);
            _exited.Add(processId);
        }
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    }
}
