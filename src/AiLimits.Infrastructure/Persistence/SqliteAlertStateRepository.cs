// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Application.Alerts;
using Microsoft.Data.Sqlite;

namespace AiLimits.Infrastructure.Persistence;

/// <summary>
/// Durable, atomic deduplication for quota notifications. The existing
/// alert_state primary key makes INSERT OR IGNORE a cross-refresh claim.
/// </summary>
public sealed class SqliteAlertStateRepository(SqliteDatabase database) : IAlertStateStore
{
    public async Task<bool> TryClaimAsync(
        AlertCandidate candidate,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken
    )
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO alert_state(
                provider_id, account_id, meter_key, threshold_key, reset_cycle, notified_at)
            VALUES($provider, $account, $meter, $event, $cycle, $notified);
            """;
        AddIdentityParameters(command, candidate);
        command.Parameters.AddWithValue("$notified", Format(claimedAt));
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task ReleaseAsync(AlertCandidate candidate, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DELETE FROM alert_state
            WHERE provider_id = $provider
              AND account_id = $account
              AND meter_key = $meter
              AND threshold_key = $event
              AND reset_cycle = $cycle;
            """;
        AddIdentityParameters(command, candidate);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddIdentityParameters(SqliteCommand command, AlertCandidate candidate)
    {
        command.Parameters.AddWithValue("$provider", candidate.Account.Provider.Value);
        command.Parameters.AddWithValue("$account", candidate.Account.Value);
        command.Parameters.AddWithValue("$meter", candidate.Meter.Value);
        command.Parameters.AddWithValue("$event", candidate.DeduplicationKey);
        command.Parameters.AddWithValue(
            "$cycle",
            candidate.ResetCycle is DateTimeOffset cycle ? Format(cycle) : "open"
        );
    }

    private static string Format(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
