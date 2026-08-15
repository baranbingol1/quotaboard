// SPDX-License-Identifier: Apache-2.0
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Usage;
using Microsoft.Data.Sqlite;

namespace AiLimits.Infrastructure.Providers.OpenCode;

public sealed class OpenCodeDatabaseTokenSource(OpenCodePathDiscovery discovery) : ITokenUsageSource
{
    private readonly ProjectIdentityResolver projectIdentityResolver = new();

    public string Id => "opencode.database";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        var path = await discovery.FindDatabaseAsync(cancellationToken).ConfigureAwait(false);
        if (!File.Exists(path))
            yield break;
        var openAiAuthType = await ReadOpenAiAuthTypeAsync(path, cancellationToken).ConfigureAwait(false);

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString();
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        var messageColumns = await ReadColumnsAsync(connection, "message", cancellationToken).ConfigureAwait(false);
        var sessionColumns = await ReadColumnsAsync(connection, "session", cancellationToken).ConfigureAwait(false);
        var hasTimeCreatedColumn = messageColumns.Contains("time_created");
        var timestampExpression = hasTimeCreatedColumn
            ? "COALESCE(CAST(json_extract(m.data, '$.time.created') AS INTEGER), m.time_created)"
            : "CAST(json_extract(m.data, '$.time.created') AS INTEGER)";
        var canJoinSession = messageColumns.Contains("session_id") && sessionColumns.Contains("id");
        var sessionJoin = canJoinSession ? "LEFT JOIN [session] AS s ON s.id = m.session_id" : string.Empty;
        var projectExpression =
            canJoinSession && sessionColumns.Contains("directory") ? "s.directory"
            : canJoinSession && sessionColumns.Contains("data") ? "json_extract(s.data, '$.directory')"
            : "NULL";
        // Incremental floor: only read rows newer than the last scan's overlap
        // window. When a real time_created column exists we filter on it (no
        // JSON parse for the excluded majority); otherwise fall back to the
        // JSON timestamp. The per-row AlreadyCovered check below still enforces
        // the exact boundary, so an over-inclusive filter here is harmless.
        var rescanFloorMs = ScannerBoundary.RescanFloor(cursor)?.ToUnixTimeMilliseconds();
        var incrementalFilter =
            rescanFloorMs is null ? string.Empty
            : hasTimeCreatedColumn ? "AND (m.time_created IS NULL OR m.time_created > @since)"
            : $"AND {timestampExpression} > @since";
        await using var command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT m.id,
                   {timestampExpression},
                   m.data,
                   {projectExpression}
            FROM message AS m
            {sessionJoin}
            WHERE json_valid(m.data)
              AND json_extract(m.data, '$.role') = 'assistant'
              {incrementalFilter}
            ORDER BY {timestampExpression}, m.id;
            """;
        if (rescanFloorMs is { } sinceMs)
            command.Parameters.AddWithValue("@since", sinceMs);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = reader.GetString(0);
            if (reader.IsDBNull(1))
                continue;
            var milliseconds = reader.GetInt64(1);
            var occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(milliseconds);
            if (ScannerBoundary.AlreadyCovered(cursor, occurredAt))
                continue;

            using var document = JsonDocument.Parse(reader.GetString(2));
            var root = document.RootElement;
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                continue;
            var cacheRead = 0L;
            var cacheWrite = 0L;
            if (tokens.TryGetProperty("cache", out var cache) && cache.ValueKind == JsonValueKind.Object)
            {
                cacheRead = ReadLong(cache, "read");
                cacheWrite = ReadLong(cache, "write");
            }
            var model = ReadString(root, "modelID") ?? "unknown";
            var authorizationProvider = ReadString(root, "providerID") ?? "opencode";
            if (authorizationProvider == "openai")
            {
                // OpenCode records a computed cost for metered API-key usage and
                // cost=0 for subscription-covered requests; auth.json's current
                // connection type breaks the tie for cost=0 rows.
                authorizationProvider = ReadCost(root) > 0m || openAiAuthType == "api" ? "openai-api" : "openai-oauth";
            }
            var workingDirectory = reader.IsDBNull(3) ? ReadWorkingDirectory(root) : reader.GetString(3);
            // OpenCode already normalizes its lanes to be disjoint: tokens.output
            // excludes reasoning and tokens.input excludes cache reads (verified
            // against a real opencode.db, where reasoning regularly exceeds
            // output). Do NOT subtract here the way the Codex/Factory sources do.
            yield return new TokenUsageEvent(
                account.Key,
                new ServiceProviderId(authorizationProvider),
                model,
                occurredAt,
                ReadLong(tokens, "input"),
                ReadLong(tokens, "output"),
                cacheRead,
                cacheWrite,
                ReadLong(tokens, "reasoning"),
                $"opencode:{authorizationProvider}:{id}",
                projectIdentityResolver.Resolve(workingDirectory)
            );
        }
    }

    private static async Task<HashSet<string>> ReadColumnsAsync(
        SqliteConnection connection,
        string table,
        CancellationToken cancellationToken
    )
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info([{table}]);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            columns.Add(reader.GetString(1));
        return columns;
    }

    private static string? ReadWorkingDirectory(JsonElement root)
    {
        if (ReadString(root, "cwd") is { } cwd)
            return cwd;
        if (ReadString(root, "directory") is { } directory)
            return directory;
        if (root.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.Object)
            return ReadString(path, "cwd") ?? ReadString(path, "directory") ?? ReadString(path, "root");
        return null;
    }

    private static async Task<string?> ReadOpenAiAuthTypeAsync(string databasePath, CancellationToken cancellationToken)
    {
        try
        {
            var authPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, "auth.json");
            if (!File.Exists(authPath))
                return null;
            await using var stream = File.OpenRead(authPath);
            using var document = await JsonDocument
                .ParseAsync(stream, default, cancellationToken)
                .ConfigureAwait(false);
            return
                document.RootElement.TryGetProperty("openai", out var openai)
                && openai.ValueKind == JsonValueKind.Object
                ? ReadString(openai, "type")
                : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static decimal ReadCost(JsonElement element) =>
        element.TryGetProperty("cost", out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetDecimal(out var cost)
            ? cost
            : 0m;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static long ReadLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? Math.Max(0, result) : 0;
}
