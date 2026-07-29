// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Infrastructure.Providers.Statuspage;

namespace AiLimits.Tests;

public sealed class ProviderStatusCacheTests
{
    private static readonly ProviderServiceStatus Incident = new("claude", "major", "Partial system outage");
    private static readonly ProviderServiceStatus Operational = new("claude", "none", "All Systems Operational");

    [Fact]
    public void A_fresh_entry_is_returned()
    {
        var clock = new MutableClock();
        var cache = new ProviderStatusCache(clock);
        cache.Merge(new Dictionary<string, ProviderServiceStatus> { ["claude"] = Incident });

        Assert.Equal(Incident, cache.Get("claude"));
    }

    [Fact]
    public void An_entry_older_than_the_ttl_disappears()
    {
        var clock = new MutableClock();
        var cache = new ProviderStatusCache(clock);
        cache.Merge(new Dictionary<string, ProviderServiceStatus> { ["claude"] = Incident });

        // A dead statuspage feed must not leave the banner up indefinitely.
        clock.UtcNow += TimeSpan.FromMinutes(31);

        Assert.Null(cache.Get("claude"));
    }

    [Fact]
    public void A_fresh_entry_survives_failed_polls_within_the_ttl()
    {
        var clock = new MutableClock();
        var cache = new ProviderStatusCache(clock);
        cache.Merge(new Dictionary<string, ProviderServiceStatus> { ["claude"] = Incident });

        // The next poll fails (empty merge); within the TTL the last known
        // incident still shows.
        clock.UtcNow += TimeSpan.FromMinutes(29);
        cache.Merge(new Dictionary<string, ProviderServiceStatus>());

        Assert.Equal(Incident, cache.Get("claude"));

        // ...but once it ages past the TTL without a successful poll, it drops.
        clock.UtcNow += TimeSpan.FromMinutes(2);
        Assert.Null(cache.Get("claude"));
    }

    [Fact]
    public void An_operational_response_clears_the_incident()
    {
        var clock = new MutableClock();
        var cache = new ProviderStatusCache(clock);
        cache.Merge(new Dictionary<string, ProviderServiceStatus> { ["claude"] = Incident });

        clock.UtcNow += TimeSpan.FromMinutes(10);
        cache.Merge(new Dictionary<string, ProviderServiceStatus> { ["claude"] = Operational });

        Assert.Equal(Operational, cache.Get("claude"));
        Assert.True(cache.Get("claude")!.IsOperational);
    }

    [Fact]
    public void The_custom_ttl_is_honoured()
    {
        var clock = new MutableClock();
        var cache = new ProviderStatusCache(clock, TimeSpan.FromMinutes(5));
        cache.Merge(new Dictionary<string, ProviderServiceStatus> { ["claude"] = Incident });

        clock.UtcNow += TimeSpan.FromMinutes(6);

        Assert.Null(cache.Get("claude"));
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    }
}
