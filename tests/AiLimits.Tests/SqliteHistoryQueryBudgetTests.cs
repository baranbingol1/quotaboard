// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

/// <summary>
/// N+1 budget for snapshot history. Loading N snapshots used to issue
/// 1 + 2N queries (header, then meters and balances per row). History must
/// stay three statements regardless of how many snapshots are in range.
/// </summary>
public sealed class SqliteHistoryQueryBudgetTests
{
    [Fact]
    public async Task GetHistoryAsync_issues_a_constant_number_of_statements()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(temporary.File("history.db"));
        await database.InitializeAsync();
        var accounts = new SqliteAccountRepository(database);
        var snapshots = new SqliteSnapshotRepository(database);
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("codex"), "primary"),
            "Codex",
            null,
            "oauth",
            1,
            true
        );
        await accounts.UpsertAsync(account, default);

        DateTimeOffset start = new(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        const int count = 12;
        for (int i = 0; i < count; i++)
        {
            DateTimeOffset at = start.AddHours(i);
            UsageMeter meter = new(
                new MeterKey($"codex:m{i}"),
                $"Meter {i}",
                MeterScope.Feature,
                MeterUnit.Credits,
                i + 1,
                100,
                i,
                TimeSpan.FromDays(1),
                at.AddDays(1),
                "gpt-5",
                MeterStatus.Healthy,
                new MeterProvenance("codex.oauth", "$.limits", at, true),
                at,
                i == 0
            );
            ProviderSnapshot snapshot = new(
                account.Key,
                [meter],
                [new BalanceMetric("balance", "Balance", 10 + i, MeterUnit.Usd)],
                SnapshotCompleteness.Authoritative,
                at,
                DataConfidence.Exact,
                new Dictionary<string, JsonElement>()
            );
            await snapshots.SaveAsync(snapshot, i + 1, default);
        }

        IReadOnlyList<ProviderSnapshot> history = await snapshots.GetHistoryAsync(
            account.Key,
            start.AddHours(-1),
            default
        );

        Assert.Equal(count, history.Count);
        Assert.Equal(3, snapshots.LastHistoryCommandCount);
        Assert.Equal("Meter 0", history[0].Meters[0].DisplayName);
        Assert.Equal("Meter 11", history[^1].Meters[0].DisplayName);
        Assert.Equal(10m, history[0].Balances[0].Value);
        Assert.Equal(21m, history[^1].Balances[0].Value);
        Assert.All(
            history,
            item =>
            {
                Assert.Single(item.Meters);
                Assert.Single(item.Balances);
            }
        );
    }

    [Fact]
    public async Task GetHistoryAsync_returns_empty_without_child_queries()
    {
        using var temporary = new TemporaryDirectory();
        var database = new SqliteDatabase(temporary.File("empty.db"));
        await database.InitializeAsync();
        var snapshots = new SqliteSnapshotRepository(database);
        var account = new AccountKey(new ProviderId("codex"), "missing");

        IReadOnlyList<ProviderSnapshot> history = await snapshots.GetHistoryAsync(
            account,
            DateTimeOffset.UnixEpoch,
            default
        );

        Assert.Empty(history);
        Assert.Equal(1, snapshots.LastHistoryCommandCount);
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
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
