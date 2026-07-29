// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Presentation;
using AiLimits.Application.Refresh;
using AiLimits.Application.Snapshots;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using AiLimits.Infrastructure.Providers.Fixtures;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLimits.Tests;

public sealed class RefreshCoordinatorTests
{
    // Deadline for awaiting controlled continuations. The pass path completes
    // in milliseconds; this bound only decides how long a genuine failure
    // takes to report. 2s proved flaky on cold CI runners (timed out once at
    // CancelingOneCoalescedCallerDoesNotCancelTheSharedOperation).
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(30);


    [Fact]
    public async Task EquivalentConcurrentRefreshesCoalesce()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var snapshots = new MemorySnapshots();
        var strategy = new ControlledStrategy(account.Key);
        var adapter = new FakeAdapter(strategy);
        var coordinator = new RefreshCoordinator([adapter], accounts, snapshots, new SnapshotMerger(),
            new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        var first = coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));
        var second = coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));
        await strategy.Started.Task.WaitAsync(TestTimeout);
        Assert.Equal(1, strategy.FetchCount);
        strategy.Release.SetResult();
        var results = await Task.WhenAll(first, second);
        Assert.All(results, item => Assert.Equal(RefreshPublicationStatus.Published, item.Status));
        Assert.Equal(results[0].Generation, results[1].Generation);
    }

    [Fact]
    public async Task ConfigurationChangeRejectsOldResult()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 1, true);
        var accounts = new MemoryAccounts(account);
        var strategy = new ControlledStrategy(account.Key);
        var coordinator = new RefreshCoordinator([new FakeAdapter(strategy)], accounts, new MemorySnapshots(),
            new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);
        var refresh = coordinator.RefreshAsync(new RefreshRequest(account.Key, 1));
        await strategy.Started.Task.WaitAsync(TestTimeout);
        accounts.Current = account with { ConfigurationRevision = 2 };
        strategy.Release.SetResult();
        var result = await refresh;
        Assert.Equal(RefreshPublicationStatus.StaleResultRejected, result.Status);
    }

    [Fact]
    public async Task CancelingOneCoalescedCallerDoesNotCancelTheSharedOperation()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var strategy = new ControlledStrategy(account.Key);
        var coordinator = new RefreshCoordinator([new FakeAdapter(strategy)], accounts, new MemorySnapshots(),
            new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        using var impatient = new CancellationTokenSource();
        var first = coordinator.RefreshAsync(new RefreshRequest(account.Key, 4), impatient.Token);
        var second = coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));
        await strategy.Started.Task.WaitAsync(TestTimeout);

        impatient.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        Assert.False(second.IsCompleted);

        strategy.Release.SetResult();
        var survivor = await second.WaitAsync(TestTimeout);
        Assert.Equal(RefreshPublicationStatus.Published, survivor.Status);
        Assert.Equal(1, strategy.FetchCount);
    }

    [Fact]
    public async Task CancelingTheLastCallerCancelsTheSharedOperation()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var strategy = new ControlledStrategy(account.Key);
        var coordinator = new RefreshCoordinator([new FakeAdapter(strategy)], accounts, new MemorySnapshots(),
            new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        using var only = new CancellationTokenSource();
        var refresh = coordinator.RefreshAsync(new RefreshRequest(account.Key, 4), only.Token);
        await strategy.Started.Task.WaitAsync(TestTimeout);

        only.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => refresh);
        await strategy.Cancelled.Task.WaitAsync(TestTimeout);

        // A later request for the same key starts a fresh operation.
        strategy.Release.SetResult();
        var again = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 4)).WaitAsync(TestTimeout);
        Assert.Equal(RefreshPublicationStatus.Published, again.Status);
        Assert.Equal(2, strategy.FetchCount);
    }

    [Fact]
    public async Task AvailabilityCheckExceptionFallsThroughToTheNextStrategy()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var healthy = new ControlledStrategy(account.Key) { Order = 2 };
        healthy.Release.SetResult();
        var coordinator = new RefreshCoordinator(
            [new FakeAdapter(new ThrowingAvailabilityStrategy(), healthy)], accounts, new MemorySnapshots(),
            new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        var publication = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));

        Assert.Equal(RefreshPublicationStatus.Published, publication.Status);
        Assert.Contains(publication.Attempts, attempt => attempt.StrategyId == "throwing-availability"
            && attempt.FailureKind == FetchFailureKind.Unknown);
    }

    [Fact]
    public async Task StrategyCreationExceptionFailsThatAccountWithoutThrowing()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var coordinator = new RefreshCoordinator([new StrategyCreationThrowingAdapter()], accounts, new MemorySnapshots(),
            new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        var publication = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));

        Assert.Equal(RefreshPublicationStatus.FailedWithoutData, publication.Status);
    }

    [Fact]
    public async Task AllUnavailableStrategiesReplaceAStaleSuccessfulAttemptOnce()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var snapshots = new MemorySnapshots();
        snapshots.Attempts.Add(new FetchAttempt("previous", account.Key, "working", new FixedClock().UtcNow,
            TimeSpan.Zero, FetchFailureKind.None, "Success"));
        var coordinator = new RefreshCoordinator(
            [new FakeAdapter(
                new SkippedStrategy("not-configured", StrategyAvailability.NotConfigured),
                new SkippedStrategy("unsupported", StrategyAvailability.Unsupported))],
            accounts, snapshots, new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        var publication = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));

        Assert.Equal(RefreshPublicationStatus.FailedWithoutData, publication.Status);
        FetchAttempt unavailable = Assert.Single(publication.Attempts);
        Assert.Equal(FetchFailureKind.Unsupported, unavailable.FailureKind);
        Assert.Equal(2, snapshots.Attempts.Count);
        Assert.Equal(AccountHealth.SignInRequired,
            AccountHealthPolicy.Decide(isConnected: true, TimeSpan.FromSeconds(30), unavailable.FailureKind));
    }

    [Fact]
    public async Task TemporarilyUnavailableAvailabilityIsPersistedWithItsOwnFailureKind()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var snapshots = new MemorySnapshots();
        var coordinator = new RefreshCoordinator(
            [new FakeAdapter(new SkippedStrategy("locked-db", StrategyAvailability.TemporarilyUnavailable))],
            accounts, snapshots, new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        var publication = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));

        Assert.Equal(RefreshPublicationStatus.FailedWithoutData, publication.Status);
        var attempt = Assert.Single(publication.Attempts);
        Assert.Equal("locked-db", attempt.StrategyId);
        Assert.Equal(FetchFailureKind.TemporarilyUnavailable, attempt.FailureKind);
        Assert.Equal(FetchFailureKind.TemporarilyUnavailable, Assert.Single(snapshots.Attempts).FailureKind);
    }

    [Fact]
    public async Task ALaterAvailabilitySkipDoesNotMaskAnEarlierRealFailure()
    {
        var account = new ProviderAccount(new AccountKey(new ProviderId("fake"), "one"),
            "one", null, "fixture", 4, true);
        var accounts = new MemoryAccounts(account);
        var snapshots = new MemorySnapshots();
        var coordinator = new RefreshCoordinator(
            [new FakeAdapter(
                new FailingStrategy(FetchFailureKind.Network) { Order = 1 },
                new SkippedStrategy("not-configured", StrategyAvailability.NotConfigured) { Order = 2 })],
            accounts, snapshots, new SnapshotMerger(), new FixedClock(), NullLogger<RefreshCoordinator>.Instance);

        var publication = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 4));

        Assert.Equal(RefreshPublicationStatus.FailedWithoutData, publication.Status);
        var attempt = Assert.Single(publication.Attempts);
        Assert.Equal(FetchFailureKind.Network, attempt.FailureKind);
        Assert.Equal(FetchFailureKind.Network, Assert.Single(snapshots.Attempts).FailureKind);
    }

    [Fact]
    public async Task FixtureMeterPersistsEndToEndWithoutPresentationKnowledge()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(System.IO.Path.Combine(temporary.Path, "e2e.db"));
        await database.InitializeAsync();
        var accounts = new SqliteAccountRepository(database);
        var snapshots = new SqliteSnapshotRepository(database);
        var account = new ProviderAccount(FixtureProviderAdapter.FixtureAccount, "Fixture", null, "fixture", 1, true);
        await accounts.UpsertAsync(account, default);
        var adapter = new FixtureProviderAdapter(new FixedClock(), """
            {"limits":{"weekly":{"id":"weekly","used_percent":61},"fable":{"used":4,"limit":10,"model_id":"fable-x"}}}
            """);
        var coordinator = new RefreshCoordinator([adapter], accounts, snapshots, new SnapshotMerger(),
            new FixedClock(), NullLogger<RefreshCoordinator>.Instance);
        var publication = await coordinator.RefreshAsync(new RefreshRequest(account.Key, 1));
        Assert.Equal(RefreshPublicationStatus.Published, publication.Status);
        var reloaded = await snapshots.GetLatestAsync(account.Key, default);
        Assert.NotNull(reloaded);
        Assert.Equal(2, reloaded.Meters.Count);
        Assert.Contains(reloaded.Meters, meter => meter.DisplayName == "Fable");
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    }

    private sealed class ControlledStrategy(AccountKey account) : ILimitFetchStrategy
    {
        public int FetchCount;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string Id => "fake";
        public int Order { get; init; } = 1;
        public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            Task.FromResult(StrategyAvailabilityResult.Ready());

        public async Task<FetchResult> FetchAsync(ProviderAccount providerAccount, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref FetchCount);
            Started.TrySetResult();
            try
            {
                await Release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                Cancelled.TrySetResult();
                throw;
            }
            var at = DateTimeOffset.UtcNow;
            var meter = new UsageMeter(new MeterKey("one"), "One", MeterScope.Account, MeterUnit.Percent,
                null, null, 10, null, null, null, MeterStatus.Healthy,
                new MeterProvenance(Id, "$", at, true));
            return FetchResult.Success(new ProviderSnapshot(account, [meter], [], SnapshotCompleteness.Authoritative,
                at, DataConfidence.Exact, new Dictionary<string, JsonElement>()), Id, TimeSpan.Zero);
        }
    }

    private sealed class FakeAdapter(params ILimitFetchStrategy[] strategies) : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(new ProviderId("fake"), "Fake", "#000000",
            true, false, "fixture", ["fixture"]);
        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);
        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) => strategies;
        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) => [];
    }

    private sealed class ThrowingAvailabilityStrategy : ILimitFetchStrategy
    {
        public string Id => "throwing-availability";
        public int Order => 1;
        public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("availability probe exploded");
        public Task<FetchResult> FetchAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("should never be called");
    }

    private sealed class SkippedStrategy(string id, StrategyAvailability availability) : ILimitFetchStrategy
    {
        public string Id => id;
        public int Order { get; init; } = 1;
        public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            Task.FromResult(new StrategyAvailabilityResult(availability, "not runnable right now"));
        public Task<FetchResult> FetchAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("skipped strategies must never be fetched");
    }

    private sealed class FailingStrategy(FetchFailureKind kind) : ILimitFetchStrategy
    {
        public string Id => "failing";
        public int Order { get; init; } = 1;
        public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            Task.FromResult(StrategyAvailabilityResult.Ready());
        public Task<FetchResult> FetchAsync(ProviderAccount providerAccount, CancellationToken cancellationToken) =>
            Task.FromResult(FetchResult.Failure(kind, "boom", FallbackPolicy.TryNextStrategy, Id, TimeSpan.Zero));
    }

    private sealed class StrategyCreationThrowingAdapter : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(new ProviderId("fake"), "Fake", "#000000",
            true, false, "fixture", ["fixture"]);
        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);
        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) =>
            throw new InvalidOperationException("strategy creation exploded");
        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) => [];
    }

    private sealed class MemoryAccounts(ProviderAccount current) : IAccountRepository
    {
        public ProviderAccount Current { get; set; } = current;
        public Task<IReadOnlyList<ProviderAccount>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([Current]);
        public Task<ProviderAccount?> GetAsync(AccountKey key, CancellationToken cancellationToken) =>
            Task.FromResult<ProviderAccount?>(Current.Key == key ? Current : null);
        public Task UpsertAsync(ProviderAccount account, CancellationToken cancellationToken) { Current = account; return Task.CompletedTask; }
        public Task DeleteAsync(AccountKey key, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class MemorySnapshots : ISnapshotRepository
    {
        private ProviderSnapshot? _snapshot;
        public List<FetchAttempt> Attempts { get; } = [];
        public Task<ProviderSnapshot?> GetLatestAsync(AccountKey account, CancellationToken cancellationToken) => Task.FromResult(_snapshot);
        public Task SaveAsync(ProviderSnapshot snapshot, long generation, CancellationToken cancellationToken) { _snapshot = snapshot; return Task.CompletedTask; }
        public Task<IReadOnlyList<ProviderSnapshot>> GetHistoryAsync(AccountKey account, DateTimeOffset from, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderSnapshot>>(_snapshot is null ? [] : [_snapshot]);
        public Task RecordAttemptAsync(FetchAttempt attempt, CancellationToken cancellationToken) { Attempts.Add(attempt); return Task.CompletedTask; }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
