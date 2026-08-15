// SPDX-License-Identifier: Apache-2.0
using System.Data.Common;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using Microsoft.Data.Sqlite;

namespace AiLimits.Infrastructure.Persistence;

public sealed class SqliteUsageAggregateRepository(SqliteDatabase database) : IUsageAggregateRepository
{
    public async Task AddEventsAsync(IEnumerable<TokenUsageEvent> events, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using DbTransaction transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            foreach (TokenUsageEvent usage in events)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (
                    await TryInsertFingerprintAsync(
                            fingerprint: Fingerprint(usage.Account, usage.Service, usage.SourceEventId),
                            connection: connection,
                            transaction: (SqliteTransaction)transaction,
                            usage: usage,
                            cancellationToken: cancellationToken
                        )
                        .ConfigureAwait(false)
                )
                {
                    await UpsertAggregateAsync(connection, (SqliteTransaction)transaction, usage, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (connection != null)
            {
                await connection.DisposeAsync();
            }
        }
    }

    public async Task<IReadOnlyList<DailyUsageAggregate>> QueryAsync(
        DateOnly from,
        DateOnly through,
        IReadOnlyCollection<AccountKey>? accounts,
        CancellationToken cancellationToken
    )
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DailyUsageAggregate> result;
        try
        {
            SqliteCommand command = connection.CreateCommand();
            IReadOnlyList<DailyUsageAggregate> readOnlyList2;
            try
            {
                string accountFilter = string.Empty;
                if (accounts != null && accounts.Count > 0)
                {
                    List<string> clauses = new List<string>();
                    int index = 0;
                    foreach (AccountKey account in accounts)
                    {
                        clauses.Add($"(provider_id = $provider{index} AND account_id = $account{index})");
                        command.Parameters.AddWithValue($"$provider{index}", account.Provider.Value);
                        command.Parameters.AddWithValue($"$account{index}", account.Value);
                        index++;
                    }
                    accountFilter = " AND (" + string.Join(" OR ", clauses) + ")";
                }
                command.CommandText =
                    "SELECT day, provider_id, account_id, service_id, raw_model_id,\n       pricing_provider_id, canonical_model_id, resolution_confidence,\n       input_tokens, output_tokens, cache_read_tokens, cache_write_tokens,\n       reasoning_tokens, reported_service_cost_usd,\n       project_key, project_path, repository_root_path\nFROM daily_usage\nWHERE day >= $from AND day <= $through"
                    + accountFilter
                    + " ORDER BY day, service_id, raw_model_id, project_key;";
                command.Parameters.AddWithValue("$from", from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(
                    "$through",
                    through.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                );
                List<DailyUsageAggregate> rows = new List<DailyUsageAggregate>();
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<DailyUsageAggregate> readOnlyList;
                try
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        string pricingProvider = reader.GetString(5);
                        string canonicalModel = reader.GetString(6);
                        rows.Add(ReadAggregate(reader, pricingProvider, canonicalModel));
                    }
                    readOnlyList = rows;
                }
                finally
                {
                    if (reader != null)
                    {
                        await reader.DisposeAsync();
                    }
                }
                readOnlyList2 = readOnlyList;
            }
            finally
            {
                if (command != null)
                {
                    await command.DisposeAsync();
                }
            }
            result = readOnlyList2;
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

    private static DailyUsageAggregate ReadAggregate(
        SqliteDataReader reader,
        string pricingProvider,
        string canonicalModel
    )
    {
        ModelResolution? resolution =
            string.IsNullOrEmpty(pricingProvider) || string.IsNullOrEmpty(canonicalModel)
                ? null
                : new ModelResolution(pricingProvider, canonicalModel, (ResolutionConfidence)reader.GetInt32(7));
        decimal? reportedCost = reader.IsDBNull(13)
            ? null
            : decimal.Parse(reader.GetString(13), CultureInfo.InvariantCulture);
        ProjectIdentity project = new ProjectIdentity(
            reader.GetString(14),
            reader.GetString(15),
            reader.IsDBNull(16) ? null : reader.GetString(16)
        );
        return new DailyUsageAggregate(
            DateOnly.ParseExact(reader.GetString(0), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            new AccountKey(new ProviderId(reader.GetString(1)), reader.GetString(2)),
            new ServiceProviderId(reader.GetString(3)),
            reader.GetString(4),
            resolution,
            reader.GetInt64(8),
            reader.GetInt64(9),
            reader.GetInt64(10),
            reader.GetInt64(11),
            reader.GetInt64(12),
            reportedCost,
            project
        );
    }

    public async Task<ScannerCursor?> GetCursorAsync(
        AccountKey account,
        string sourceId,
        CancellationToken cancellationToken
    )
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        ScannerCursor result;
        try
        {
            SqliteCommand command = connection.CreateCommand();
            ScannerCursor scannerCursor2;
            try
            {
                command.CommandText =
                    "SELECT position, last_observed_at, fingerprint\nFROM scanner_cursors\nWHERE provider_id = $provider AND account_id = $account AND source_id = $source;";
                command.Parameters.AddWithValue("$provider", account.Provider.Value);
                command.Parameters.AddWithValue("$account", account.Value);
                command.Parameters.AddWithValue("$source", sourceId);
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                ScannerCursor scannerCursor;
                try
                {
                    scannerCursor = (
                        (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            ? new ScannerCursor(
                                sourceId,
                                reader.IsDBNull(0) ? null : reader.GetString(0),
                                reader.IsDBNull(1)
                                    ? null
                                    : DateTimeOffset.Parse(
                                        reader.GetString(1),
                                        CultureInfo.InvariantCulture,
                                        DateTimeStyles.RoundtripKind
                                    ),
                                reader.IsDBNull(2) ? null : reader.GetString(2)
                            )
                            : null
                    );
                }
                finally
                {
                    if (reader != null)
                    {
                        await reader.DisposeAsync();
                    }
                }
                scannerCursor2 = scannerCursor;
            }
            finally
            {
                if (command != null)
                {
                    await command.DisposeAsync();
                }
            }
            result = scannerCursor2;
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

    public async Task SaveCursorAsync(AccountKey account, ScannerCursor cursor, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteCommand command = connection.CreateCommand();
            try
            {
                command.CommandText =
                    "INSERT INTO scanner_cursors(\n    provider_id, account_id, source_id, position, last_observed_at, fingerprint)\nVALUES($provider, $account, $source, $position, $observed, $fingerprint)\nON CONFLICT(provider_id, account_id, source_id) DO UPDATE SET\n    position = excluded.position,\n    last_observed_at = excluded.last_observed_at,\n    fingerprint = excluded.fingerprint;";
                command.Parameters.AddWithValue("$provider", account.Provider.Value);
                command.Parameters.AddWithValue("$account", account.Value);
                command.Parameters.AddWithValue("$source", cursor.SourceId);
                command.Parameters.AddWithValue("$position", (object?)cursor.Position ?? DBNull.Value);
                command.Parameters.AddWithValue(
                    "$observed",
                    (object?)cursor.LastObservedAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value
                );
                command.Parameters.AddWithValue("$fingerprint", (object?)cursor.Fingerprint ?? DBNull.Value);
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

    private static async Task<bool> TryInsertFingerprintAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TokenUsageEvent usage,
        string fingerprint,
        CancellationToken cancellationToken
    )
    {
        SqliteCommand command = connection.CreateCommand();
        bool result;
        try
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT OR IGNORE INTO scanner_fingerprints(\n    fingerprint, provider_id, account_id, source_id, observed_at)\nVALUES($fingerprint, $provider, $account, $source, $observed);";
            command.Parameters.AddWithValue("$fingerprint", fingerprint);
            command.Parameters.AddWithValue("$provider", usage.Account.Provider.Value);
            command.Parameters.AddWithValue("$account", usage.Account.Value);
            command.Parameters.AddWithValue("$source", usage.Service.Value);
            command.Parameters.AddWithValue("$observed", usage.OccurredAt.ToString("O", CultureInfo.InvariantCulture));
            result = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
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

    private static async Task UpsertAggregateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TokenUsageEvent usage,
        CancellationToken cancellationToken
    )
    {
        SqliteCommand command = connection.CreateCommand();
        try
        {
            command.Transaction = transaction;
            command.CommandText =
                "INSERT INTO daily_usage(\n    day, provider_id, account_id, service_id, raw_model_id,\n    input_tokens, output_tokens, cache_read_tokens, cache_write_tokens, reasoning_tokens,\n    project_key, project_path, repository_root_path)\nVALUES($day, $provider, $account, $service, $model, $input, $output, $cacheRead, $cacheWrite, $reasoning,\n       $projectKey, $projectPath, $repositoryRootPath)\nON CONFLICT(\n    day, provider_id, account_id, service_id, raw_model_id,\n    pricing_provider_id, canonical_model_id, project_key)\nDO UPDATE SET\n    input_tokens = input_tokens + excluded.input_tokens,\n    output_tokens = output_tokens + excluded.output_tokens,\n    cache_read_tokens = cache_read_tokens + excluded.cache_read_tokens,\n    cache_write_tokens = cache_write_tokens + excluded.cache_write_tokens,\n    reasoning_tokens = reasoning_tokens + excluded.reasoning_tokens,\n    project_path = excluded.project_path,\n    repository_root_path = COALESCE(excluded.repository_root_path, repository_root_path);";
            command.Parameters.AddWithValue(
                "$day",
                DateOnly
                    .FromDateTime(usage.OccurredAt.ToLocalTime().DateTime)
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            );
            command.Parameters.AddWithValue("$provider", usage.Account.Provider.Value);
            command.Parameters.AddWithValue("$account", usage.Account.Value);
            command.Parameters.AddWithValue("$service", usage.Service.Value);
            command.Parameters.AddWithValue("$model", usage.RawModelId);
            command.Parameters.AddWithValue("$input", usage.InputTokens);
            command.Parameters.AddWithValue("$output", usage.OutputTokens);
            command.Parameters.AddWithValue("$cacheRead", usage.CacheReadTokens);
            command.Parameters.AddWithValue("$cacheWrite", usage.CacheWriteTokens);
            command.Parameters.AddWithValue("$reasoning", usage.ReasoningTokens);
            command.Parameters.AddWithValue("$projectKey", usage.Project.ProjectKey);
            command.Parameters.AddWithValue("$projectPath", usage.Project.ProjectPath);
            command.Parameters.AddWithValue(
                "$repositoryRootPath",
                (object?)usage.Project.RepositoryRootPath ?? DBNull.Value
            );
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

    private static string Fingerprint(AccountKey account, ServiceProviderId service, string sourceEventId)
    {
        byte[] inArray = SHA256.HashData(Encoding.UTF8.GetBytes($"{account}\n{service.Value}\n{sourceEventId}"));
        return Convert.ToHexString(inArray).ToLowerInvariant();
    }
}
