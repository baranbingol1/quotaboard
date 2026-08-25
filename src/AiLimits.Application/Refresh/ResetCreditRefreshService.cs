// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Application.Refresh;

/// <summary>
/// Owns the reset-credit inventories and their refresh cadence. A failed fetch
/// stores <c>null</c> for the account (rendered as "no credits" rather than as
/// an error), and results are held for the TTL so projections do not re-query
/// every provider on every paint.
/// </summary>
public sealed class ResetCreditRefreshService(IClock clock)
{
    /// <summary>How long a fetched inventory stays authoritative.</summary>
    public static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);

    private readonly Dictionary<AccountKey, ResetCreditInventory?> _inventories = [];
    private DateTimeOffset? _fetchedAt;

    public IReadOnlyDictionary<AccountKey, ResetCreditInventory?> Inventories => _inventories;

    public async Task RefreshIfDueAsync(
        IReadOnlyDictionary<ProviderId, IProviderAdapter> adapters,
        IReadOnlyList<ProviderAccount> accounts,
        bool force,
        CancellationToken cancellationToken
    )
    {
        if (!force && _fetchedAt.HasValue && clock.UtcNow - _fetchedAt.Value < RefreshInterval)
        {
            return;
        }
        _inventories.Clear();
        foreach (ProviderAccount account in accounts.Where(account => account.IsConnected))
        {
            if (
                !adapters.TryGetValue(account.Key.Provider, out IProviderAdapter adapter)
                || adapter is not IResetCreditSource source
            )
            {
                continue;
            }
            try
            {
                _inventories[account.Key] = await source
                    .FetchResetCreditsAsync(account, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _inventories[account.Key] = null;
            }
        }
        _fetchedAt = clock.UtcNow;
    }
}
