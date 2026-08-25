// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Antigravity;

/// <summary>
/// Google Antigravity, read through its <c>agy</c> CLI.
///
/// The adapter owns the CLI session for the lifetime of the app rather than
/// creating one per fetch, so the warm <c>agy</c> process is shared across
/// refreshes and there is a single owner responsible for shutting it down.
/// </summary>
public sealed class AgyProviderAdapter : IProviderAdapter, IDisposable
{
    private static readonly AccountKey DefaultAccount = new(BuiltInProviderDescriptors.Antigravity.Id, "default");
    private readonly IClock _clock;
    private readonly AgyCliSession _session;
    private readonly bool _ownsSession;

    public AgyProviderAdapter(IClock clock)
        : this(clock, new AgyCliSession(new AgyProcessDiscovery()), ownsSession: true) { }

    internal AgyProviderAdapter(IClock clock, AgyCliSession session, bool ownsSession = false)
    {
        _clock = clock;
        _session = session;
        _ownsSession = ownsSession;
    }

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Antigravity;

    public Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Discovery must stay cheap and must not start the CLI: an installed agy
        // counts as connected, and the first refresh decides whether it can
        // actually answer.
        bool connected = _session.IsUsable();
        IReadOnlyList<ProviderAccount> accounts =
        [
            new ProviderAccount(DefaultAccount, "Google Antigravity", null, "Antigravity CLI", 1, connected),
        ];
        return Task.FromResult(accounts);
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) =>
        [new AgyLimitStrategy(_clock, _session)];

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) =>
        Array.Empty<ITokenUsageSource>();

    public void Dispose()
    {
        if (_ownsSession)
        {
            _session.Dispose();
        }
    }
}
