// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Discovery;
using AiLimits.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AiLimits.Tests;

public sealed class AccountDiscoveryServiceTests
{
    [Fact]
    public async Task Discovery_exception_preserves_saved_accounts_and_revision()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        ProviderAccount saved = Account(
            "codex",
            "user@example.com",
            revision: 3,
            connected: true,
            lastSuccess: DateTimeOffset.UtcNow.AddMinutes(-5)
        );
        await repository.UpsertAsync(saved, CancellationToken.None);
        AccountDiscoveryService service = Service(repository, new ThrowingAdapter("codex"));

        IReadOnlyList<ProviderAccount> result = await service.DiscoverAsync(CancellationToken.None);

        ProviderAccount preserved = Assert.Single(result);
        Assert.True(preserved.IsConnected);
        Assert.Equal(3, preserved.ConfigurationRevision);
        Assert.Equal(saved.LastSuccessfulRefreshAt, preserved.LastSuccessfulRefreshAt);
    }

    [Fact]
    public async Task Successful_empty_discovery_disconnects_that_providers_accounts()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        await repository.UpsertAsync(
            Account("codex", "user@example.com", revision: 3, connected: true),
            CancellationToken.None
        );
        AccountDiscoveryService service = Service(repository, new ScriptedAdapter("codex"));

        IReadOnlyList<ProviderAccount> result = await service.DiscoverAsync(CancellationToken.None);

        ProviderAccount disconnected = Assert.Single(result);
        Assert.False(disconnected.IsConnected);
        Assert.Equal(4, disconnected.ConfigurationRevision);
    }

    [Fact]
    public async Task One_providers_failure_never_affects_another_provider()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        await repository.UpsertAsync(
            Account("codex", "codex@example.com", revision: 1, connected: true),
            CancellationToken.None
        );
        await repository.UpsertAsync(
            Account("claude", "claude@example.com", revision: 1, connected: true),
            CancellationToken.None
        );
        AccountDiscoveryService service = Service(
            repository,
            new ThrowingAdapter("codex"),
            new ScriptedAdapter("claude")
        );

        IReadOnlyList<ProviderAccount> result = await service.DiscoverAsync(CancellationToken.None);

        ProviderAccount codex = result.Single(account => account.Key.Provider.Value == "codex");
        ProviderAccount claude = result.Single(account => account.Key.Provider.Value == "claude");
        Assert.True(codex.IsConnected);
        Assert.Equal(1, codex.ConfigurationRevision);
        Assert.False(claude.IsConnected);
        Assert.Equal(2, claude.ConfigurationRevision);
    }

    [Fact]
    public async Task Rediscovery_does_not_duplicate_accounts_and_keeps_last_success()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        DateTimeOffset lastSuccess = DateTimeOffset.UtcNow.AddHours(-1);
        ProviderAccount saved = Account(
            "codex",
            "user@example.com",
            revision: 2,
            connected: true,
            lastSuccess: lastSuccess
        );
        await repository.UpsertAsync(saved, CancellationToken.None);
        AccountDiscoveryService service = Service(
            repository,
            new ScriptedAdapter("codex", Account("codex", "user@example.com", revision: 0, connected: true))
        );

        IReadOnlyList<ProviderAccount> first = await service.DiscoverAsync(CancellationToken.None);
        IReadOnlyList<ProviderAccount> second = await service.DiscoverAsync(CancellationToken.None);

        ProviderAccount account = Assert.Single(second);
        Assert.Single(first);
        Assert.Equal(2, account.ConfigurationRevision);
        Assert.Equal(lastSuccess, account.LastSuccessfulRefreshAt);
    }

    [Fact]
    public async Task Only_auth_or_login_changes_increment_configuration_revision()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        await repository.UpsertAsync(
            Account("codex", "user@example.com", revision: 2, connected: true),
            CancellationToken.None
        );
        ProviderAccount changedAuth = Account("codex", "user@example.com", revision: 0, connected: true) with
        {
            AuthSource = "Codex CLI OAuth (device)",
        };
        AccountDiscoveryService service = Service(repository, new ScriptedAdapter("codex", changedAuth));

        IReadOnlyList<ProviderAccount> result = await service.DiscoverAsync(CancellationToken.None);

        Assert.Equal(3, Assert.Single(result).ConfigurationRevision);
    }

    [Fact]
    public async Task Failed_provider_skips_disconnect_even_when_other_accounts_are_discovered()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        await repository.UpsertAsync(
            Account("codex", "a@example.com", revision: 1, connected: true),
            CancellationToken.None
        );
        AccountDiscoveryService service = Service(
            repository,
            new ThrowingAdapter("codex"),
            new ScriptedAdapter("claude", Account("claude", "b@example.com", revision: 0, connected: true))
        );

        IReadOnlyList<ProviderAccount> result = await service.DiscoverAsync(CancellationToken.None);

        Assert.True(result.Single(account => account.Key.Provider.Value == "codex").IsConnected);
        Assert.True(result.Single(account => account.Key.Provider.Value == "claude").IsConnected);
    }

    [Fact]
    public async Task Cancellation_propagates_instead_of_being_swallowed()
    {
        FakeAccountRepository repository = new FakeAccountRepository();
        using CancellationTokenSource source = new CancellationTokenSource();
        AccountDiscoveryService service = Service(repository, new CancellingAdapter("codex", source));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DiscoverAsync(source.Token));
    }

    private static AccountDiscoveryService Service(IAccountRepository repository, params IProviderAdapter[] adapters)
    {
        return new AccountDiscoveryService(adapters, repository, NullLogger<AccountDiscoveryService>.Instance);
    }

    private static ProviderAccount Account(
        string provider,
        string login,
        long revision,
        bool connected,
        DateTimeOffset? lastSuccess = null
    )
    {
        return new ProviderAccount(
            new AccountKey(new ProviderId(provider), login),
            provider,
            login,
            provider + " auth",
            revision,
            connected,
            lastSuccess
        );
    }

    private static ProviderDescriptor Descriptor(string id)
    {
        return new ProviderDescriptor(
            new ProviderId(id),
            id,
            "#333333",
            SupportsMultipleAccounts: true,
            SupportsExactTokens: false,
            "test",
            Array.Empty<string>()
        );
    }

    private sealed class ScriptedAdapter : IProviderAdapter
    {
        private readonly ProviderAccount[] _accounts;

        public ScriptedAdapter(string providerId, params ProviderAccount[] accounts)
        {
            Descriptor = AccountDiscoveryServiceTests.Descriptor(providerId);
            _accounts = accounts;
        }

        public ProviderDescriptor Descriptor { get; }

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProviderAccount>>(_accounts);
        }

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
        {
            return Array.Empty<ILimitFetchStrategy>();
        }

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
        {
            return Array.Empty<ITokenUsageSource>();
        }
    }

    private sealed class ThrowingAdapter : IProviderAdapter
    {
        public ThrowingAdapter(string providerId)
        {
            Descriptor = AccountDiscoveryServiceTests.Descriptor(providerId);
        }

        public ProviderDescriptor Descriptor { get; }

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
        {
            throw new InvalidOperationException("discovery blew up");
        }

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
        {
            return Array.Empty<ILimitFetchStrategy>();
        }

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
        {
            return Array.Empty<ITokenUsageSource>();
        }
    }

    private sealed class CancellingAdapter : IProviderAdapter
    {
        private readonly CancellationTokenSource _source;

        public CancellingAdapter(string providerId, CancellationTokenSource source)
        {
            Descriptor = AccountDiscoveryServiceTests.Descriptor(providerId);
            _source = source;
        }

        public ProviderDescriptor Descriptor { get; }

        public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
        {
            _source.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<ProviderAccount>>(Array.Empty<ProviderAccount>());
        }

        public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
        {
            return Array.Empty<ILimitFetchStrategy>();
        }

        public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
        {
            return Array.Empty<ITokenUsageSource>();
        }
    }

    private sealed class FakeAccountRepository : IAccountRepository
    {
        private readonly Dictionary<AccountKey, ProviderAccount> _accounts =
            new Dictionary<AccountKey, ProviderAccount>();

        public Task<IReadOnlyList<ProviderAccount>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<ProviderAccount>>(
                _accounts.Values.OrderBy(account => account.Key.ToString(), StringComparer.Ordinal).ToArray()
            );
        }

        public Task<ProviderAccount?> GetAsync(AccountKey key, CancellationToken cancellationToken)
        {
            return Task.FromResult(_accounts.GetValueOrDefault(key));
        }

        public Task UpsertAsync(ProviderAccount account, CancellationToken cancellationToken)
        {
            _accounts[account.Key] = account;
            return Task.CompletedTask;
        }

        public Task DeleteAsync(AccountKey key, CancellationToken cancellationToken)
        {
            _accounts.Remove(key);
            return Task.CompletedTask;
        }
    }
}
