// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Shared;

namespace AiLimits.Infrastructure.Providers.Fixtures;

public sealed class FixtureProviderAdapter(IClock clock, string fixtureJson) : IProviderAdapter
{
    private sealed class FixtureStrategy(IClock clock, string json) : ILimitFetchStrategy
    {
        public string Id => "fixture.dynamic-json";

        public int Order => 1;

        public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
            ProviderAccount account,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(StrategyAvailabilityResult.Ready());
        }

        public Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
        {
            try
            {
                using JsonDocument jsonDocument = JsonDocument.Parse(json);
                DateTimeOffset utcNow = clock.UtcNow;
                IReadOnlyList<UsageMeter> meters = new DynamicMeterExtractor().Extract(
                    account.Key.Provider,
                    jsonDocument.RootElement,
                    Id,
                    utcNow,
                    authoritative: true
                );
                ProviderSnapshot snapshot = new ProviderSnapshot(
                    account.Key,
                    meters,
                    Array.Empty<BalanceMetric>(),
                    SnapshotCompleteness.Authoritative,
                    utcNow,
                    DataConfidence.Exact,
                    new Dictionary<string, JsonElement>()
                );
                return Task.FromResult(FetchResult.Success(snapshot, Id, TimeSpan.Zero));
            }
            catch (JsonException)
            {
                return Task.FromResult(
                    FetchResult.Failure(
                        FetchFailureKind.MalformedResponse,
                        "Fixture JSON is malformed.",
                        FallbackPolicy.Stop,
                        Id,
                        TimeSpan.Zero
                    )
                );
            }
        }
    }

    public static readonly ProviderId FixtureId = new ProviderId("fixture");

    public static readonly AccountKey FixtureAccount = new AccountKey(FixtureId, "local-demo");

    public ProviderDescriptor Descriptor =>
        new ProviderDescriptor(
            FixtureId,
            "Fixture",
            "#4F7D6B",
            SupportsMultipleAccounts: true,
            SupportsExactTokens: true,
            "Synthetic exact coverage for offline development.",
            new string[] { "Offline fixture" }
        );

    public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        return Task.FromResult(
            (IReadOnlyList<ProviderAccount>)
                new ProviderAccount[]
                {
                    new ProviderAccount(
                        FixtureAccount,
                        "Offline fixture",
                        "fixture@local",
                        "Fixture",
                        1L,
                        IsConnected: true
                    ),
                }
        );
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
    {
        return new ILimitFetchStrategy[] { new FixtureStrategy(clock, fixtureJson) };
    }

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        return Array.Empty<ITokenUsageSource>();
    }
}
