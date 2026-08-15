// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;

namespace AiLimits.Infrastructure.Providers.Statuspage;

/// <summary>
/// Last-known provider service statuses with a time-to-live. A status banner
/// is only as trustworthy as the poll that produced it: once the newest entry
/// for a provider is older than the TTL (a dead statuspage feed, a proxy
/// replaying stale JSON), the entry is dropped rather than leaving a banner
/// up indefinitely. Expiry is checked on every merge and every read, so a
/// stale entry cannot survive by simply never being polled again. The cache
/// is in-memory only; app restarts start empty as before.
/// </summary>
public sealed class ProviderStatusCache(IClock clock, TimeSpan? timeToLive = null)
{
    public static readonly TimeSpan DefaultTimeToLive = TimeSpan.FromMinutes(30);

    private readonly TimeSpan _timeToLive = timeToLive ?? DefaultTimeToLive;

    private readonly Dictionary<string, (ProviderServiceStatus Status, DateTimeOffset FetchedAt)> _entries = new(
        StringComparer.OrdinalIgnoreCase
    );

    /// <summary>
    /// Records the freshly polled statuses. Operational responses replace
    /// incidents too, which clears the warning on the next successful poll.
    /// </summary>
    public void Merge(IReadOnlyDictionary<string, ProviderServiceStatus> statuses)
    {
        PruneExpired();
        foreach (KeyValuePair<string, ProviderServiceStatus> pair in statuses)
        {
            _entries[pair.Key] = (pair.Value, clock.UtcNow);
        }
    }

    public ProviderServiceStatus? Get(string providerId)
    {
        PruneExpired();
        return _entries.TryGetValue(providerId, out (ProviderServiceStatus Status, DateTimeOffset FetchedAt) entry)
            ? entry.Status
            : null;
    }

    private void PruneExpired()
    {
        DateTimeOffset cutoff = clock.UtcNow - _timeToLive;
        List<string>? expired = null;
        foreach (KeyValuePair<string, (ProviderServiceStatus Status, DateTimeOffset FetchedAt)> pair in _entries)
        {
            if (pair.Value.FetchedAt < cutoff)
            {
                (expired ??= []).Add(pair.Key);
            }
        }
        if (expired is null)
        {
            return;
        }
        foreach (string key in expired)
        {
            _entries.Remove(key);
        }
    }
}
