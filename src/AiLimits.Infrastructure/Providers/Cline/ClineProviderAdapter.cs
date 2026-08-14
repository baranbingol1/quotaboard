// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>
/// One Cline card: ClinePass limit meters from the account API plus exact
/// per-request tokens from the local Cline logs (VS Code extension and CLI).
/// The account exists whenever either half has something to read.
/// </summary>
public sealed class ClineProviderAdapter(
    HttpClient http,
    IClock clock,
    IReadOnlyList<string>? roots = null,
    Func<ClineCredential?>? credentialProbe = null,
    ISecretStore? secrets = null,
    string? legacySessionCachePath = null
) : IProviderAdapter
{
    // Built once so the one-time migration off the old plaintext cache file
    // does not re-run on every fetch.
    private readonly ClineSessionStore? _sessionStore = secrets is null
        ? null
        : new ClineSessionStore(secrets, legacySessionCachePath);

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Cline;

    public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        bool hasLogs = EffectiveRoots().Count > 0;
        ClineCredential? credential = ResolveCredential();
        IReadOnlyList<ProviderAccount> result =
        [
            new ProviderAccount(
                new AccountKey(Descriptor.Id, "default"),
                "Cline",
                credential?.Email,
                credential is not null ? credential.SourceLabel : "Local logs",
                1,
                credential is not null || hasLogs
            ),
        ];
        return Task.FromResult(result);
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) =>
        [new ClinePassLimitStrategy(http, clock, credentialProbe, _sessionStore)];

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        IReadOnlyList<string> effective = EffectiveRoots();
        return effective.Count == 0 ? [] : [new ClineTaskHistoryTokenSource(effective)];
    }

    private IReadOnlyList<string> EffectiveRoots() => roots ?? ClinePathDiscovery.FindRoots().ToArray();

    private ClineCredential? ResolveCredential() =>
        credentialProbe is not null ? credentialProbe() : ClineCredentialReader.Resolve();
}
