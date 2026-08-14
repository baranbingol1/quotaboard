// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using Microsoft.Extensions.Logging;

namespace AiLimits.Application.Discovery;

/// <summary>
/// Runs account discovery across all provider adapters and reconciles the
/// results with saved accounts. A provider whose discovery throws is treated
/// as <em>failed</em>, not as an authoritative empty result: its saved
/// accounts keep their connection state and configuration revision. Only a
/// discovery that completed successfully may disconnect accounts, and only
/// accounts belonging to that same provider.
/// </summary>
public sealed class AccountDiscoveryService
{
    private readonly IReadOnlyList<IProviderAdapter> _adapters;

    private readonly IAccountRepository _accounts;

    private readonly ILogger _logger;

    public AccountDiscoveryService(
        IReadOnlyList<IProviderAdapter> adapters,
        IAccountRepository accounts,
        ILogger<AccountDiscoveryService> logger
    )
    {
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _accounts = accounts ?? throw new ArgumentNullException(nameof(accounts));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<ProviderAccount>> DiscoverAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<ProviderAccount> existing = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
        Dictionary<AccountKey, ProviderAccount> existingByKey = existing.ToDictionary(
            (ProviderAccount account) => account.Key
        );
        HashSet<AccountKey> discoveredKeys = new HashSet<AccountKey>();
        HashSet<ProviderId> succeededProviders = new HashSet<ProviderId>();
        foreach (IProviderAdapter adapter in _adapters)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ProviderAccount> accounts;
            try
            {
                accounts = await adapter.DiscoverAccountsAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Account discovery failed for provider {Provider}; saved accounts are preserved.",
                    adapter.Descriptor.Id.Value
                );
                continue;
            }
            succeededProviders.Add(adapter.Descriptor.Id);
            foreach (ProviderAccount account in accounts)
            {
                ProviderAccount merged = MergeAccount(existingByKey.GetValueOrDefault(account.Key), account);
                await _accounts.UpsertAsync(merged, cancellationToken).ConfigureAwait(false);
                discoveredKeys.Add(merged.Key);
            }
        }
        foreach (
            ProviderAccount vanished in existing.Where(
                (ProviderAccount account) =>
                    account.IsConnected
                    && succeededProviders.Contains(account.Key.Provider)
                    && !discoveredKeys.Contains(account.Key)
            )
        )
        {
            await _accounts
                .UpsertAsync(
                    vanished with
                    {
                        IsConnected = false,
                        ConfigurationRevision = vanished.ConfigurationRevision + 1,
                    },
                    cancellationToken
                )
                .ConfigureAwait(false);
        }
        return await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProviderAccount MergeAccount(ProviderAccount? existing, ProviderAccount discovered)
    {
        if ((object?)existing == null)
        {
            return discovered;
        }
        bool authChanged =
            !string.Equals(existing.AuthSource, discovered.AuthSource, StringComparison.Ordinal)
            || !string.Equals(existing.Login, discovered.Login, StringComparison.Ordinal);
        return discovered with
        {
            ConfigurationRevision = (
                authChanged ? (existing.ConfigurationRevision + 1) : existing.ConfigurationRevision
            ),
            LastSuccessfulRefreshAt = existing.LastSuccessfulRefreshAt,
        };
    }
}
