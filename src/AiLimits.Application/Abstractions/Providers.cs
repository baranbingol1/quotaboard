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
/// Optional capability for a source whose per-scan work is expensive enough
/// that it must remember what it already did — <see cref="ScannerCursor.LastObservedAt"/>
/// alone cannot express "thread X was already exported at revision Y".
///
/// The scan loop reads <see cref="Position"/> once the enumeration has finished
/// and persists it in <see cref="ScannerCursor.Position"/>, handing it back on
/// the next scan. The value is opaque to the loop; only the source interprets
/// it. Sources that need no such state simply do not implement this.
/// </summary>
public interface IScanPositionSource
{
    /// <summary>
    /// Scan state to persist, or null to leave the stored position untouched.
    /// Only meaningful after <see cref="ITokenUsageSource.ReadAsync"/> has been
    /// enumerated to completion.
    /// </summary>
    string? Position { get; }
}

/// <summary>
/// Opt-in capability for a source that can expose an opaque position known to
/// be safe after failed enumeration. Unlike <see cref="IScanPositionSource.Position"/>,
/// this value must not cover any yielded event that may still be uncommitted.
/// </summary>
public interface IScanFailureCheckpointSource
{
    string? FailureCheckpoint { get; }
}

/// <summary>Conservative position selection for a failed source scan.</summary>
public static class ScanFailureCheckpoint
{
    public static ScannerCursor ResolveCursor(
        ITokenUsageSource source,
        string sourceId,
        string? startingPosition,
        DateTimeOffset? committedAt) =>
        new(sourceId,
            source is IScanFailureCheckpointSource checkpointSource
            ? checkpointSource.FailureCheckpoint ?? startingPosition
            : startingPosition,
            committedAt,
            null);
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
