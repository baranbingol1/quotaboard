// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;

namespace AiLimits.Infrastructure.Providers.Antigravity;

/// <summary>Why no <c>agy</c> port could be reached.</summary>
internal enum AgyAcquireFailure
{
    /// <summary>A port answered; the caller has its payload.</summary>
    None,

    /// <summary>No <c>agy</c> is running and none is installed to start.</summary>
    NotInstalled,

    /// <summary><c>agy</c> was started but never answered - usually a signed-out CLI.</summary>
    NotReady,
}

internal readonly record struct AgyAcquireResult<T>(T? Payload, AgyAcquireFailure Failure)
    where T : class;

/// <summary>
/// Keeps a usable <c>agy</c> localhost server within reach.
///
/// The Antigravity CLI only serves quota while it is running, which would
/// otherwise limit QuotaBoard to the minutes a user happens to have <c>agy</c>
/// open. This session closes that gap: it reuses a running CLI when there is
/// one, starts an invisible one when there is not, and keeps that process warm
/// for a short window so a refresh every few minutes does not pay the cold-start
/// cost each time.
///
/// Two rules keep it a good citizen. Only a process this session started is ever
/// shut down, so a CLI the user is working in is never killed underneath them.
/// And readiness is judged by an endpoint actually answering rather than by a
/// port being bound, because <c>agy</c> binds its ports about a second before the
/// quota service behind them can respond.
/// </summary>
internal sealed class AgyCliSession : IDisposable
{
    private static readonly TimeSpan DefaultIdleWindow = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan DefaultReadinessTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan IdleSweepInterval = TimeSpan.FromSeconds(30);

    private readonly IAgyProcessHost _host;
    private readonly AgyCliLocator _locator;
    private readonly IClock _clock;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _idleWindow;
    private readonly TimeSpan _readinessTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Timer? _idleSweep;
    private int? _ownedProcessId;
    private DateTimeOffset _lastUsedAt;
    private bool _disposed;

    public AgyCliSession(AgyProcessDiscovery discovery)
        : this(new AgyProcessHost(discovery), new AgyCliLocator(), new SystemClock(), Task.Delay) { }

    internal AgyCliSession(
        IAgyProcessHost host,
        AgyCliLocator locator,
        IClock clock,
        Func<TimeSpan, CancellationToken, Task> delay,
        TimeSpan? idleWindow = null,
        TimeSpan? readinessTimeout = null,
        bool sweepIdleInBackground = true
    )
    {
        _host = host;
        _locator = locator;
        _clock = clock;
        _delay = delay;
        _idleWindow = idleWindow ?? DefaultIdleWindow;
        _readinessTimeout = readinessTimeout ?? DefaultReadinessTimeout;
        _lastUsedAt = clock.UtcNow;
        if (sweepIdleInBackground)
        {
            _idleSweep = new Timer(_ => StopIfIdle(), null, IdleSweepInterval, IdleSweepInterval);
        }
    }

    /// <summary>True when a CLI is running now, or one is installed to start.</summary>
    public bool IsUsable() => _host.FindListeners().Count > 0 || _locator.Resolve() is not null;

    /// <summary>
    /// Runs <paramref name="attemptAsync"/> against every reachable <c>agy</c>
    /// port, starting the CLI and waiting for it when nothing answers yet. The
    /// attempt returns null for a port that is not serving usable data, which is
    /// also how a still-initializing CLI reports itself.
    /// </summary>
    public async Task<AgyAcquireResult<T>> AcquireAsync<T>(
        Func<int, CancellationToken, Task<T?>> attemptAsync,
        CancellationToken cancellationToken
    )
        where T : class
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Touch();

            T? existing = await TryPortsAsync(attemptAsync, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                return new AgyAcquireResult<T>(existing, AgyAcquireFailure.None);
            }

            if (!EnsureStarted())
            {
                return new AgyAcquireResult<T>(null, AgyAcquireFailure.NotInstalled);
            }

            DateTimeOffset deadline = _clock.UtcNow + _readinessTimeout;
            while (_clock.UtcNow < deadline)
            {
                await _delay(PollInterval, cancellationToken).ConfigureAwait(false);

                int? owned = OwnedProcessId;
                if (owned is not null && !_host.IsAlive(owned.Value))
                {
                    // The CLI exited on its own; a stale pid must not be mistaken
                    // for a warm session on the next refresh.
                    Forget(owned.Value);
                    break;
                }

                T? payload = await TryPortsAsync(attemptAsync, cancellationToken).ConfigureAwait(false);
                if (payload is not null)
                {
                    Touch();
                    return new AgyAcquireResult<T>(payload, AgyAcquireFailure.None);
                }
            }

            // A CLI that never answered is wedged or signed out. Keeping it warm
            // would only make the next refresh wait on the same dead process.
            StopOwnedProcess();
            return new AgyAcquireResult<T>(null, AgyAcquireFailure.NotReady);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<T?> TryPortsAsync<T>(
        Func<int, CancellationToken, Task<T?>> attemptAsync,
        CancellationToken cancellationToken
    )
        where T : class
    {
        foreach (AgyListener listener in _host.FindListeners())
        {
            cancellationToken.ThrowIfCancellationRequested();
            T? payload = await attemptAsync(listener.Port, cancellationToken).ConfigureAwait(false);
            if (payload is not null)
            {
                return payload;
            }
        }
        return null;
    }

    /// <summary>Starts the CLI unless one this session owns is already running.</summary>
    private bool EnsureStarted()
    {
        int? owned = OwnedProcessId;
        if (owned is not null)
        {
            if (_host.IsAlive(owned.Value))
            {
                return true;
            }
            Forget(owned.Value);
        }

        string? binaryPath = _locator.Resolve();
        if (binaryPath is null)
        {
            return false;
        }

        int? processId = _host.Launch(binaryPath);
        if (processId is null)
        {
            return false;
        }
        lock (_sync)
        {
            _ownedProcessId = processId;
        }
        return true;
    }

    /// <summary>Shuts the warm CLI down once nothing has needed it for a while.</summary>
    internal void StopIfIdle()
    {
        lock (_sync)
        {
            if (_disposed || _ownedProcessId is null || _clock.UtcNow - _lastUsedAt < _idleWindow)
            {
                return;
            }
        }
        StopOwnedProcess();
    }

    private void StopOwnedProcess()
    {
        int? processId;
        lock (_sync)
        {
            processId = _ownedProcessId;
            _ownedProcessId = null;
        }
        if (processId is not null)
        {
            _host.Kill(processId.Value);
        }
    }

    private int? OwnedProcessId
    {
        get
        {
            lock (_sync)
            {
                return _ownedProcessId;
            }
        }
    }

    private void Forget(int processId)
    {
        lock (_sync)
        {
            if (_ownedProcessId == processId)
            {
                _ownedProcessId = null;
            }
        }
    }

    private void Touch()
    {
        lock (_sync)
        {
            _lastUsedAt = _clock.UtcNow;
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _idleSweep?.Dispose();
        StopOwnedProcess();
        (_host as IDisposable)?.Dispose();
        _gate.Dispose();
    }
}
