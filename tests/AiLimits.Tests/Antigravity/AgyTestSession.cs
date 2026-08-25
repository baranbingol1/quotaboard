// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Infrastructure.Providers.Antigravity;

namespace AiLimits.Tests;

/// <summary>
/// CLI sessions for strategy tests, wired so nothing is ever launched: either a
/// port is already listening or there is no CLI to start.
/// </summary>
internal static class AgyTestSession
{
    /// <summary>No CLI running and none installed - for parser-only tests.</summary>
    public static AgyCliSession Idle() => Build(listeningPort: null);

    /// <summary>A CLI already serving on <paramref name="port"/>.</summary>
    public static AgyCliSession Listening(int port) => Build(port);

    private static AgyCliSession Build(int? listeningPort)
    {
        StubHost host = new(listeningPort);
        return new AgyCliSession(
            host,
            new AgyCliLocator(_ => null, _ => false, _ => null),
            new StubClock(),
            (_, _) => Task.CompletedTask,
            sweepIdleInBackground: false
        );
    }

    private sealed class StubHost(int? listeningPort) : IAgyProcessHost
    {
        public IReadOnlyList<AgyListener> FindListeners() =>
            listeningPort is null ? Array.Empty<AgyListener>() : [new AgyListener(1, listeningPort.Value)];

        public int? Launch(string binaryPath) => null;

        public bool IsAlive(int processId) => false;

        public void Kill(int processId) { }
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    }
}
