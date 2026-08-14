// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Antigravity;

public sealed class AgyProviderAdapter : IProviderAdapter
{
    private static readonly AccountKey DefaultAccount = new(BuiltInProviderDescriptors.Antigravity.Id, "default");
    private readonly IClock _clock;
    private readonly AgyProcessDiscovery _processDiscovery;

    public AgyProviderAdapter(IClock clock)
        : this(clock, new AgyProcessDiscovery()) { }

    internal AgyProviderAdapter(IClock clock, AgyProcessDiscovery processDiscovery)
    {
        _clock = clock;
        _processDiscovery = processDiscovery;
    }

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Antigravity;

    public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool connected = _processDiscovery.FindListeningPorts().Count > 0;
        IReadOnlyList<ProviderAccount> accounts = new[]
        {
            new ProviderAccount(DefaultAccount, "Google Antigravity", null, "Existing agy session", 1, connected),
        };
        return Task.FromResult(accounts);
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) =>
        new[] { new AgyLimitStrategy(_clock, _processDiscovery) };

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) =>
        Array.Empty<ITokenUsageSource>();
}
