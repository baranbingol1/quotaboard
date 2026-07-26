// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using Microsoft.Data.Sqlite;
using System.Data.Common;
using System.Globalization;

namespace AiLimits.Infrastructure.Persistence;

public sealed class SqliteAccountRepository(SqliteDatabase database) : IAccountRepository
{
    public async Task<IReadOnlyList<ProviderAccount>> ListAsync(CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderAccount> result;
        try
        {
            SqliteCommand command = connection.CreateCommand();
            IReadOnlyList<ProviderAccount> readOnlyList2;
            try
            {
                command.CommandText = "SELECT provider_id, account_id, display_name, login, auth_source,\n       configuration_revision, is_connected, last_successful_refresh_at\nFROM accounts\nORDER BY provider_id, display_name;";
                List<ProviderAccount> accounts = new List<ProviderAccount>();
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                IReadOnlyList<ProviderAccount> readOnlyList;
                try
                {
                    while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    {
                        accounts.Add(Read(reader));
                    }
                    readOnlyList = accounts;
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

    public async Task<ProviderAccount?> GetAsync(AccountKey key, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        ProviderAccount result;
        try
        {
            SqliteCommand command = connection.CreateCommand();
            ProviderAccount providerAccount2;
            try
            {
                command.CommandText = "SELECT provider_id, account_id, display_name, login, auth_source,\n       configuration_revision, is_connected, last_successful_refresh_at\nFROM accounts\nWHERE provider_id = $provider AND account_id = $account;";
                command.Parameters.AddWithValue("$provider", key.Provider.Value);
                command.Parameters.AddWithValue("$account", key.Value);
                SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                ProviderAccount providerAccount;
                try
                {
                    providerAccount = ((await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) ? Read(reader) : null);
                }
                finally
                {
                    if (reader != null)
                    {
                        await reader.DisposeAsync();
                    }
                }
                providerAccount2 = providerAccount;
            }
            finally
            {
                if (command != null)
                {
                    await command.DisposeAsync();
                }
            }
            result = providerAccount2;
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

    public async Task UpsertAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteCommand command = connection.CreateCommand();
            try
            {
                command.CommandText = "INSERT INTO accounts(\n    provider_id, account_id, display_name, login, auth_source,\n    configuration_revision, is_connected, last_successful_refresh_at)\nVALUES($provider, $account, $display, $login, $auth, $revision, $connected, $lastSuccess)\nON CONFLICT(provider_id, account_id) DO UPDATE SET\n    display_name = excluded.display_name,\n    login = excluded.login,\n    auth_source = excluded.auth_source,\n    configuration_revision = excluded.configuration_revision,\n    is_connected = excluded.is_connected,\n    last_successful_refresh_at = excluded.last_successful_refresh_at;";
                command.Parameters.AddWithValue("$provider", account.Key.Provider.Value);
                command.Parameters.AddWithValue("$account", account.Key.Value);
                command.Parameters.AddWithValue("$display", account.DisplayName);
                command.Parameters.AddWithValue("$login", (object?)account.Login ?? DBNull.Value);
                command.Parameters.AddWithValue("$auth", account.AuthSource);
                command.Parameters.AddWithValue("$revision", account.ConfigurationRevision);
                command.Parameters.AddWithValue("$connected", (account.IsConnected ? 1 : 0));
                command.Parameters.AddWithValue("$lastSuccess", (object?)account.LastSuccessfulRefreshAt?.ToString("O", CultureInfo.InvariantCulture) ?? DBNull.Value);
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

    public async Task DeleteAsync(AccountKey key, CancellationToken cancellationToken)
    {
        SqliteConnection connection = await database.OpenAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            SqliteCommand command = connection.CreateCommand();
            try
            {
                command.CommandText = "DELETE FROM accounts WHERE provider_id = $provider AND account_id = $account;";
                command.Parameters.AddWithValue("$provider", key.Provider.Value);
                command.Parameters.AddWithValue("$account", key.Value);
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

    private static ProviderAccount Read(SqliteDataReader reader)
    {
        return new ProviderAccount(new AccountKey(new ProviderId(reader.GetString(0)), reader.GetString(1)), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetString(4), reader.GetInt64(5), reader.GetInt64(6) != 0, reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
    }
}
