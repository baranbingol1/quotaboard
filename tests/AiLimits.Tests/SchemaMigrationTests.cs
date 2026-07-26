// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class SchemaMigrationTests
{
    [Fact]
    public async Task CorruptDatabaseIsMovedAsideAndRecreatedFromScratch()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");

        try
        {
            // Write garbage bytes to simulate a corrupt database file.
            await File.WriteAllBytesAsync(path, [0x42, 0x41, 0x44, 0x44, 0x41, 0x54, 0x41]);

            var database = new SqliteDatabase(path);
            await database.InitializeAsync();

            // The corrupt file should have been moved aside.
            Assert.True(File.Exists(path + ".corrupt"));
            // A fresh schema should exist in the new database.
            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(7L, Convert.ToInt64(await command.ExecuteScalarAsync()));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CorruptDatabaseThatCannotBeMovedAsideKeepsItsWalAndShm()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");

        await File.WriteAllBytesAsync(path, [0x42, 0x41, 0x44, 0x44, 0x41, 0x54, 0x41]);
        await File.WriteAllTextAsync(path + "-wal", "committed pages live here");
        await File.WriteAllTextAsync(path + "-shm", "shared memory index");

        // Hold the database file open so the move aside cannot succeed. The
        // sidecars may still hold committed transactions, so discarding them
        // while the database remains in place would destroy real data.
        FileStream held = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var database = new SqliteDatabase(path);
            try
            {
                await database.InitializeAsync();
            }
            catch (SqliteException)
            {
                // Expected: the corrupt file is still in place, so migrations
                // fail. What matters is what did *not* get deleted.
            }

            Assert.False(File.Exists(path + ".corrupt"));
            Assert.True(File.Exists(path + "-wal"), "the WAL must survive a failed move aside");
            Assert.True(File.Exists(path + "-shm"), "the SHM must survive a failed move aside");
        }
        finally
        {
            held.Dispose();
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task CurrentSchemaIncludesProjectAttributionColumns()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");

        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync();

            await using var connection = new SqliteConnection($"Data Source={path}");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT MAX(version) FROM schema_migrations;";
            Assert.Equal(7L, Convert.ToInt64(await command.ExecuteScalarAsync()));

            command.CommandText = "SELECT name FROM pragma_table_info('daily_usage');";
            var columns = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(0));
            Assert.Contains("project_key", columns);
            Assert.Contains("project_path", columns);
            Assert.Contains("repository_root_path", columns);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VersionSevenPurgesWindsurfProviderData()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");

        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync();

            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var seed = connection.CreateCommand();
                seed.CommandText = """
                    DELETE FROM schema_migrations WHERE version = 7;
                    INSERT INTO accounts VALUES('windsurf', 'a', 'Windsurf', NULL, 'cache', 0, 1, NULL);
                    INSERT INTO accounts VALUES('claude', 'b', 'Claude', NULL, 'oauth', 0, 1, NULL);
                    INSERT INTO fetch_attempts VALUES('x', 'windsurf', 'a', 's', '2026-07-01T00:00:00Z', 1, 0, 'ok');
                    """;
                await seed.ExecuteNonQueryAsync();
            }

            await new SqliteDatabase(path).InitializeAsync();

            await using var verify = new SqliteConnection($"Data Source={path}");
            await verify.OpenAsync();
            await using var command = verify.CreateCommand();
            command.CommandText = """
                SELECT
                    (SELECT COUNT(*) FROM accounts WHERE provider_id = 'windsurf'),
                    (SELECT COUNT(*) FROM fetch_attempts WHERE provider_id = 'windsurf'),
                    (SELECT COUNT(*) FROM accounts WHERE provider_id = 'claude'),
                    (SELECT MAX(version) FROM schema_migrations);
                """;
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(1L, reader.GetInt64(2));
            Assert.Equal(7L, reader.GetInt64(3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task VersionFiveResetsUnattributableAggregatesAndScannerStateForReplay()
    {
        var directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "state.db");

        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync();
                await using var command = connection.CreateCommand();
                command.CommandText = """
                    CREATE TABLE schema_migrations(version INTEGER NOT NULL PRIMARY KEY, applied_at TEXT NOT NULL);
                    INSERT INTO schema_migrations VALUES(4, '2026-07-13T00:00:00Z');
                    CREATE TABLE daily_usage(legacy_value INTEGER NOT NULL);
                    INSERT INTO daily_usage VALUES(1);
                    CREATE TABLE scanner_fingerprints(fingerprint TEXT NOT NULL, provider_id TEXT NOT NULL);
                    INSERT INTO scanner_fingerprints VALUES('old', 'codex');
                    CREATE TABLE scanner_cursors(source_id TEXT NOT NULL, provider_id TEXT NOT NULL);
                    INSERT INTO scanner_cursors VALUES('old', 'codex');
                    CREATE TABLE accounts(provider_id TEXT NOT NULL);
                    CREATE TABLE snapshots(provider_id TEXT NOT NULL);
                    CREATE TABLE fetch_attempts(provider_id TEXT NOT NULL);
                    CREATE TABLE alert_state(provider_id TEXT NOT NULL);
                    """;
                await command.ExecuteNonQueryAsync();
            }

            var database = new SqliteDatabase(path);
            await database.InitializeAsync();

            await using var migrated = new SqliteConnection($"Data Source={path}");
            await migrated.OpenAsync();
            await using var verification = migrated.CreateCommand();
            verification.CommandText = """
                SELECT
                    (SELECT MAX(version) FROM schema_migrations),
                    (SELECT COUNT(*) FROM daily_usage),
                    (SELECT COUNT(*) FROM scanner_fingerprints),
                    (SELECT COUNT(*) FROM scanner_cursors);
                """;
            await using var reader = await verification.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(7L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
            Assert.Equal(0L, reader.GetInt64(2));
            Assert.Equal(0L, reader.GetInt64(3));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
