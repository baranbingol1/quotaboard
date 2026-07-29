// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class SqliteRetentionTests
{
    [Fact]
    public async Task PruneAgesOutBookkeepingButKeepsEachAccountsLatestSnapshot()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = $$"""
                    INSERT INTO accounts VALUES('claude', 'a', 'Claude', NULL, 'oauth', 0, 1, NULL);
                    -- Both snapshots are far past the 90-day window; only the
                    -- newest per account may survive.
                    INSERT INTO snapshots(provider_id, account_id, completeness, observed_at, confidence, generation, extensions_json)
                    VALUES ('claude', 'a', 0, '{{Iso(now.AddDays(-300))}}', 0, 1, '{}'),
                           ('claude', 'a', 0, '{{Iso(now.AddDays(-200))}}', 0, 2, '{}');
                    INSERT INTO fetch_attempts VALUES('old', 'claude', 'a', 's', '{{Iso(now.AddDays(-40))}}', 1, 0, 'ok');
                    INSERT INTO fetch_attempts VALUES('new', 'claude', 'a', 's', '{{Iso(now.AddDays(-1))}}', 1, 0, 'ok');
                    INSERT INTO scanner_fingerprints VALUES('old', 'claude', 'a', 's', '{{Iso(now.AddDays(-90))}}');
                    INSERT INTO scanner_fingerprints VALUES('new', 'claude', 'a', 's', '{{Iso(now.AddDays(-10))}}');
                    INSERT INTO alert_state VALUES('claude', 'a', 'm', 't', 'cycle-old', '{{Iso(now.AddDays(-90))}}');
                    INSERT INTO alert_state VALUES('claude', 'a', 'm', 't', 'cycle-new', '{{Iso(now.AddDays(-1))}}');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            await new SqliteRetention(database).PruneAsync(now);

            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM snapshots),
                    (SELECT MAX(generation) FROM snapshots),
                    (SELECT COUNT(*) FROM fetch_attempts),
                    (SELECT id FROM fetch_attempts),
                    (SELECT COUNT(*) FROM scanner_fingerprints),
                    (SELECT fingerprint FROM scanner_fingerprints),
                    (SELECT COUNT(*) FROM alert_state),
                    (SELECT reset_cycle FROM alert_state);
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(1L, reader.GetInt64(0));
            Assert.Equal(2L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal("new", reader.GetString(3));
            Assert.Equal(1L, reader.GetInt64(4));
            Assert.Equal("new", reader.GetString(5));
            Assert.Equal(1L, reader.GetInt64(6));
            Assert.Equal("cycle-new", reader.GetString(7));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task PruneKeepsEachAccountsNewestAttemptEvenWhenItIsPastTheCutoff()
    {
        // A persistent failure must not age into a generic "Cached" label: the
        // newest attempt row per account survives regardless of age.
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");
        var now = new DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero);

        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = $$"""
                    -- Every attempt for 'a' is past the cutoff; the newest still survives.
                    INSERT INTO fetch_attempts VALUES('a-oldest', 'claude', 'a', 's', '{{Iso(now.AddDays(-45))}}', 1, 5, 'net');
                    INSERT INTO fetch_attempts VALUES('a-newest', 'claude', 'a', 's', '{{Iso(now.AddDays(-40))}}', 1, 5, 'net');
                    -- 'b' has a fresh attempt; its old ones prune as before.
                    INSERT INTO fetch_attempts VALUES('b-old', 'claude', 'b', 's', '{{Iso(now.AddDays(-40))}}', 1, 5, 'net');
                    INSERT INTO fetch_attempts VALUES('b-new', 'claude', 'b', 's', '{{Iso(now.AddDays(-1))}}', 1, 0, 'ok');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            await new SqliteRetention(database).PruneAsync(now);

            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = "SELECT id FROM fetch_attempts ORDER BY id;";
            await using var reader = await command.ExecuteReaderAsync();
            var survivors = new List<string>();
            while (await reader.ReadAsync())
            {
                survivors.Add(reader.GetString(0));
            }
            Assert.Equal(["a-newest", "b-new"], survivors);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static string Iso(DateTimeOffset value) => value.ToString("O");
}
