// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Refresh;
using AiLimits.Application.Snapshots;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using AiLimits.Infrastructure.Providers.Fixtures;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLimits.IntegrationTests;

/// <summary>
/// End-to-end refresh against a real SQLite database and the built-in
/// fixture adapter. This is the composition the app host uses: discover
/// an account, fetch a snapshot, persist it, then read it back.
/// </summary>
public sealed class RefreshPipelineIntegrationTests
{
    [Fact]
    public async Task Fixture_refresh_persists_an_authoritative_snapshot()
    {
        using var temporary = new TemporaryDatabase();
        var database = new SqliteDatabase(temporary.PathToFile);
        await database.InitializeAsync();

        const string fixture = """
            {
              "limits": {
                "weekly": { "id": "week", "used": 12, "limit": 50, "resets_at": "2026-07-20T00:00:00Z" }
              }
            }
            """;

        var clock = new FixedClock(new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero));
        var adapter = new FixtureProviderAdapter(clock, fixture);
        var accounts = new SqliteAccountRepository(database);
        var snapshots = new SqliteSnapshotRepository(database);

        IReadOnlyList<ProviderAccount> discovered = await adapter.DiscoverAccountsAsync(default);
        ProviderAccount account = Assert.Single(discovered);
        await accounts.UpsertAsync(account, default);

        using var coordinator = new RefreshCoordinator(
            [adapter],
            accounts,
            snapshots,
            new SnapshotMerger(),
            clock,
            NullLogger<RefreshCoordinator>.Instance
        );

        RefreshPublication published = await coordinator.RefreshAsync(
            new RefreshRequest(account.Key, account.ConfigurationRevision)
        );

        Assert.Equal(RefreshPublicationStatus.Published, published.Status);
        ProviderSnapshot? loaded = await snapshots.GetLatestAsync(account.Key, default);
        Assert.NotNull(loaded);
        Assert.Equal(SnapshotCompleteness.Authoritative, loaded.Completeness);
        UsageMeter meter = Assert.Single(loaded.Meters);
        Assert.Equal(12m, meter.Used);
        Assert.Equal(50m, meter.Limit);
        Assert.Equal(account.Key, (await accounts.GetAsync(account.Key, default))!.Key);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : AiLimits.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
