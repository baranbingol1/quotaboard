// SPDX-License-Identifier: Apache-2.0
using System;
using System.Threading;
using System.Threading.Tasks;
using AiLimits.Application.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLimits.Application.Refresh;

/// <summary>What a scheduled refresh actually did, from the scheduler's point of view.</summary>
public enum RefreshOutcome
{
    Completed,
    TransientFailure,
    Skipped,
}

/// <summary>
/// Background loop that keeps usage data fresh without user clicks. The delay
/// between refreshes comes from <see cref="AdaptiveRefreshPolicy"/> (recomputed
/// every tick), a user interaction wakes the loop so stale data refreshes the
/// moment the window regains attention, and transient connectivity failures
/// retry on the escalating ladder instead of waiting a full adaptive period.
/// UI-free by design: the host supplies the refresh callback (marshalled to its
/// dispatcher) and the energy-saver probe.
/// </summary>
public sealed class RefreshScheduler : IDisposable
{
    private readonly Func<CancellationToken, Task<RefreshOutcome>> _refresh;

    private readonly Func<bool> _energySaverEnabled;

    private readonly AdaptiveRefreshPolicy _policy;

    private readonly IClock _clock;

    private readonly ILogger<RefreshScheduler> _logger;

    private readonly CancellationTokenSource _lifetime = new CancellationTokenSource();

    private readonly SemaphoreSlim _wake = new SemaphoreSlim(0, 1);

    private readonly object _gate = new object();

    private DateTimeOffset? _lastInteractionAt;

    private DateTimeOffset? _lastAttemptAt;

    private DateTimeOffset? _notBefore;

    private int _transientRetryAttempt;

    private Task? _loop;

    public RefreshScheduler(
        Func<CancellationToken, Task<RefreshOutcome>> refresh,
        Func<bool> energySaverEnabled,
        IClock clock,
        AdaptiveRefreshPolicy? policy = null,
        ILogger<RefreshScheduler>? logger = null
    )
    {
        _refresh = refresh ?? throw new ArgumentNullException(nameof(refresh));
        _energySaverEnabled = energySaverEnabled ?? throw new ArgumentNullException(nameof(energySaverEnabled));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _policy = policy ?? new AdaptiveRefreshPolicy();
        _logger = logger ?? NullLogger<RefreshScheduler>.Instance;
    }

    /// <summary>Current transient-retry attempt count (0 = healthy). Exposed for diagnostics and testing.</summary>
    internal int TransientRetryAttempt
    {
        get
        {
            lock (_gate)
            {
                return _transientRetryAttempt;
            }
        }
    }

    public void Start()
    {
        lock (_gate)
        {
            _loop ??= Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    /// <summary>
    /// Record that the user is looking at the app (e.g. the window was
    /// activated). Shortens the cadence and, if the data is already older than
    /// the new cadence, triggers a refresh right away.
    /// </summary>
    public void NoteUserInteraction()
    {
        lock (_gate)
        {
            _lastInteractionAt = _clock.UtcNow;
        }
        Wake();
    }

    /// <summary>
    /// Record a refresh the scheduler did not run itself (the startup refresh,
    /// a manual click) so the next tick is measured from it — and so a startup
    /// connectivity failure enters the retry ladder instead of idling.
    /// </summary>
    public void NoteExternalRefresh(bool hadTransientFailure)
    {
        lock (_gate)
        {
            _lastAttemptAt = _clock.UtcNow;
            _transientRetryAttempt = hadTransientFailure ? Math.Min(_transientRetryAttempt + 1, 5) : 0;
        }
        Wake();
    }

    /// <summary>
    /// Honor a provider Retry-After: no scheduled refresh runs before the given
    /// delay has elapsed. User-initiated refreshes bypass the scheduler and are
    /// unaffected. Later hints only ever push the floor further out.
    /// </summary>
    public void NoteRetryAfter(TimeSpan retryAfter)
    {
        if (retryAfter <= TimeSpan.Zero)
        {
            return;
        }
        DateTimeOffset until = _clock.UtcNow + retryAfter;
        lock (_gate)
        {
            if (!_notBefore.HasValue || until > _notBefore.Value)
            {
                _notBefore = until;
            }
        }
        // A loop already sleeping toward an earlier dueAt must recompute against
        // the new floor, otherwise it fires before the provider's Retry-After.
        Wake();
    }

    private void Wake()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    private TimeSpan CurrentDelay(DateTimeOffset now)
    {
        lock (_gate)
        {
            AdaptiveRefreshPolicy.Decision decision = _policy.NextDelay(now, _lastInteractionAt, SafeEnergySaver());
            if (_transientRetryAttempt > 0)
            {
                TimeSpan? retry = AdaptiveRefreshPolicy.TransientRetryDelay(_transientRetryAttempt);
                if (retry.HasValue && retry.Value < decision.Delay)
                {
                    return retry.Value;
                }
            }
            return decision.Delay;
        }
    }

    private bool SafeEnergySaver()
    {
        try
        {
            return _energySaverEnabled();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Energy-saver probe threw; assuming off.");
            return false;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        // A single persistent waiter: an abandoned WaitAsync would silently
        // consume the next Release, losing that wake signal.
        Task? woken = null;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                DateTimeOffset now = _clock.UtcNow;
                DateTimeOffset anchor;
                lock (_gate)
                {
                    anchor = _lastAttemptAt ?? now;
                    if (!_lastAttemptAt.HasValue)
                    {
                        _lastAttemptAt = now;
                    }
                }
                DateTimeOffset dueAt = anchor + CurrentDelay(now);
                lock (_gate)
                {
                    if (_notBefore is DateTimeOffset floor)
                    {
                        if (floor <= now)
                        {
                            _notBefore = null;
                        }
                        else if (floor > dueAt)
                        {
                            dueAt = floor;
                        }
                    }
                }
                TimeSpan wait = dueAt - now;
                if (wait > TimeSpan.Zero)
                {
                    woken ??= _wake.WaitAsync(cancellationToken);
                    await Task.WhenAny(Task.Delay(wait, cancellationToken), woken).ConfigureAwait(false);
                    if (woken.IsCompleted)
                    {
                        woken = null;
                    }
                    if (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    // Woken early (interaction or external refresh): recompute
                    // the due time against the new state instead of firing.
                    if (_clock.UtcNow < dueAt)
                    {
                        continue;
                    }
                }
                // A Retry-After floor set while this tick was already waiting
                // (or racing the delay's completion) must still be honored.
                lock (_gate)
                {
                    if (_notBefore is DateTimeOffset notBefore && notBefore > _clock.UtcNow)
                    {
                        continue;
                    }
                }
                RefreshOutcome outcome = await _refresh(cancellationToken).ConfigureAwait(false);
                lock (_gate)
                {
                    _lastAttemptAt = _clock.UtcNow;
                    _transientRetryAttempt = outcome switch
                    {
                        RefreshOutcome.Completed => 0,
                        RefreshOutcome.TransientFailure => Math.Min(_transientRetryAttempt + 1, 5),
                        _ => _transientRetryAttempt,
                    };
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failed tick must never kill the loop; the next tick will
                // use whatever cadence the state implies. Treat a throwing
                // callback as a transient failure so the retry ladder engages.
                _logger.LogWarning(ex, "Scheduled refresh tick threw an unexpected exception.");
                lock (_gate)
                {
                    _lastAttemptAt = _clock.UtcNow;
                    _transientRetryAttempt = Math.Min(_transientRetryAttempt + 1, 5);
                }
            }
        }
    }

    /// <summary>
    /// Cancel the loop and wait for any in-flight refresh callback to observe
    /// the cancellation, so the host can dispose its services afterwards
    /// without racing a live tick.
    /// </summary>
    public async Task StopAsync()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException) { }
        Task? loop;
        lock (_gate)
        {
            loop = _loop;
        }
        if (loop is not null)
        {
            try
            {
                await loop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // The loop's own failure modes are irrelevant during shutdown.
                _logger.LogWarning(ex, "Refresh loop threw during shutdown; ignoring.");
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _lifetime.Cancel();
        }
        catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        _wake.Dispose();
    }
}
