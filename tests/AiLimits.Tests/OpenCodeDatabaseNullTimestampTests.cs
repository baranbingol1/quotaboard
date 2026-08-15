// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Providers.OpenCode;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class OpenCodeDatabaseNullTimestampTests
{
    [Fact]
    public async Task MessageTableWithoutTimeCreatedColumnStillReads()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "opencode.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE message(id TEXT PRIMARY KEY, data TEXT NOT NULL);
                INSERT INTO message VALUES('m1',
                  '{"role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1783936800000},"tokens":{"input":10,"output":5}}');
                INSERT INTO message VALUES('m2',
                  '{"role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1783936800001},"tokens":{"input":20,"output":10}}');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var discovery = new OpenCodePathDiscovery(new FakeProcessRunner(path));
        var events = await CollectAsync(
            new OpenCodeDatabaseTokenSource(discovery).ReadAsync(Account("opencode"), null, default)
        );

        Assert.Equal(2, events.Count);
        Assert.Equal(10, events[0].InputTokens);
        Assert.Equal(20, events[1].InputTokens);
    }

    [Fact]
    public async Task RowWithNullTimestampIsSkippedButLaterRowsStillYielded()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "opencode.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE message(id TEXT PRIMARY KEY, time_created INTEGER, data TEXT NOT NULL);
                INSERT INTO message VALUES('m_null', NULL,
                  '{"role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":null},"tokens":{"input":10,"output":5}}');
                INSERT INTO message VALUES('m_ok', 1783936800001,
                  '{"role":"assistant","providerID":"openai","modelID":"gpt-5","time":{"created":1783936800001},"tokens":{"input":20,"output":10}}');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var discovery = new OpenCodePathDiscovery(new FakeProcessRunner(path));
        var events = await CollectAsync(
            new OpenCodeDatabaseTokenSource(discovery).ReadAsync(Account("opencode"), null, default)
        );

        var usage = Assert.Single(events);
        Assert.Contains("m_ok", usage.SourceEventId, StringComparison.Ordinal);
        Assert.Equal(20, usage.InputTokens);
    }

    [Fact]
    public async Task IncrementalCursorSkipsRowsOlderThanOverlapFloor()
    {
        // Overlap floor is cursor - 5 min; only rows strictly newer survive.
        const long baseMs = 1783936800000;
        const long oldMs = baseMs - 600_000; // 10 min before → covered
        const long newMs = baseMs + 5_000; // after cursor → must read

        static string Msg(long ms, long input, long output) =>
            "{\"role\":\"assistant\",\"providerID\":\"openai\",\"modelID\":\"gpt-5\",\"time\":{\"created\":"
            + ms
            + "},\"tokens\":{\"input\":"
            + input
            + ",\"output\":"
            + output
            + "}}";

        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "opencode.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE message(id TEXT PRIMARY KEY, time_created INTEGER, data TEXT NOT NULL);"
                + "INSERT INTO message VALUES('m_old', "
                + oldMs
                + ", '"
                + Msg(oldMs, 10, 5)
                + "');"
                + "INSERT INTO message VALUES('m_new', "
                + newMs
                + ", '"
                + Msg(newMs, 20, 10)
                + "');";
            await command.ExecuteNonQueryAsync();
        }

        var cursor = new ScannerCursor(
            "opencode.database",
            "incremental",
            DateTimeOffset.FromUnixTimeMilliseconds(baseMs),
            null
        );
        var discovery = new OpenCodePathDiscovery(new FakeProcessRunner(path));
        var events = await CollectAsync(
            new OpenCodeDatabaseTokenSource(discovery).ReadAsync(Account("opencode"), cursor, default)
        );

        var usage = Assert.Single(events);
        Assert.Contains("m_new", usage.SourceEventId, StringComparison.Ordinal);
        Assert.Equal(20, usage.InputTokens);
    }

    private static ProviderAccount Account(string provider) =>
        new(new AccountKey(new ProviderId(provider), "one"), "one", null, "fixture", 1, true);

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
    }

    private sealed class FakeProcessRunner(string path) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken
        ) => Task.FromResult(new ProcessResult(0, path, string.Empty, TimeSpan.Zero));
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
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
