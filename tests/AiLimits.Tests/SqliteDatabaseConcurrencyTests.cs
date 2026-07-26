// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class SqliteDatabaseConcurrencyTests
{
    [Fact]
    public async Task ConcurrentReadsAndWritesDoNotProduceLockedErrors()
    {
        using var temp = new TempDir();
        var dbPath = Path.Combine(temp.Path, "concurrent.db");
        var db = new SqliteDatabase(dbPath);
        await db.InitializeAsync();

        var writeCount = 20;
        var errors = new List<Exception>();

        // Writer: insert rows into schema_migrations (already exists).
        var writeTasks = Enumerable.Range(0, writeCount).Select(async i =>
        {
            try
            {
                await using var connection = await db.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $"INSERT OR IGNORE INTO schema_migrations(version, applied_at) VALUES({1000 + i}, strftime('%Y-%m-%dT%H:%M:%fZ','now'));";
                await command.ExecuteNonQueryAsync();
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        });

        // Reader: query schema_migrations concurrently.
        var readTasks = Enumerable.Range(0, writeCount).Select(async _ =>
        {
            try
            {
                await using var connection = await db.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT COUNT(*) FROM schema_migrations;";
                await command.ExecuteScalarAsync();
            }
            catch (Exception ex)
            {
                lock (errors) errors.Add(ex);
            }
        });

        await Task.WhenAll(writeTasks.Concat(readTasks));

        Assert.Empty(errors);
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
