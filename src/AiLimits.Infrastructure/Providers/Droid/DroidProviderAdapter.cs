// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Droid;

public sealed class DroidProviderAdapter : IProviderAdapter
{
    private readonly HttpClient _httpClient;

    private readonly IClock _clock;

    private readonly FactoryCredentialReader _credentialReader;

    private readonly string _factoryHome;

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Droid;

    public DroidProviderAdapter(HttpClient httpClient, IClock clock, string? factoryHome = null)
    {
        _httpClient = httpClient;
        _clock = clock;
        _factoryHome =
            factoryHome ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".factory");
        _credentialReader = new FactoryCredentialReader(_factoryHome);
    }

    public async Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        FactoryCredential credential = await _credentialReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        bool hasApiKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_API_KEY"));
        return new ProviderAccount[]
        {
            new ProviderAccount(
                IsConnected: credential is not null || hasApiKey,
                Key: new AccountKey(Descriptor.Id, "default"),
                DisplayName: "Factory / Droid",
                Login: credential?.Email,
                AuthSource: (credential is not null) ? "Factory CLI session" : "Optional API key",
                ConfigurationRevision: 1L
            ),
        };
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
    {
        return new ILimitFetchStrategy[]
        {
            new DroidSessionLimitStrategy(_httpClient, _clock, _credentialReader),
            new DroidApiLimitStrategy(_httpClient, _clock),
        };
    }

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        return new ITokenUsageSource[] { new FactoryLogTokenSource(_factoryHome) };
    }
}
