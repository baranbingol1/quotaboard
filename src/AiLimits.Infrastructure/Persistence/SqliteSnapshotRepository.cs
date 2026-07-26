// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Globalization;
using System.Text.Json;

namespace AiLimits.Infrastructure.Persistence;

public sealed class SqliteSnapshotRepository(SqliteDatabase database) : ISnapshotRepository
{
    private sealed record SnapshotHeader(long Id, SnapshotCompleteness Completeness, DateTimeOffset ObservedAt, DataConfidence Confidence, string ExtensionsJson);

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    public async Task<ProviderSnapshot?> GetLatestAsync(AccountKey account, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        ProviderSnapshot result;
        try
        {
            SqliteCommand command = connection.CreateCommand();
            ProviderSnapshot providerSnapshot2;
            try
            {
                command.CommandText = "SELECT id, completeness, observed_at, confidence, extensions_json\nFROM snapshots\nWHERE provider_id = $provider AND account_id = $account\nORDER BY observed_at DESC, id DESC\nLIMIT 1;";
                command.Parameters.AddWithValue("$provider", account.Provider.Value);
                command.Parameters.AddWithValue("$account", account.Value);
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                ProviderSnapshot providerSnapshot;
                try
                {
                    if (!(await reader.ReadAsync(cancellationToken).ConfigureAwait(false)))
                    {
                        providerSnapshot = null;
                    }
                    else
                    {
                        SnapshotHeader header = ReadHeader(reader);
                        await reader.DisposeAsync().ConfigureAwait(false);
                        providerSnapshot = await ReadSnapshotAsync(connection, account, header, cancellationToken).ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (reader != null)
                    {
                        await reader.DisposeAsync();
                    }
                }
                providerSnapshot2 = providerSnapshot;
            }
            finally
            {
                if (command != null)
                {
                    await command.DisposeAsync();
                }
            }
            result = providerSnapshot2;
        }
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
        }
        return result;
    }

    public async Task SaveAsync(ProviderSnapshot snapshot, long generation, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            SqliteCommand snapshotCommand = connection.CreateCommand();
            try
            {
                snapshotCommand.Transaction = (SqliteTransaction)transaction;
                snapshotCommand.CommandText = "INSERT INTO snapshots(\n    provider_id, account_id, completeness, observed_at, confidence, generation, extensions_json)\nVALUES($provider, $account, $completeness, $observed, $confidence, $generation, $extensions);\nSELECT last_insert_rowid();";
                snapshotCommand.Parameters.AddWithValue("$provider", snapshot.Account.Provider.Value);
                snapshotCommand.Parameters.AddWithValue("$account", snapshot.Account.Value);
                snapshotCommand.Parameters.AddWithValue("$completeness", (int)snapshot.Completeness);
                snapshotCommand.Parameters.AddWithValue("$observed", Format(snapshot.ObservedAt));
                snapshotCommand.Parameters.AddWithValue("$confidence", (int)snapshot.Confidence);
                snapshotCommand.Parameters.AddWithValue("$generation", generation);
                snapshotCommand.Parameters.AddWithValue("$extensions", JsonSerializer.Serialize(snapshot.Extensions, JsonOptions));
                long snapshotId = Convert.ToInt64(await snapshotCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
                using (IEnumerator<UsageMeter> enumerator = snapshot.Meters.GetEnumerator())
                {
                    while (enumerator.MoveNext())
                    {
                        await InsertMeterAsync(meter: enumerator.Current, connection: connection, transaction: (SqliteTransaction)transaction, snapshotId: snapshotId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                using (IEnumerator<BalanceMetric> enumerator2 = snapshot.Balances.GetEnumerator())
                {
                    while (enumerator2.MoveNext())
                    {
                        await InsertBalanceAsync(balance: enumerator2.Current, connection: connection, transaction: (SqliteTransaction)transaction, snapshotId: snapshotId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    }
                }
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (snapshotCommand != null)
                {
                    await snapshotCommand.DisposeAsync();
                }
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

    public async Task<IReadOnlyList<ProviderSnapshot>> GetHistoryAsync(AccountKey account, DateTimeOffset from, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderSnapshot> result;
        try
        {
            SqliteCommand command = connection.CreateCommand();
            IReadOnlyList<ProviderSnapshot> readOnlyList;
            try
            {
                command.CommandText = "SELECT id, completeness, observed_at, confidence, extensions_json\nFROM snapshots\nWHERE provider_id = $provider AND account_id = $account AND observed_at >= $from\nORDER BY observed_at;";
                command.Parameters.AddWithValue("$provider", account.Provider.Value);
                command.Parameters.AddWithValue("$account", account.Value);
                command.Parameters.AddWithValue("$from", Format(from));
                List<SnapshotHeader> headers = new List<SnapshotHeader>();
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        headers.Add(ReadHeader(reader));
                    }
                }
                finally
                {
                    if (reader != null)
                    {
                        await reader.DisposeAsync();
                    }
                }
                List<ProviderSnapshot> snapshots = new List<ProviderSnapshot>(headers.Count);
                foreach (SnapshotHeader header in headers)
                {
                    List<ProviderSnapshot> list = snapshots;
                    list.Add(await ReadSnapshotAsync(connection, account, header, cancellationToken).ConfigureAwait(false));
                }
                readOnlyList = snapshots;
            }
            finally
            {
                if (command != null)
                {
                    await command.DisposeAsync();
                }
            }
            result = readOnlyList;
        }
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
        }
        return result;
    }

    public async Task RecordAttemptAsync(FetchAttempt attempt, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteCommand command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT OR REPLACE INTO fetch_attempts(\n    id, provider_id, account_id, strategy_id, started_at, duration_ms, failure_kind, safe_message)\nVALUES($id, $provider, $account, $strategy, $started, $duration, $failure, $message);";
                command.Parameters.AddWithValue("$id", attempt.Id);
                command.Parameters.AddWithValue("$provider", attempt.Account.Provider.Value);
                command.Parameters.AddWithValue("$account", attempt.Account.Value);
                command.Parameters.AddWithValue("$strategy", attempt.StrategyId);
                command.Parameters.AddWithValue("$started", Format(attempt.StartedAt));
                command.Parameters.AddWithValue("$duration", (long)attempt.Duration.TotalMilliseconds);
                command.Parameters.AddWithValue("$failure", (int)attempt.FailureKind);
                command.Parameters.AddWithValue("$message", attempt.SafeMessage);
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
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    private static async Task<ProviderSnapshot> ReadSnapshotAsync(SqliteConnection connection, AccountKey account, SnapshotHeader header, CancellationToken cancellationToken)
    {
        List<UsageMeter> meters = new List<UsageMeter>();
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.CommandText = "SELECT meter_key, display_name, scope, unit, used, meter_limit, used_percent,\n       window_ticks, resets_at, raw_model_id, status, strategy_id, source_path,\n       acquired_at, is_authoritative, attempt_id, first_observed_at, is_new\nFROM snapshot_meters WHERE snapshot_id = $snapshot ORDER BY rowid;";
            command.Parameters.AddWithValue("$snapshot", header.Id);
            SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    meters.Add(new UsageMeter(new MeterKey(reader.GetString(0)), reader.GetString(1), (MeterScope)reader.GetInt32(2), (MeterUnit)reader.GetInt32(3), ParseDecimal(reader, 4), ParseDecimal(reader, 5), reader.IsDBNull(6) ? null : reader.GetDouble(6), reader.IsDBNull(7) ? null : TimeSpan.FromTicks(reader.GetInt64(7)), ParseDate(reader, 8), reader.IsDBNull(9) ? null : reader.GetString(9), (MeterStatus)reader.GetInt32(10), new MeterProvenance(reader.GetString(11), reader.GetString(12), ParseDate(reader, 13).Value, reader.GetInt64(14) != 0, reader.IsDBNull(15) ? null : reader.GetString(15)), ParseDate(reader, 16), reader.GetInt64(17) != 0));
                }
            }
            finally
            {
                if (reader != null)
                {
                    await reader.DisposeAsync();
                }
            }
        }
        finally
        {
            if (command != null)
            {
                await command.DisposeAsync();
            }
        }
        List<BalanceMetric> balances = new List<BalanceMetric>();
        SqliteCommand command2 = connection.CreateCommand();
        try
        {
            command2.CommandText = "SELECT balance_key, display_name, value, unit, formatted_value\nFROM snapshot_balances WHERE snapshot_id = $snapshot ORDER BY rowid;";
            command2.Parameters.AddWithValue("$snapshot", header.Id);
            SqliteDataReader reader2 = await command2.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                while (await reader2.ReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    balances.Add(new BalanceMetric(reader2.GetString(0), reader2.GetString(1), ParseDecimal(reader2, 2), (MeterUnit)reader2.GetInt32(3), reader2.IsDBNull(4) ? null : reader2.GetString(4)));
                }
            }
            finally
            {
                if (reader2 != null)
                {
                    await reader2.DisposeAsync();
                }
            }
        }
        finally
        {
            if (command2 != null)
            {
                await command2.DisposeAsync();
            }
        }
        return new ProviderSnapshot(Extensions: JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(header.ExtensionsJson, JsonOptions) ?? new Dictionary<string, JsonElement>(), Account: account, Meters: meters, Balances: balances, Completeness: header.Completeness, ObservedAt: header.ObservedAt, Confidence: header.Confidence);
    }

    private static async Task InsertMeterAsync(SqliteConnection connection, SqliteTransaction transaction, long snapshotId, UsageMeter meter, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO snapshot_meters(\n    snapshot_id, meter_key, display_name, scope, unit, used, meter_limit, used_percent,\n    window_ticks, resets_at, raw_model_id, status, strategy_id, source_path, acquired_at,\n    is_authoritative, attempt_id, first_observed_at, is_new)\nVALUES($snapshot, $key, $display, $scope, $unit, $used, $limit, $percent,\n    $window, $resets, $model, $status, $strategy, $path, $acquired,\n    $authoritative, $attempt, $firstObserved, $isNew);";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$key", meter.Key.Value);
            command.Parameters.AddWithValue("$display", meter.DisplayName);
            command.Parameters.AddWithValue("$scope", (int)meter.Scope);
            command.Parameters.AddWithValue("$unit", (int)meter.Unit);
            command.Parameters.AddWithValue("$used", Format(meter.Used));
            command.Parameters.AddWithValue("$limit", Format(meter.Limit));
            command.Parameters.AddWithValue("$percent", (object?)meter.UsedPercent ?? DBNull.Value);
            command.Parameters.AddWithValue("$window", (object?)meter.WindowDuration?.Ticks ?? DBNull.Value);
            command.Parameters.AddWithValue("$resets", meter.ResetsAt.HasValue ? Format(meter.ResetsAt.Value) : DBNull.Value);
            command.Parameters.AddWithValue("$model", (object?)meter.RawModelId ?? DBNull.Value);
            command.Parameters.AddWithValue("$status", (int)meter.Status);
            command.Parameters.AddWithValue("$strategy", meter.Provenance.StrategyId);
            command.Parameters.AddWithValue("$path", meter.Provenance.SourcePath);
            command.Parameters.AddWithValue("$acquired", Format(meter.Provenance.AcquiredAt));
            command.Parameters.AddWithValue("$authoritative", (meter.Provenance.IsAuthoritative ? 1 : 0));
            command.Parameters.AddWithValue("$attempt", (object?)meter.Provenance.AttemptId ?? DBNull.Value);
            command.Parameters.AddWithValue("$firstObserved", ((!meter.FirstObservedAt.HasValue) ? ((IConvertible)DBNull.Value) : ((IConvertible)Format(meter.FirstObservedAt.Value))));
            command.Parameters.AddWithValue("$isNew", (meter.IsNew ? 1 : 0));
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

    private static async Task InsertBalanceAsync(SqliteConnection connection, SqliteTransaction transaction, long snapshotId, BalanceMetric balance, CancellationToken cancellationToken)
    {
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO snapshot_balances(snapshot_id, balance_key, display_name, value, unit, formatted_value)\nVALUES($snapshot, $key, $display, $value, $unit, $formatted);";
            command.Parameters.AddWithValue("$snapshot", snapshotId);
            command.Parameters.AddWithValue("$key", balance.Key);
            command.Parameters.AddWithValue("$display", balance.DisplayName);
            command.Parameters.AddWithValue("$value", Format(balance.Value));
            command.Parameters.AddWithValue("$unit", (int)balance.Unit);
            command.Parameters.AddWithValue("$formatted", (object?)balance.FormattedValue ?? DBNull.Value);
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

    private static SnapshotHeader ReadHeader(SqliteDataReader reader)
    {
        return new SnapshotHeader(reader.GetInt64(0), (SnapshotCompleteness)reader.GetInt32(1), ParseDate(reader, 2).Value, (DataConfidence)reader.GetInt32(3), reader.GetString(4));
    }

    private static decimal? ParseDecimal(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : decimal.Parse(reader.GetString(ordinal), NumberStyles.Number, CultureInfo.InvariantCulture);
    }

    private static DateTimeOffset? ParseDate(SqliteDataReader reader, int ordinal)
    {
        return reader.IsDBNull(ordinal) ? null : DateTimeOffset.Parse(reader.GetString(ordinal), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }

    private static object Format(decimal? value)
    {
        return (object?)value?.ToString(CultureInfo.InvariantCulture) ?? DBNull.Value;
    }

    private static string Format(DateTimeOffset value)
    {
        return value.ToString("O", CultureInfo.InvariantCulture);
    }
}
