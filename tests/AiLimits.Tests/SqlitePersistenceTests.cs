// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class SqlitePersistenceTests
{
    [Fact]
    public async Task DynamicSnapshotRoundTripsWithProvenanceAndBalances()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(temporary.File("state.db"));
        await database.InitializeAsync();
        var accounts = new SqliteAccountRepository(database);
        var snapshots = new SqliteSnapshotRepository(database);
        var account = new ProviderAccount(new AccountKey(new ProviderId("future-provider"), "a"),
            "Future account", null, "fixture", 3, true);
        await accounts.UpsertAsync(account, default);
        var at = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        var meter = new UsageMeter(new MeterKey("future:m1"), "Never Seen Before", MeterScope.Feature,
            MeterUnit.Credits, 4, 10, 40, TimeSpan.FromDays(3), at.AddDays(1), "new-model",
            MeterStatus.Healthy, new MeterProvenance("future.fixture", "$.limits.new", at, true), at, true);
        var snapshot = new ProviderSnapshot(account.Key, [meter],
            [new BalanceMetric("balance", "Balance", 12.5m, MeterUnit.Usd)],
            SnapshotCompleteness.Authoritative, at, DataConfidence.Exact,
            new Dictionary<string, JsonElement> { ["source"] = JsonSerializer.SerializeToElement("fixture") });
        await snapshots.SaveAsync(snapshot, 7, default);

        var loaded = await snapshots.GetLatestAsync(account.Key, default);
        Assert.NotNull(loaded);
        Assert.Equal("Never Seen Before", Assert.Single(loaded.Meters).DisplayName);
        Assert.True(loaded.Meters[0].IsNew);
        Assert.Equal(12.5m, Assert.Single(loaded.Balances).Value);
        Assert.Equal("fixture", loaded.Extensions["source"].GetString());
    }

    [Fact]
    public async Task DuplicateSourceEventIsAggregatedOnlyOnce()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(temporary.File("usage.db"));
        await database.InitializeAsync();
        var repository = new SqliteUsageAggregateRepository(database);
        var account = new AccountKey(new ProviderId("codex"), "a");
        var usage = new TokenUsageEvent(account, new ServiceProviderId("codex"), "gpt-5",
            new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero), 100, 50, 20, 0, 10, "same-event");
        await repository.AddEventsAsync([usage, usage], default);
        await repository.AddEventsAsync([usage], default);
        var rows = await repository.QueryAsync(new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 13), null, default);
        var row = Assert.Single(rows);
        Assert.Equal(100, row.InputTokens);
        Assert.Equal(50, row.OutputTokens);
        Assert.Equal(20, row.CacheReadTokens);
        Assert.True(row.Project.IsUnknown);
    }

    [Fact]
    public async Task ProjectDimensionRoundTripsAndStillReconcilesToOverallTotals()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(temporary.File("projects.db"));
        await database.InitializeAsync();
        var repository = new SqliteUsageAggregateRepository(database);
        var account = new AccountKey(new ProviderId("codex"), "a");
        var repositoryRoot = Path.Combine(temporary.Path, "ortak-depo");
        var projectOne = new ProjectIdentity(
            "project:one",
            Path.Combine(temporary.Path, "çalışma-ağacı", "bir"),
            repositoryRoot);
        var projectTwo = new ProjectIdentity(
            "project:two",
            Path.Combine(temporary.Path, "çalışma-ağacı", "iki"),
            repositoryRoot);
        var occurredAt = new DateTimeOffset(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
        await repository.AddEventsAsync(
        [
            new TokenUsageEvent(account, new ServiceProviderId("codex"), "gpt-5", occurredAt,
                100, 50, 20, 0, 10, "event-one", projectOne),
            new TokenUsageEvent(account, new ServiceProviderId("codex"), "gpt-5", occurredAt,
                200, 75, 30, 0, 15, "event-two", projectTwo)
        ], default);

        var rows = await repository.QueryAsync(
            new DateOnly(2026, 7, 13), new DateOnly(2026, 7, 13), null, default);

        Assert.Equal(2, rows.Count);
        Assert.Equal(500, rows.Sum(row =>
            row.InputTokens + row.OutputTokens + row.CacheReadTokens +
            row.CacheWriteTokens + row.ReasoningTokens));
        Assert.Equal(new[] { "project:one", "project:two" },
            rows.Select(row => row.Project.ProjectKey).Order(StringComparer.Ordinal).ToArray());
        Assert.All(rows, row => Assert.Equal(repositoryRoot, row.Project.RepositoryRootPath));
        Assert.Contains(rows, row => row.Project.ProjectPath.Contains("çalışma-ağacı", StringComparison.Ordinal));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
