// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Usage;
using AiLimits.Domain;

namespace AiLimits.Tests;

public sealed class TelemetryScanServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ASecondSweepInsideTheIntervalIsSkippedEntirely()
    {
        var clock = new FakeClock();
        var source = new FakeSource("codex.sessions", [Event(Now)]);
        using var service = new TelemetryScanService(new FakeUsageStore(), clock);

        Assert.NotNull(await service.ScanIfDueAsync(Adapters(source), [Account()], null, default));
        Assert.Equal(1, source.ReadCount);

        clock.UtcNow = Now.Add(TelemetryScanService.ScanInterval).AddSeconds(-1);

        Assert.Null(await service.ScanIfDueAsync(Adapters(source), [Account()], null, default));
        Assert.Equal(1, source.ReadCount);
    }

    [Fact]
    public async Task TheSweepRunsAgainOnceTheIntervalHasElapsed()
    {
        var clock = new FakeClock();
        var source = new FakeSource("codex.sessions", [Event(Now)]);
        using var service = new TelemetryScanService(new FakeUsageStore(), clock);

        await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);
        clock.UtcNow = Now.Add(TelemetryScanService.ScanInterval).AddSeconds(1);
        var second = await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);

        Assert.NotNull(second);
        Assert.Equal(2, source.ReadCount);
    }

    [Fact]
    public async Task TheStartingHookFiresOnlyWhenASweepActuallyRuns()
    {
        var clock = new FakeClock();
        var source = new FakeSource("codex.sessions", []);
        using var service = new TelemetryScanService(new FakeUsageStore(), clock);
        int started = 0;

        await service.ScanIfDueAsync(Adapters(source), [Account()], () => started++, default);
        await service.ScanIfDueAsync(Adapters(source), [Account()], () => started++, default);

        Assert.Equal(1, started);
    }

    [Fact]
    public async Task AFailedSourceIsCountedAsFailedRatherThanAsHavingNothingNew()
    {
        // The bug this guards: a source that returned nothing because it got a
        // 401 used to be indistinguishable from one that was simply up to date.
        var store = new FakeUsageStore();
        var healthy = new FakeSource("codex.sessions", [Event(Now)]);
        var broken = new FakeSource("amp.threads", []) { Throw = new TokenScanException("amp export failed") };
        using var service = new TelemetryScanService(store, new FakeClock());

        var outcome = await service.ScanIfDueAsync(Adapters(healthy, broken), [Account()], null, default);

        Assert.NotNull(outcome);
        Assert.Equal(2, outcome!.Value.SourceCount);
        Assert.Equal(1, outcome.Value.SuccessfulSourceCount);
        Assert.True(outcome.Value.HasFailure);
        Assert.Contains("amp export failed", outcome.Value.FirstFailureMessage!, StringComparison.Ordinal);
        // The healthy source still drained; one bad source does not abort the sweep.
        Assert.Single(store.Events);
    }

    [Fact]
    public async Task AnEmptySweepReportsNoEventsWithoutClaimingAFailure()
    {
        using var service = new TelemetryScanService(new FakeUsageStore(), new FakeClock());

        var outcome = await service.ScanIfDueAsync(
            Adapters(new FakeSource("codex.sessions", [])),
            [Account()],
            null,
            default
        );

        Assert.NotNull(outcome);
        Assert.Equal(1, outcome!.Value.SuccessfulSourceCount);
        Assert.False(outcome.Value.EmittedEvents);
        Assert.False(outcome.Value.HasFailure);
    }

    [Fact]
    public async Task AnAccountWithNoAdapterContributesNoSources()
    {
        using var service = new TelemetryScanService(new FakeUsageStore(), new FakeClock());

        var outcome = await service.ScanIfDueAsync(
            new Dictionary<ProviderId, IProviderAdapter>(),
            [Account()],
            null,
            default
        );

        Assert.NotNull(outcome);
        Assert.Equal(0, outcome!.Value.SourceCount);
    }

    [Fact]
    public async Task ASuccessfulSweepAdvancesTheCursorToTheNewestEventItSaved()
    {
        var store = new FakeUsageStore();
        var newest = Now.AddMinutes(5);
        var source = new FakeSource("codex.sessions", [Event(Now), Event(newest), Event(Now.AddMinutes(2))]);
        using var service = new TelemetryScanService(store, new FakeClock());

        var outcome = await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);

        Assert.Equal(3, outcome!.Value.EmittedEventCount);
        var cursor = Assert.Single(store.Cursors).Value;
        Assert.Equal(newest, cursor.LastObservedAt);
        Assert.Equal("incremental", cursor.Position);
    }

    [Fact]
    public async Task AFailedWriteLeavesTheCursorOnWhatReachedTheDatabase()
    {
        // Only events that were actually persisted may advance the cursor —
        // otherwise a failed batch is skipped forever on the next sweep.
        var store = new FakeUsageStore { FailWrites = true };
        var source = new FakeSource("codex.sessions", [Event(Now.AddMinutes(9))]);
        using var service = new TelemetryScanService(store, new FakeClock());

        var outcome = await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);

        Assert.True(outcome!.Value.HasFailure);
        var cursor = Assert.Single(store.Cursors).Value;
        Assert.Null(cursor.LastObservedAt);
        Assert.Empty(store.Events);
    }

    [Fact]
    public async Task AStatefulSourceKeepsItsStoredPositionWhenItReportsNothingNew()
    {
        var store = new FakeUsageStore();
        store.Cursors[("codex", "amp.threads")] = new ScannerCursor("amp.threads", "thread-7", Now, null);
        var source = new StatefulFakeSource("amp.threads", position: null);
        using var service = new TelemetryScanService(store, new FakeClock());

        await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);

        Assert.Equal("thread-7", store.Cursors[("codex", "amp.threads")].Position);
    }

    [Fact]
    public async Task AStatefulSourceThatFailedDoesNotKeepItsNewPosition()
    {
        // Its new position asserts every item was processed, but items it
        // yielded may have died with the batch that failed to write.
        var store = new FakeUsageStore();
        store.Cursors[("codex", "amp.threads")] = new ScannerCursor("amp.threads", "thread-7", Now, null);
        var source = new StatefulFakeSource("amp.threads", position: "thread-9")
        {
            Throw = new TokenScanException("export timed out"),
        };
        using var service = new TelemetryScanService(store, new FakeClock());

        await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);

        Assert.Equal("thread-7", store.Cursors[("codex", "amp.threads")].Position);
    }

    [Fact]
    public async Task IsScanningIsFalseOnceTheSweepUnwinds()
    {
        using var service = new TelemetryScanService(new FakeUsageStore(), new FakeClock());
        Assert.False(service.IsScanning);

        var source = new FakeSource("codex.sessions", []) { Throw = new TokenScanException("boom") };
        await service.ScanIfDueAsync(Adapters(source), [Account()], null, default);

        // A stale "still indexing" caption must not outlive a failed sweep.
        Assert.False(service.IsScanning);
    }

    private static AccountKey Key() => new(new ProviderId("codex"), "primary");

    private static ProviderAccount Account() =>
        new(Key(), "Codex", "user@example.com", "CLI OAuth", 1, IsConnected: true);

    private static TokenUsageEvent Event(DateTimeOffset occurredAt) =>
        new(
            Key(),
            new ServiceProviderId("openai"),
            "gpt-5",
            occurredAt,
            10,
            20,
            0,
            0,
            0,
            occurredAt.ToUnixTimeMilliseconds().ToString(System.Globalization.CultureInfo.InvariantCulture)
        );

    private static Dictionary<ProviderId, IProviderAdapter> Adapters(params ITokenUsageSource[] sources) =>
        new() { [new ProviderId("codex")] = new FakeAdapter(sources) };

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class FakeAdapter(IReadOnlyList<ITokenUsageSource> sources) : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } =
            new(new ProviderId("codex"), "Codex", "#000000", true, true, "", []);

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) => [];

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) => sources;
    }

    private class FakeSource(string id, IReadOnlyList<TokenUsageEvent> events) : ITokenUsageSource
    {
        public string Id { get; } = id;

        public int ReadCount { get; private set; }

        /// <summary>Thrown after the events below have been yielded.</summary>
        public Exception? Throw { get; init; }

        public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
            ProviderAccount account,
            ScannerCursor? cursor,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            ReadCount++;
            foreach (TokenUsageEvent item in events)
            {
                await Task.Yield();
                yield return item;
            }
            if (Throw is not null)
            {
                throw Throw;
            }
        }
    }

    private sealed class StatefulFakeSource(string id, string? position) : FakeSource(id, []), IScanPositionSource
    {
        public string? Position { get; } = position;
    }

    private sealed class FakeUsageStore : IUsageAggregateRepository
    {
        public List<TokenUsageEvent> Events { get; } = [];

        public Dictionary<(string Provider, string Source), ScannerCursor> Cursors { get; } = [];

        public bool FailWrites { get; init; }

        public Task AddEventsAsync(IEnumerable<TokenUsageEvent> events, CancellationToken cancellationToken)
        {
            if (FailWrites)
            {
                throw new InvalidOperationException("database is locked");
            }
            Events.AddRange(events);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<DailyUsageAggregate>> QueryAsync(
            DateOnly from,
            DateOnly through,
            IReadOnlyCollection<AccountKey>? accounts,
            CancellationToken cancellationToken
        ) => Task.FromResult<IReadOnlyList<DailyUsageAggregate>>([]);

        public Task<ScannerCursor?> GetCursorAsync(
            AccountKey account,
            string sourceId,
            CancellationToken cancellationToken
        ) =>
            Task.FromResult(
                Cursors.TryGetValue((account.Provider.Value, sourceId), out ScannerCursor? cursor) ? cursor : null
            );

        public Task SaveCursorAsync(AccountKey account, ScannerCursor cursor, CancellationToken cancellationToken)
        {
            Cursors[(account.Provider.Value, cursor.SourceId)] = cursor;
            return Task.CompletedTask;
        }
    }
}
