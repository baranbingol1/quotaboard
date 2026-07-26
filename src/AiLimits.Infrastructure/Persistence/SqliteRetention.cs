// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace AiLimits.Infrastructure.Persistence;

/// <summary>
/// Age-based pruning for the append-only bookkeeping tables. Without it the
/// database grows without bound (measured ~2.3 MB/day on a real install:
/// one snapshot per account per refresh, one fetch-attempt row per strategy
/// call, one fingerprint per usage event forever). daily_usage is the durable
/// ledger and is never pruned here.
/// </summary>
public sealed class SqliteRetention(SqliteDatabase database)
{
    // Each account's newest snapshot is always kept so cached data still
    // paints after arbitrary downtime; only older history ages out.
    private static readonly TimeSpan SnapshotRetention = TimeSpan.FromDays(90);
    private static readonly TimeSpan FetchAttemptRetention = TimeSpan.FromDays(30);
    // Must exceed every scanner's replay reach (5-minute overlap windows,
    // Amp's one-day overlap, Cursor's 30-day initial backfill) so a pruned
    // fingerprint can never let a replayed event double-count.
    private static readonly TimeSpan FingerprintRetention = TimeSpan.FromDays(60);
    private static readonly TimeSpan AlertStateRetention = TimeSpan.FromDays(60);

    public async Task PruneAsync(DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using (connection.ConfigureAwait(false))
        {
            SqliteCommand command = connection.CreateCommand();
            await using (command.ConfigureAwait(false))
            {
                command.CommandText = """
                    DELETE FROM snapshots
                    WHERE observed_at < $snapshotCutoff
                      AND id NOT IN (SELECT MAX(id) FROM snapshots GROUP BY provider_id, account_id);
                    DELETE FROM fetch_attempts WHERE started_at < $attemptCutoff;
                    DELETE FROM scanner_fingerprints WHERE observed_at < $fingerprintCutoff;
                    DELETE FROM alert_state WHERE notified_at < $alertCutoff;
                    """;
                command.Parameters.AddWithValue("$snapshotCutoff", Format(now - SnapshotRetention));
                command.Parameters.AddWithValue("$attemptCutoff", Format(now - FetchAttemptRetention));
                command.Parameters.AddWithValue("$fingerprintCutoff", Format(now - FingerprintRetention));
                command.Parameters.AddWithValue("$alertCutoff", Format(now - AlertStateRetention));
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
