// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;

namespace AiLimits.IntegrationTests;

/// <summary>
/// Cross-repository SQLite flow: accounts, snapshots, usage aggregates, and
/// retention share one on-disk database. These tests open a real file, not
/// an in-memory fake, so schema, foreign keys, and WAL behave as they do
/// in the shipped app.
/// </summary>
public sealed class SqlitePersistenceIntegrationTests
{
    [Fact]
    public async Task Account_snapshot_and_usage_round_trip_on_one_database()
    {
        using var temporary = new TemporaryDatabase();
        var database = new SqliteDatabase(temporary.PathToFile);
        await database.InitializeAsync();

        var accounts = new SqliteAccountRepository(database);
        var snapshots = new SqliteSnapshotRepository(database);
        var usage = new SqliteUsageAggregateRepository(database);
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("codex"), "primary"),
            "Codex",
            "user@example.com",
            "oauth",
            3,
            true
        );
        DateTimeOffset observed = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

        await accounts.UpsertAsync(account, default);
        UsageMeter meter = new(
            new MeterKey("codex:week"),
            "Weekly",
            MeterScope.Account,
            MeterUnit.Credits,
            12,
            50,
            24,
            TimeSpan.FromDays(7),
            observed.AddDays(2),
            "gpt-5",
            MeterStatus.Healthy,
            new MeterProvenance("codex.oauth", "$.rate_limit", observed, true),
            observed
        );
        await snapshots.SaveAsync(
            new ProviderSnapshot(
                account.Key,
                [meter],
                [new BalanceMetric("credits", "Credits", 38, MeterUnit.Credits)],
                SnapshotCompleteness.Authoritative,
                observed,
                DataConfidence.Exact,
                new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("live") }
            ),
            generation: 1,
            default
        );
        await usage.AddEventsAsync(
            [
                new TokenUsageEvent(
                    account.Key,
                    new ServiceProviderId("codex"),
                    "gpt-5",
                    observed,
                    100,
                    40,
                    10,
                    0,
                    5,
                    "evt-1",
                    new ProjectIdentity("project:demo", @"C:\work\demo", @"C:\work\demo")
                ),
            ],
            default
        );

        ProviderAccount? loadedAccount = await accounts.GetAsync(account.Key, default);
        ProviderSnapshot? latest = await snapshots.GetLatestAsync(account.Key, default);
        IReadOnlyList<ProviderSnapshot> history = await snapshots.GetHistoryAsync(
            account.Key,
            observed.AddHours(-1),
            default
        );
        DateOnly usageDay = DateOnly.FromDateTime(observed.ToLocalTime().DateTime);
        IReadOnlyList<DailyUsageAggregate> rows = await usage.QueryAsync(usageDay, usageDay, [account.Key], default);

        Assert.NotNull(loadedAccount);
        Assert.Equal("user@example.com", loadedAccount.Login);
        Assert.NotNull(latest);
        Assert.Equal("Weekly", Assert.Single(latest.Meters).DisplayName);
        Assert.Equal(38m, Assert.Single(latest.Balances).Value);
        Assert.Equal("live", latest.Extensions["source"].GetString());
        Assert.Equal(latest.ObservedAt, Assert.Single(history).ObservedAt);
        DailyUsageAggregate row = Assert.Single(rows);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal("project:demo", row.Project.ProjectKey);
    }

    [Fact]
    public async Task Retention_keeps_the_newest_snapshot_after_aging_out_history()
    {
        using var temporary = new TemporaryDatabase();
        var database = new SqliteDatabase(temporary.PathToFile);
        await database.InitializeAsync();
        var accounts = new SqliteAccountRepository(database);
        var snapshots = new SqliteSnapshotRepository(database);
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("claude"), "a"),
            "Claude",
            null,
            "oauth",
            1,
            true
        );
        await accounts.UpsertAsync(account, default);

        DateTimeOffset now = new(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);
        await snapshots.SaveAsync(EmptySnapshot(account.Key, now.AddDays(-200)), 1, default);
        await snapshots.SaveAsync(EmptySnapshot(account.Key, now.AddDays(-1)), 2, default);

        await new SqliteRetention(database).PruneAsync(now);

        IReadOnlyList<ProviderSnapshot> remaining = await snapshots.GetHistoryAsync(
            account.Key,
            DateTimeOffset.UnixEpoch,
            default
        );
        Assert.Single(remaining);
        Assert.Equal(now.AddDays(-1), remaining[0].ObservedAt);
    }

    private static ProviderSnapshot EmptySnapshot(AccountKey account, DateTimeOffset observedAt) =>
        new(
            account,
            [],
            [],
            SnapshotCompleteness.Partial,
            observedAt,
            DataConfidence.Low,
            new Dictionary<string, JsonElement>()
        );
}
