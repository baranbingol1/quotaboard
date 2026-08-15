// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Diagnostics;
using AiLimits.Application.Snapshots;
using AiLimits.Domain;
using Microsoft.Extensions.Logging;

namespace AiLimits.Application.Refresh;

public sealed class RefreshCoordinator : IDisposable
{
    private readonly record struct RefreshKey(AccountKey Account, long Revision, bool Force);

    private sealed class AccountRefreshState
    {
        private readonly object _gate = new object();

        private long _generation;

        private long _configurationRevision;

        public long Begin(long configurationRevision)
        {
            lock (_gate)
            {
                _configurationRevision = configurationRevision;
                return ++_generation;
            }
        }

        public bool IsCurrent(long generation, long configurationRevision)
        {
            lock (_gate)
            {
                return _generation == generation && _configurationRevision == configurationRevision;
            }
        }
    }

    private readonly IReadOnlyDictionary<ProviderId, IProviderAdapter> _adapters;

    private readonly IAccountRepository _accounts;

    private readonly ISnapshotRepository _snapshots;

    private readonly SnapshotMerger _snapshotMerger;

    private readonly IClock _clock;

    private readonly ILogger<RefreshCoordinator> _logger;

    private readonly SemaphoreSlim _providerConcurrency;

    // One coalesced operation per key. The operation owns its own cancellation
    // source; each caller only stops waiting when its token fires, and the shared
    // work is cancelled only once the last interested caller has walked away.
    private sealed class InflightOperation
    {
        private readonly RefreshCoordinator _owner;

        private readonly RefreshKey _key;

        private readonly CancellationTokenSource _cancellation = new CancellationTokenSource();

        private readonly Lazy<Task<RefreshPublication>> _task;

        private int _waiters;

        // The token source outlives neither of its two users, but which of
        // them finishes last is a race: a caller can walk away while the work
        // is still running, and the work can finish while callers are still
        // attached. Disposing on the second retirement covers both orders
        // without ever cancelling a source that is already disposed.
        private int _retirements;

        public InflightOperation(RefreshCoordinator owner, RefreshKey key, RefreshRequest request)
        {
            _owner = owner;
            _key = key;
            _task = new Lazy<Task<RefreshPublication>>(
                () => RunAsync(request),
                LazyThreadSafetyMode.ExecutionAndPublication
            );
        }

        public bool TryAddWaiter()
        {
            int current;
            do
            {
                current = Volatile.Read(ref _waiters);
                if (current < 0)
                {
                    return false;
                }
            } while (Interlocked.CompareExchange(ref _waiters, current + 1, current) != current);
            return true;
        }

        public async Task<RefreshPublication> WaitAsync(CancellationToken callerToken)
        {
            try
            {
                return await _task.Value.WaitAsync(callerToken).ConfigureAwait(false);
            }
            finally
            {
                RemoveWaiter();
            }
        }

        private void RemoveWaiter()
        {
            // Close only if no other waiter slipped in between the decrement and
            // the exchange; a successful close retires the operation for good.
            if (Interlocked.Decrement(ref _waiters) == 0 && Interlocked.CompareExchange(ref _waiters, -1, 0) == 0)
            {
                _cancellation.Cancel();
                _owner._inflight.TryRemove(new KeyValuePair<RefreshKey, InflightOperation>(_key, this));
                Retire();
            }
        }

        private async Task<RefreshPublication> RunAsync(RefreshRequest request)
        {
            try
            {
                return await _owner.ExecuteAsync(request, _cancellation.Token).ConfigureAwait(false);
            }
            finally
            {
                _owner._inflight.TryRemove(new KeyValuePair<RefreshKey, InflightOperation>(_key, this));
                Retire();
            }
        }

        private void Retire()
        {
            if (Interlocked.Increment(ref _retirements) == 2)
            {
                _cancellation.Dispose();
            }
        }
    }

    private readonly ConcurrentDictionary<RefreshKey, InflightOperation> _inflight =
        new ConcurrentDictionary<RefreshKey, InflightOperation>();

    private readonly ConcurrentDictionary<AccountKey, AccountRefreshState> _state =
        new ConcurrentDictionary<AccountKey, AccountRefreshState>();

    public RefreshCoordinator(
        IEnumerable<IProviderAdapter> adapters,
        IAccountRepository accounts,
        ISnapshotRepository snapshots,
        SnapshotMerger snapshotMerger,
        IClock clock,
        ILogger<RefreshCoordinator> logger,
        int maxProviderConcurrency = 4
    )
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxProviderConcurrency, 1, "maxProviderConcurrency");
        _adapters = adapters.ToDictionary((IProviderAdapter adapter) => adapter.Descriptor.Id);
        _accounts = accounts;
        _snapshots = snapshots;
        _snapshotMerger = snapshotMerger;
        _clock = clock;
        _logger = logger;
        _providerConcurrency = new SemaphoreSlim(maxProviderConcurrency, maxProviderConcurrency);
    }

    /// <summary>
    /// Releases the concurrency gate. In-flight operations own their own
    /// cancellation sources and dispose them as they retire, so shutdown only
    /// has to wait for them — see <c>AppBootstrap.ShutdownAsync</c>.
    /// </summary>
    public void Dispose() => _providerConcurrency.Dispose();

    public Task<RefreshPublication> RefreshAsync(
        RefreshRequest request,
        CancellationToken cancellationToken = default(CancellationToken)
    )
    {
        RefreshKey key = new RefreshKey(request.Account, request.ConfigurationRevision, request.Force);
        while (true)
        {
            InflightOperation operation = _inflight.GetOrAdd(
                key,
                (RefreshKey _) => new InflightOperation(this, key, request)
            );
            if (operation.TryAddWaiter())
            {
                return operation.WaitAsync(cancellationToken);
            }
            // The operation retired between lookup and join; clear it and retry.
            _inflight.TryRemove(new KeyValuePair<RefreshKey, InflightOperation>(key, operation));
        }
    }

    private async Task<RefreshPublication> ExecuteAsync(RefreshRequest request, CancellationToken cancellationToken)
    {
        AccountRefreshState state = _state.GetOrAdd(request.Account, (AccountKey _) => new AccountRefreshState());
        long generation = state.Begin(request.ConfigurationRevision);
        ProviderAccount account = await _accounts.GetAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (account is null || account.ConfigurationRevision != request.ConfigurationRevision)
        {
            return Rejected(generation, "Account configuration changed before refresh started.");
        }
        if (!_adapters.TryGetValue(request.Account.Provider, out IProviderAdapter adapter))
        {
            return new RefreshPublication(
                RefreshPublicationStatus.NoStrategyAvailable,
                await _snapshots.GetLatestAsync(request.Account, cancellationToken).ConfigureAwait(false),
                Array.Empty<FetchAttempt>(),
                generation,
                "No provider adapter is registered."
            );
        }
        await _providerConcurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            List<FetchAttempt> attempts = new List<FetchAttempt>();
            TimeSpan? retryAfterHint = null;
            string? unavailableStrategyId = null;
            string? unavailableReason = null;
            string? temporarilyUnavailableStrategyId = null;
            string? temporarilyUnavailableReason = null;
            IReadOnlyList<ILimitFetchStrategy> strategies;
            try
            {
                strategies = adapter.CreateLimitStrategies(account);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Creating limit strategies failed for {Account}", account.Key);
                strategies = Array.Empty<ILimitFetchStrategy>();
            }
            foreach (ILimitFetchStrategy strategy in from item in strategies orderby item.Order select item)
            {
                cancellationToken.ThrowIfCancellationRequested();
                StrategyAvailabilityResult availability;
                try
                {
                    availability = await strategy
                        .CheckAvailabilityAsync(account, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Availability check for strategy {StrategyId} failed for {Account}",
                        strategy.Id,
                        account.Key
                    );
                    attempts.Add(
                        await RecordAttemptAsync(
                                account.Key,
                                strategy.Id,
                                TimeSpan.Zero,
                                FetchFailureKind.Unknown,
                                "The provider availability check failed unexpectedly.",
                                cancellationToken
                            )
                            .ConfigureAwait(false)
                    );
                    continue;
                }
                if (availability.Availability != StrategyAvailability.Available)
                {
                    if (availability.Availability == StrategyAvailability.TemporarilyUnavailable)
                    {
                        // Availability-only failures are persisted only when no
                        // strategy produced a real attempt. Otherwise their
                        // later timestamp would hide the actionable failure.
                        temporarilyUnavailableStrategyId ??= strategy.Id;
                        temporarilyUnavailableReason ??= availability.SafeReason;
                    }
                    else
                    {
                        // Keep one representative skip. It is persisted only
                        // if every strategy is unavailable, so a later skip can
                        // never mask an earlier real attempt.
                        unavailableStrategyId ??= strategy.Id;
                        unavailableReason ??= availability.SafeReason;
                    }
                    continue;
                }
                DateTimeOffset startedAt = _clock.UtcNow;
                Stopwatch stopwatch = Stopwatch.StartNew();
                FetchResult result;
                try
                {
                    result = await strategy.FetchAsync(account, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Provider strategy {StrategyId} failed for {Account}",
                        strategy.Id,
                        account.Key
                    );
                    result = FetchResult.Failure(
                        FetchFailureKind.Unknown,
                        "The provider strategy failed unexpectedly.",
                        FallbackPolicy.TryNextStrategy,
                        strategy.Id,
                        stopwatch.Elapsed
                    );
                }
                stopwatch.Stop();
                FetchAttempt attempt = new FetchAttempt(
                    Guid.NewGuid().ToString("N"),
                    account.Key,
                    strategy.Id,
                    startedAt,
                    (result.Duration == TimeSpan.Zero) ? stopwatch.Elapsed : result.Duration,
                    result.FailureKind,
                    SanitizeDiagnostic(result.SafeMessage)
                );
                await _snapshots.RecordAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
                attempts.Add(attempt);
                if (
                    result.RetryAfter is TimeSpan retryAfter
                    && (!retryAfterHint.HasValue || retryAfter > retryAfterHint.Value)
                )
                {
                    retryAfterHint = retryAfter;
                }
                if (result.IsSuccess)
                {
                    return await PublishAsync(request, result.Snapshot, attempts, generation, state, cancellationToken)
                        .ConfigureAwait(false);
                }
                if (result.FallbackPolicy != FallbackPolicy.Stop)
                {
                    continue;
                }
                break;
            }
            if (attempts.Count == 0 && temporarilyUnavailableStrategyId is not null)
            {
                attempts.Add(
                    await RecordAttemptAsync(
                            account.Key,
                            temporarilyUnavailableStrategyId,
                            TimeSpan.Zero,
                            FetchFailureKind.TemporarilyUnavailable,
                            temporarilyUnavailableReason ?? "The provider is temporarily unavailable.",
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            }
            if (attempts.Count == 0 && unavailableStrategyId is not null)
            {
                attempts.Add(
                    await RecordAttemptAsync(
                            account.Key,
                            unavailableStrategyId,
                            TimeSpan.Zero,
                            FetchFailureKind.Unsupported,
                            unavailableReason ?? "No provider strategy is configured for this account.",
                            cancellationToken
                        )
                        .ConfigureAwait(false)
                );
            }
            ProviderSnapshot cached = await _snapshots
                .GetLatestAsync(request.Account, cancellationToken)
                .ConfigureAwait(false);
            return new RefreshPublication(
                (cached is null)
                    ? RefreshPublicationStatus.FailedWithoutData
                    : RefreshPublicationStatus.FailedWithCachedData,
                cached,
                attempts,
                generation,
                (cached is null) ? "All provider strategies failed." : "Showing last known data; refresh failed.",
                retryAfterHint
            );
        }
        finally
        {
            _providerConcurrency.Release();
        }
    }

    private async Task<RefreshPublication> PublishAsync(
        RefreshRequest request,
        ProviderSnapshot incoming,
        IReadOnlyList<FetchAttempt> attempts,
        long generation,
        AccountRefreshState state,
        CancellationToken cancellationToken
    )
    {
        if (incoming.Account != request.Account)
        {
            return Rejected(generation, "Provider returned data for a different account.", attempts);
        }
        ProviderAccount current = await _accounts.GetAsync(request.Account, cancellationToken).ConfigureAwait(false);
        if (
            current is null
            || current.ConfigurationRevision != request.ConfigurationRevision
            || !state.IsCurrent(generation, request.ConfigurationRevision)
        )
        {
            return Rejected(generation, "A newer account configuration or refresh superseded this result.", attempts);
        }
        ProviderSnapshot previous = await _snapshots
            .GetLatestAsync(request.Account, cancellationToken)
            .ConfigureAwait(false);
        ProviderSnapshot merged = _snapshotMerger.Merge(previous, incoming);
        await _snapshots.SaveAsync(merged, generation, cancellationToken).ConfigureAwait(false);
        return new RefreshPublication(
            RefreshPublicationStatus.Published,
            merged,
            attempts,
            generation,
            "Usage refreshed."
        );
    }

    private async Task<FetchAttempt> RecordAttemptAsync(
        AccountKey account,
        string strategyId,
        TimeSpan duration,
        FetchFailureKind failure,
        string message,
        CancellationToken cancellationToken
    )
    {
        FetchAttempt attempt = new FetchAttempt(
            Guid.NewGuid().ToString("N"),
            account,
            strategyId,
            _clock.UtcNow,
            duration,
            failure,
            SanitizeDiagnostic(message)
        );
        await _snapshots.RecordAttemptAsync(attempt, cancellationToken).ConfigureAwait(false);
        return attempt;
    }

    private static RefreshPublication Rejected(
        long generation,
        string message,
        IReadOnlyList<FetchAttempt>? attempts = null
    )
    {
        return new RefreshPublication(
            RefreshPublicationStatus.StaleResultRejected,
            null,
            attempts ?? Array.Empty<FetchAttempt>(),
            generation,
            message
        );
    }

    internal static string SanitizeDiagnostic(string message) => DiagnosticRedactor.Redact(message);
}
