// SPDX-License-Identifier: Apache-2.0
using System.Data.Common;
using Microsoft.Data.Sqlite;

namespace AiLimits.Infrastructure.Persistence;

public sealed class SqliteDatabase
{
    private readonly string _connectionString;

    private readonly string _databasePath;

    public SqliteDatabase(string databasePath)
    {
        //IL_0037: Unknown result type (might be due to invalid IL or missing references)
        //IL_003c: Unknown result type (might be due to invalid IL or missing references)
        //IL_0044: Unknown result type (might be due to invalid IL or missing references)
        //IL_004c: Unknown result type (might be due to invalid IL or missing references)
        //IL_0054: Unknown result type (might be due to invalid IL or missing references)
        //IL_0061: Expected O, but got Unknown
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath, "databasePath");
        string directoryName = Path.GetDirectoryName(Path.GetFullPath(databasePath));
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            Directory.CreateDirectory(directoryName);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString();
        _databasePath = databasePath;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default(CancellationToken))
    {
        await VerifyIntegrityOrRecoverAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ExecuteAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA synchronous = NORMAL;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA busy_timeout = 5000;", cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(
                    connection,
                    "CREATE TABLE IF NOT EXISTS schema_migrations (\n    version INTEGER NOT NULL PRIMARY KEY,\n    applied_at TEXT NOT NULL\n);",
                    cancellationToken
                )
                .ConfigureAwait(false);
            long version = await GetVersionAsync(connection, cancellationToken).ConfigureAwait(false);
            if (version < 1)
            {
                await ApplyVersionOneAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 1;
            }
            if (version < 2)
            {
                await ApplyVersionTwoAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 2;
            }
            if (version < 3)
            {
                await ApplyVersionThreeAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 3;
            }
            if (version < 4)
            {
                await ApplyVersionFourAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 4;
            }
            if (version < 5)
            {
                await ApplyVersionFiveAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 5;
            }
            if (version < 6)
            {
                await ApplyVersionSixAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 6;
            }
            if (version < 7)
            {
                await ApplyVersionSevenAsync(connection, cancellationToken).ConfigureAwait(false);
                version = 7;
            }
            if (version < 8)
            {
                await ApplyVersionEightAsync(connection, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = new SqliteConnection(_connectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON; PRAGMA busy_timeout = 5000;", cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            // The caller never receives the connection on this path, so nobody
            // else can dispose it. Pooling is on, so a leak here holds a pool
            // slot and its file handle for the life of the process.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return connection;
    }

    /// <summary>
    /// Runs PRAGMA quick_check before migrations. On corruption, moves the
    /// database file aside (”.corrupt“ suffix) so a fresh schema is created
    /// from scratch; aggregates rebuild from local histories on the next scan.
    /// </summary>
    private async Task VerifyIntegrityOrRecoverAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_databasePath))
        {
            return;
        }
        bool healthy = true;
        SqliteConnection? probe = null;
        try
        {
            probe = new SqliteConnection(_connectionString);
            await probe.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using DbCommand command = probe.CreateCommand();
            command.CommandText = "PRAGMA quick_check;";
            object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            healthy = result is string text && string.Equals(text, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (SqliteException)
        {
            healthy = false;
        }
        finally
        {
            if (probe != null)
            {
                await probe.DisposeAsync().ConfigureAwait(false);
            }
        }
        if (!healthy)
        {
            SqliteConnection.ClearAllPools();
            string corruptPath = _databasePath + ".corrupt";
            bool movedAside = false;
            try
            {
                if (File.Exists(corruptPath))
                {
                    File.Delete(corruptPath);
                }
                File.Move(_databasePath, corruptPath);
                movedAside = true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // If the move fails (e.g. WAL/shm sidecars are locked), continue
                // with the existing file; migrations will either succeed or throw.
            }
            if (movedAside)
            {
                // Only now are the sidecars orphaned. Deleting them while the
                // database is still in place would discard the committed pages
                // a WAL is holding — turning a database quick_check merely
                // distrusted into one that really is unrecoverable.
                foreach (string sidecar in new[] { _databasePath + "-wal", _databasePath + "-shm" })
                {
                    try
                    {
                        if (File.Exists(sidecar))
                        {
                            File.Delete(sidecar);
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                }
            }
        }
    }

    private static async Task<long> GetVersionAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        long result;
        try
        {
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
            result = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        finally
        {
            if (command != null)
            {
                await command.DisposeAsync();
            }
        }
        return result;
    }

    private static async Task ApplyVersionOneAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText =
                "CREATE TABLE accounts (\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    display_name TEXT NOT NULL,\n    login TEXT NULL,\n    auth_source TEXT NOT NULL,\n    configuration_revision INTEGER NOT NULL,\n    is_connected INTEGER NOT NULL,\n    last_successful_refresh_at TEXT NULL,\n    PRIMARY KEY (provider_id, account_id)\n);\n\nCREATE TABLE snapshots (\n    id INTEGER PRIMARY KEY AUTOINCREMENT,\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    completeness INTEGER NOT NULL,\n    observed_at TEXT NOT NULL,\n    confidence INTEGER NOT NULL,\n    generation INTEGER NOT NULL,\n    extensions_json TEXT NOT NULL,\n    FOREIGN KEY (provider_id, account_id) REFERENCES accounts(provider_id, account_id) ON DELETE CASCADE\n);\nCREATE INDEX ix_snapshots_account_observed\n    ON snapshots(provider_id, account_id, observed_at DESC);\n\nCREATE TABLE snapshot_meters (\n    snapshot_id INTEGER NOT NULL,\n    meter_key TEXT NOT NULL,\n    display_name TEXT NOT NULL,\n    scope INTEGER NOT NULL,\n    unit INTEGER NOT NULL,\n    used TEXT NULL,\n    meter_limit TEXT NULL,\n    used_percent REAL NULL,\n    window_ticks INTEGER NULL,\n    resets_at TEXT NULL,\n    raw_model_id TEXT NULL,\n    status INTEGER NOT NULL,\n    strategy_id TEXT NOT NULL,\n    source_path TEXT NOT NULL,\n    acquired_at TEXT NOT NULL,\n    is_authoritative INTEGER NOT NULL,\n    attempt_id TEXT NULL,\n    first_observed_at TEXT NULL,\n    is_new INTEGER NOT NULL,\n    PRIMARY KEY (snapshot_id, meter_key),\n    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE\n);\n\nCREATE TABLE snapshot_balances (\n    snapshot_id INTEGER NOT NULL,\n    balance_key TEXT NOT NULL,\n    display_name TEXT NOT NULL,\n    value TEXT NULL,\n    unit INTEGER NOT NULL,\n    formatted_value TEXT NULL,\n    PRIMARY KEY (snapshot_id, balance_key),\n    FOREIGN KEY (snapshot_id) REFERENCES snapshots(id) ON DELETE CASCADE\n);\n\nCREATE TABLE fetch_attempts (\n    id TEXT NOT NULL PRIMARY KEY,\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    strategy_id TEXT NOT NULL,\n    started_at TEXT NOT NULL,\n    duration_ms INTEGER NOT NULL,\n    failure_kind INTEGER NOT NULL,\n    safe_message TEXT NOT NULL\n);\nCREATE INDEX ix_fetch_attempts_account_started\n    ON fetch_attempts(provider_id, account_id, started_at DESC);\n\nCREATE TABLE daily_usage (\n    day TEXT NOT NULL,\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    service_id TEXT NOT NULL,\n    raw_model_id TEXT NOT NULL,\n    pricing_provider_id TEXT NOT NULL DEFAULT '',\n    canonical_model_id TEXT NOT NULL DEFAULT '',\n    resolution_confidence INTEGER NOT NULL DEFAULT 0,\n    input_tokens INTEGER NOT NULL,\n    output_tokens INTEGER NOT NULL,\n    cache_read_tokens INTEGER NOT NULL,\n    cache_write_tokens INTEGER NOT NULL,\n    reasoning_tokens INTEGER NOT NULL,\n    reported_service_cost_usd TEXT NULL,\n    PRIMARY KEY (\n        day, provider_id, account_id, service_id, raw_model_id,\n        pricing_provider_id, canonical_model_id\n    )\n);\n\nCREATE TABLE scanner_fingerprints (\n    fingerprint TEXT NOT NULL PRIMARY KEY,\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    source_id TEXT NOT NULL,\n    observed_at TEXT NOT NULL\n);\n\nCREATE TABLE scanner_cursors (\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    source_id TEXT NOT NULL,\n    position TEXT NULL,\n    last_observed_at TEXT NULL,\n    fingerprint TEXT NULL,\n    PRIMARY KEY (provider_id, account_id, source_id)\n);\n\nCREATE TABLE pricing_catalogs (\n    catalog_hash TEXT NOT NULL PRIMARY KEY,\n    fetched_at TEXT NOT NULL,\n    etag TEXT NULL,\n    body_json TEXT NOT NULL,\n    is_current INTEGER NOT NULL\n);\n\nCREATE TABLE model_resolutions (\n    service_id TEXT NOT NULL,\n    normalized_raw_model_id TEXT NOT NULL,\n    pricing_provider_id TEXT NOT NULL,\n    canonical_model_id TEXT NOT NULL,\n    confidence INTEGER NOT NULL,\n    PRIMARY KEY (service_id, normalized_raw_model_id)\n);\n\nCREATE TABLE alert_state (\n    provider_id TEXT NOT NULL,\n    account_id TEXT NOT NULL,\n    meter_key TEXT NOT NULL,\n    threshold_key TEXT NOT NULL,\n    reset_cycle TEXT NOT NULL,\n    notified_at TEXT NOT NULL,\n    PRIMARY KEY (provider_id, account_id, meter_key, threshold_key, reset_cycle)\n);\n\nINSERT INTO schema_migrations(version, applied_at)\nVALUES (1, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));";
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (command != null)
            {
                await command.DisposeAsync();
            }
        }
    }

    private static async Task ApplyVersionTwoAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM daily_usage WHERE provider_id = 'opencode';
                DELETE FROM scanner_fingerprints WHERE provider_id = 'opencode';
                DELETE FROM scanner_cursors WHERE provider_id = 'opencode';
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (2, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    // v3: OpenCode OpenAI usage is re-attributed as openai-oauth / openai-api and
    // day keys move from UTC to local dates, so all scanned usage, fingerprints,
    // and cursors are dropped; every source replays from local history on next scan.
    private static async Task ApplyVersionThreeAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM daily_usage;
                DELETE FROM scanner_fingerprints;
                DELETE FROM scanner_cursors;
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (3, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    // v4: older builds published OpenCode Go budget meters from local history;
    // the adapter intentionally has no limit strategy anymore, so those fossil
    // snapshots would otherwise stay "cached" forever. Purge them.
    private static async Task ApplyVersionFourAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM snapshots WHERE provider_id = 'opencode';
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (4, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    // v5: usage aggregates gain a normalized project/worktree dimension. Raw
    // events were intentionally never retained, so existing aggregate rows
    // cannot be attributed safely. Reset scanner state and replay provider-owned
    // local history on the next refresh rather than guessing or double-counting.
    private static async Task ApplyVersionFiveAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DROP TABLE daily_usage;
                CREATE TABLE daily_usage (
                    day TEXT NOT NULL,
                    provider_id TEXT NOT NULL,
                    account_id TEXT NOT NULL,
                    service_id TEXT NOT NULL,
                    raw_model_id TEXT NOT NULL,
                    pricing_provider_id TEXT NOT NULL DEFAULT '',
                    canonical_model_id TEXT NOT NULL DEFAULT '',
                    resolution_confidence INTEGER NOT NULL DEFAULT 0,
                    input_tokens INTEGER NOT NULL,
                    output_tokens INTEGER NOT NULL,
                    cache_read_tokens INTEGER NOT NULL,
                    cache_write_tokens INTEGER NOT NULL,
                    reasoning_tokens INTEGER NOT NULL,
                    reported_service_cost_usd TEXT NULL,
                    project_key TEXT NOT NULL,
                    project_path TEXT NOT NULL,
                    repository_root_path TEXT NULL,
                    PRIMARY KEY (
                        day, provider_id, account_id, service_id, raw_model_id,
                        pricing_provider_id, canonical_model_id, project_key
                    )
                );
                CREATE INDEX ix_daily_usage_project_day ON daily_usage(project_key, day);
                DELETE FROM scanner_fingerprints;
                DELETE FROM scanner_cursors;
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (5, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    // v6: Grok support was removed. Purge cached provider data so an account or
    // snapshot created by an earlier build cannot remain as invisible dead state.
    private static async Task ApplyVersionSixAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM snapshots WHERE provider_id = 'grok';
                DELETE FROM accounts WHERE provider_id = 'grok';
                DELETE FROM fetch_attempts WHERE provider_id = 'grok';
                DELETE FROM daily_usage WHERE provider_id = 'grok';
                DELETE FROM scanner_fingerprints WHERE provider_id = 'grok';
                DELETE FROM scanner_cursors WHERE provider_id = 'grok';
                DELETE FROM alert_state WHERE provider_id = 'grok';
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (6, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    // v7: Windsurf support was removed. Purge cached provider data so an account or
    // snapshot created by an earlier build cannot remain as invisible dead state.
    private static async Task ApplyVersionSevenAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                DELETE FROM snapshots WHERE provider_id = 'windsurf';
                DELETE FROM accounts WHERE provider_id = 'windsurf';
                DELETE FROM fetch_attempts WHERE provider_id = 'windsurf';
                DELETE FROM daily_usage WHERE provider_id = 'windsurf';
                DELETE FROM scanner_fingerprints WHERE provider_id = 'windsurf';
                DELETE FROM scanner_cursors WHERE provider_id = 'windsurf';
                DELETE FROM alert_state WHERE provider_id = 'windsurf';
                INSERT INTO schema_migrations(version, applied_at)
                VALUES (7, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
                """;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await command.DisposeAsync();
        }
    }

    // v8: snapshots are account-configuration scoped. Existing rows receive
    // revision zero and are therefore excluded from current positive revisions.
    private static async Task ApplyVersionEightAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using DbTransaction transaction = await connection
            .BeginTransactionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = """
            ALTER TABLE snapshots ADD COLUMN configuration_revision INTEGER NOT NULL DEFAULT 0;
            CREATE INDEX ix_snapshots_account_revision_observed
                ON snapshots(provider_id, account_id, configuration_revision, observed_at DESC);
            INSERT INTO schema_migrations(version, applied_at)
            VALUES (8, strftime('%Y-%m-%dT%H:%M:%fZ', 'now'));
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (command != null)
            {
                await command.DisposeAsync();
            }
        }
    }
}
