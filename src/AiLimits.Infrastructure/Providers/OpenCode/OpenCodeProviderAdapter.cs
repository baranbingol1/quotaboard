// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.OpenCode;

public sealed class OpenCodeProviderAdapter(OpenCodePathDiscovery pathDiscovery) : IProviderAdapter
{
    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.OpenCode;

    public async Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        string path = await pathDiscovery.FindDatabaseAsync(cancellationToken).ConfigureAwait(false);
        string authPath = Path.Combine(Path.GetDirectoryName(path) ?? string.Empty, "auth.json");
        if (!File.Exists(path) && !File.Exists(authPath))
        {
            return Array.Empty<ProviderAccount>();
        }
        return new ProviderAccount[]
        {
            new ProviderAccount(
                new AccountKey(Descriptor.Id, "local"),
                "OpenCode CLI history",
                null,
                File.Exists(authPath) ? "Local history + provider auth metadata" : "Local usage database",
                1L,
                IsConnected: true
            ),
        };
    }

    // Deliberately empty: local history alone never implies a Go/Zen
    // subscription (see OpenCodeHistoryDoesNotImplyAnOpenCodeSubscription).
    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
    {
        return Array.Empty<ILimitFetchStrategy>();
    }

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        return new ITokenUsageSource[] { new OpenCodeDatabaseTokenSource(pathDiscovery) };
    }
}
