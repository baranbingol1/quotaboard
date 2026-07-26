// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Abstractions;

public interface IProviderAdapter
{
    ProviderDescriptor Descriptor { get; }

    Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken);

    IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account);

    IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account);
}

public interface ILimitFetchStrategy
{
    string Id { get; }

    int Order { get; }

    Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount account, CancellationToken cancellationToken);

    Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken);
}

/// <summary>
/// Thrown by an <see cref="ITokenUsageSource"/> whose scan could not complete.
///
/// A source that simply has nothing new returns an empty sequence; one that
/// failed must say so, or the scan loop cannot tell "no new events" from "the
/// endpoint returned 401" and would report a silent failure as a healthy scan.
/// The message is shown in diagnostics, so it must already be safe to display.
/// </summary>
public sealed class TokenScanException(string safeMessage, Exception? innerException = null)
    : Exception(safeMessage, innerException);

public interface ITokenUsageSource
{
    string Id { get; }

    IAsyncEnumerable<TokenUsageEvent> ReadAsync(ProviderAccount account, ScannerCursor? cursor, CancellationToken cancellationToken);
}

/// <summary>
/// Optional provider capability: an inventory of redeemable rate-limit reset
/// credits ("N resets left, expiring at X"). Implemented by adapters whose
/// provider grants such credits; fetches are best-effort and must never fail
/// a refresh.
/// </summary>
public interface IResetCreditSource
{
    Task<ResetCreditInventory?> FetchResetCreditsAsync(ProviderAccount account, CancellationToken cancellationToken);
}
