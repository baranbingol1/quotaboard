// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Diagnostics;
using System.Globalization;

namespace AiLimits.Infrastructure.Providers.OpenCode;

internal sealed class OpenCodeGoLocalLimitStrategy(OpenCodePathDiscovery discovery, IClock clock) : ILimitFetchStrategy
{
    private sealed record CostRow(DateTimeOffset At, decimal Cost);

    private const decimal SessionLimitUsd = 12m;

    private const decimal WeeklyLimitUsd = 30m;

    private const decimal MonthlyLimitUsd = 60m;

    public string Id => "opencode.go-local";

    public int Order => 20;

    public async Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        return File.Exists(await discovery.FindDatabaseAsync(cancellationToken).ConfigureAwait(false)) ? StrategyAvailabilityResult.Ready() : new StrategyAvailabilityResult(StrategyAvailability.NotConfigured, "OpenCode database was not found.");
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string path = await discovery.FindDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path))
        {
            return FetchResult.Failure(FetchFailureKind.Unsupported, "OpenCode database was not found.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }
        try
        {
            IReadOnlyList<CostRow> rows = await ReadCostRowsAsync(path, cancellationToken).ConfigureAwait(false);
            if (rows.Count == 0)
            {
                return FetchResult.Failure(FetchFailureKind.Unsupported, "No OpenCode Go subscription usage was found in the local database.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
            }
            DateTimeOffset now = clock.UtcNow;
            DateTimeOffset sessionStart = now.AddHours(-5.0);
            DateTimeOffset weekStart = StartOfUtcWeek(now);
            DateTimeOffset monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            DateTimeOffset nextMonth = monthStart.AddMonths(1);
            // Old Go history alone must not keep budget meters alive: require a live
            // Zen/Go auth entry or activity inside the current billing month. An
            // authoritative empty snapshot also clears previously published meters.
            if (!await HasOpenCodeAuthAsync(path, cancellationToken).ConfigureAwait(false)
                && !rows.Any((CostRow row) => row.At >= monthStart))
            {
                return FetchResult.Success(new ProviderSnapshot(account.Key, Array.Empty<UsageMeter>(), Array.Empty<BalanceMetric>(), SnapshotCompleteness.Authoritative, now, DataConfidence.Medium, ProviderHttpSupport.SafeExtensions(("offering", "go-local-inactive"))), Id, Stopwatch.GetElapsedTime(started));
            }
            decimal sessionUsed = Sum(rows, sessionStart, now);
            decimal weekUsed = Sum(rows, weekStart, weekStart.AddDays(7.0));
            decimal monthUsed = Sum(rows, monthStart, nextMonth);
            DateTimeOffset? oldestInSession = rows.Where((CostRow row) => row.At >= sessionStart && row.At < now).MinBy((CostRow row) => row.At)?.At;
            MeterProvenance provenance = new MeterProvenance(Id, "opencode.db:message/part.cost", now, IsAuthoritative: true);
            return FetchResult.Success(new ProviderSnapshot(Meters: new UsageMeter[]
            {
                Meter("go-session-cost", "Go session budget", sessionUsed, 12m, oldestInSession?.AddHours(5.0), TimeSpan.FromHours(5), provenance),
                Meter("go-week-cost", "Go weekly budget", weekUsed, 30m, weekStart.AddDays(7.0), TimeSpan.FromDays(7), provenance),
                Meter("go-month-cost", "Go monthly budget", monthUsed, 60m, nextMonth, nextMonth - monthStart, provenance)
            }, Account: account.Key, Balances: Array.Empty<BalanceMetric>(), Completeness: SnapshotCompleteness.Authoritative, ObservedAt: now, Confidence: DataConfidence.Medium, Extensions: ProviderHttpSupport.SafeExtensions(("offering", "go-local"))), Id, Stopwatch.GetElapsedTime(started));
        }
        catch (SqliteException)
        {
            return FetchResult.Failure(FetchFailureKind.MalformedResponse, "OpenCode database could not be read.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }
    }

    private static UsageMeter Meter(string key, string name, decimal used, decimal limit, DateTimeOffset? resets, TimeSpan window, MeterProvenance provenance)
    {
        double num = ((limit > 0m) ? Math.Clamp((double)(used / limit * 100m), 0.0, 100.0) : 0.0);
        MeterStatus status = ((num >= 100.0) ? MeterStatus.Exhausted : ((num >= 95.0) ? MeterStatus.Critical : ((!(num >= 80.0)) ? MeterStatus.Healthy : MeterStatus.Approaching)));
        return new UsageMeter(new MeterKey("opencode:" + key), name, MeterScope.Offering, MeterUnit.Usd, used, limit, num, window, resets, "opencode-go", status, provenance);
    }

    private static async Task<IReadOnlyList<CostRow>> ReadCostRowsAsync(string path, CancellationToken cancellationToken)
    {
        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        SqliteConnection connection = new SqliteConnection(connectionString);
        IReadOnlyList<CostRow> result;
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            SqliteCommand command = connection.CreateCommand();
            IReadOnlyList<CostRow> readOnlyList2;
            try
            {
                command.CommandText = "SELECT CAST(COALESCE(json_extract(data, '$.time.created'), time_created) AS INTEGER),\n       CAST(json_extract(data, '$.cost') AS REAL)\nFROM message\nWHERE json_valid(data)\n  AND json_extract(data, '$.providerID') = 'opencode-go'\n  AND json_extract(data, '$.role') = 'assistant'\n  AND json_type(data, '$.cost') IN ('integer', 'real');";
                List<CostRow> rows = new List<CostRow>();
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<CostRow> readOnlyList;
                try
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        long milliseconds = reader.GetInt64(0);
                        double cost = reader.GetDouble(1);
                        if (milliseconds > 0 && double.IsFinite(cost) && cost >= 0.0)
                        {
                            rows.Add(new CostRow(DateTimeOffset.FromUnixTimeMilliseconds(milliseconds), Convert.ToDecimal(cost, CultureInfo.InvariantCulture)));
                        }
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

    private static async Task<bool> HasOpenCodeAuthAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            string authPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath)) ?? string.Empty, "auth.json");
            if (!File.Exists(authPath))
            {
                return false;
            }
            await using FileStream stream = File.OpenRead(authPath);
            using System.Text.Json.JsonDocument document = await System.Text.Json.JsonDocument.ParseAsync(stream, default(System.Text.Json.JsonDocumentOptions), cancellationToken).ConfigureAwait(false);
            return document.RootElement.TryGetProperty("opencode", out _) || document.RootElement.TryGetProperty("opencode-go", out _) || document.RootElement.TryGetProperty("opencode-zen", out _);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static decimal Sum(IEnumerable<CostRow> rows, DateTimeOffset start, DateTimeOffset end)
    {
        return rows.Where((CostRow row) => row.At >= start && row.At < end).Sum((CostRow row) => row.Cost);
    }

    private static DateTimeOffset StartOfUtcWeek(DateTimeOffset now)
    {
        int num = (int)(now.DayOfWeek + 6) % 7;
        return new DateTimeOffset(now.UtcDateTime.Date.AddDays(-num), TimeSpan.Zero);
    }
}
