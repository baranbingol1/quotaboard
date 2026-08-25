// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Application.Refresh;
using AiLimits.Domain;
using Xunit;

namespace AiLimits.Tests;

public sealed class ResetCreditRefreshServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private sealed class FakeClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = Now;
    }

    private sealed class FakeResetCreditAdapter(ProviderDescriptor descriptor) : IProviderAdapter, IResetCreditSource
    {
        public int FetchCount { get; private set; }

        public ProviderDescriptor Descriptor { get; } = descriptor;

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) => [];

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) => [];

        public Task<ResetCreditInventory?> FetchResetCreditsAsync(
            ProviderAccount account,
            CancellationToken cancellationToken
        )
        {
            FetchCount++;
            return Task.FromResult<ResetCreditInventory?>(
                new ResetCreditInventory(
                    [new ResetCredit("c1", "reset", ResetCreditStatus.Available, Now, null, null, null)],
                    Now
                )
            );
        }
    }

    private static ProviderDescriptor Descriptor() =>
        new(new ProviderId("codex"), "Codex", "#000000", true, false, "", []);

    private static ProviderAccount Account(bool connected = true) =>
        new(new AccountKey(new ProviderId("codex"), "primary"), "Codex", "user@example.com", "CLI OAuth", 1, connected);

    [Fact]
    public async Task Fetches_once_within_the_ttl_and_again_after_it_expires()
    {
        var clock = new FakeClock();
        var adapter = new FakeResetCreditAdapter(Descriptor());
        var adapters = new Dictionary<ProviderId, IProviderAdapter> { [adapter.Descriptor.Id] = adapter };
        var service = new ResetCreditRefreshService(clock);

        await service.RefreshIfDueAsync(adapters, [Account()], force: false, CancellationToken.None);
        await service.RefreshIfDueAsync(adapters, [Account()], force: false, CancellationToken.None);

        Assert.Equal(1, adapter.FetchCount);
        Assert.NotNull(Assert.Single(service.Inventories).Value);

        clock.UtcNow += ResetCreditRefreshService.RefreshInterval + TimeSpan.FromMinutes(1);
        await service.RefreshIfDueAsync(adapters, [Account()], force: false, CancellationToken.None);

        Assert.Equal(2, adapter.FetchCount);
    }

    [Fact]
    public async Task Force_bypasses_the_ttl()
    {
        var clock = new FakeClock();
        var adapter = new FakeResetCreditAdapter(Descriptor());
        var adapters = new Dictionary<ProviderId, IProviderAdapter> { [adapter.Descriptor.Id] = adapter };
        var service = new ResetCreditRefreshService(clock);

        await service.RefreshIfDueAsync(adapters, [Account()], force: false, CancellationToken.None);
        await service.RefreshIfDueAsync(adapters, [Account()], force: true, CancellationToken.None);

        Assert.Equal(2, adapter.FetchCount);
    }

    [Fact]
    public async Task A_failing_source_stores_null_instead_of_failing_the_refresh()
    {
        var clock = new FakeClock();
        IProviderAdapter failing = new ThrowingAdapter(Descriptor());
        var adapters = new Dictionary<ProviderId, IProviderAdapter> { [failing.Descriptor.Id] = failing };
        var service = new ResetCreditRefreshService(clock);

        await service.RefreshIfDueAsync(adapters, [Account()], force: false, CancellationToken.None);

        Assert.Null(Assert.Single(service.Inventories).Value);
    }

    [Fact]
    public async Task Disconnected_accounts_and_non_credit_adapters_are_skipped()
    {
        var clock = new FakeClock();
        var adapter = new FakeResetCreditAdapter(Descriptor());
        var adapters = new Dictionary<ProviderId, IProviderAdapter> { [adapter.Descriptor.Id] = adapter };
        IProviderAdapter plain = new PlainAdapter(new ProviderId("plain"));
        adapters[plain.Descriptor.Id] = plain;
        var service = new ResetCreditRefreshService(clock);

        await service.RefreshIfDueAsync(adapters, [Account(connected: false)], force: false, CancellationToken.None);

        Assert.Empty(service.Inventories);
        Assert.Equal(0, adapter.FetchCount);
    }

    private sealed class ThrowingAdapter(ProviderDescriptor descriptor) : IProviderAdapter, IResetCreditSource
    {
        public ProviderDescriptor Descriptor { get; } = descriptor;

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) => [];

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) => [];

        public Task<ResetCreditInventory?> FetchResetCreditsAsync(
            ProviderAccount account,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("offline");
    }

    private sealed class PlainAdapter(ProviderId id) : IProviderAdapter
    {
        public ProviderDescriptor Descriptor { get; } = new(id, id.Value, "#000000", false, false, "", []);

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ProviderAccount>>([]);

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) => [];

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) => [];
    }
}
