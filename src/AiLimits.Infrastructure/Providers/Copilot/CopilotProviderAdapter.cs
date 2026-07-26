// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Copilot;

public sealed class CopilotProviderAdapter(HttpClient httpClient, IClock clock) : IProviderAdapter
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Copilot;

    public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        bool isConnected = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AILIMITS_GITHUB_TOKEN"));
        IReadOnlyList<ProviderAccount> result = new ProviderAccount[] { new ProviderAccount(new AccountKey(Descriptor.Id, "github"), "GitHub Copilot", null, "GitHub device authorization", 1L, isConnected) };
        return Task.FromResult(result);
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
    {
        return new ILimitFetchStrategy[] { new CopilotQuotaStrategy(httpClient, clock) };
    }

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        return Array.Empty<ITokenUsageSource>();
    }
}
