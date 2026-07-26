// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Claude;
using AiLimits.Infrastructure.Providers.Codex;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Providers.OpenCode;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class ScannerTests
{
    [Fact]
    public async Task CodexCarriesSessionMetadataProjectIntoUsageEvents()
    {
        using var temp = new TemporaryDirectory();
        var projectPath = Directory.CreateDirectory(Path.Combine(temp.Path, "İşler", "Yapay Zekâ")).FullName;
        var sessions = Directory.CreateDirectory(Path.Combine(temp.Path, "sessions"));
        var file = Path.Combine(sessions.FullName, "project.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-13T10:00:00Z",
                payload = new { cwd = projectPath }
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-13T10:01:00Z",
                payload = new
                {
                    model = "gpt-5",
                    info = new
                    {
                        total_token_usage = new
                        {
                            input_tokens = 100,
                            output_tokens = 50,
                            cached_input_tokens = 20,
                            reasoning_output_tokens = 10
                        }
                    }
                }
            })
        ]);

        var usage = Assert.Single(await CollectAsync(
            new CodexSessionTokenSource(temp.Path).ReadAsync(Account("codex"), null, default)));

        Assert.False(usage.Project.IsUnknown);
        Assert.Equal(projectPath, usage.Project.ProjectPath);
        Assert.Contains("İşler", usage.Project.ProjectPath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CodexCumulativeCountersEmitExactDeltas()
    {
        using var temp = new TemporaryDirectory();
        var sessions = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "sessions"));
        var file = System.IO.Path.Combine(sessions.FullName, "one.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"payload\":{\"model\":\"gpt-5\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":50,\"cached_input_tokens\":20,\"reasoning_output_tokens\":10}}}}",
            "{\"timestamp\":\"2026-07-13T10:01:00Z\",\"payload\":{\"info\":{\"total_token_usage\":{\"input_tokens\":160,\"output_tokens\":80,\"cached_input_tokens\":30,\"reasoning_output_tokens\":15}}}}"
        ]);
        var account = Account("codex");
        var events = await CollectAsync(new CodexSessionTokenSource(temp.Path).ReadAsync(account, null, default));
        Assert.Equal(2, events.Count);
        Assert.Equal(130, events.Sum(item => item.InputTokens));
        Assert.Equal(65, events.Sum(item => item.OutputTokens));
        Assert.Equal(30, events.Sum(item => item.CacheReadTokens));
        Assert.Equal(15, events.Sum(item => item.ReasoningTokens));
    }

    [Fact]
    public async Task ClaudeStreamingUpdatesDoNotDoubleCountInput()
    {
        using var temp = new TemporaryDirectory();
        var project = Directory.CreateDirectory(System.IO.Path.Combine(temp.Path, "projects", "demo"));
        var file = System.IO.Path.Combine(project.FullName, "chat.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20,\"cache_read_input_tokens\":10}}}",
            "{\"timestamp\":\"2026-07-13T10:00:02Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50,\"cache_read_input_tokens\":10}}}"
        ]);
        var events = await CollectAsync(new ClaudeJsonlTokenSource(temp.Path).ReadAsync(Account("claude"), null, default));
        Assert.Equal(2, events.Count);
        Assert.Equal(100, events.Sum(item => item.InputTokens));
        Assert.Equal(50, events.Sum(item => item.OutputTokens));
        Assert.Equal(10, events.Sum(item => item.CacheReadTokens));
    }

    [Fact]
    public async Task ClaudeCarriesWorkingDirectoryAcrossProjectJsonlRecords()
    {
        using var temp = new TemporaryDirectory();
        var workingDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "projects-on-disk", "çalışma")).FullName;
        var project = Directory.CreateDirectory(Path.Combine(temp.Path, "projects", "encoded-project"));
        var file = Path.Combine(project.FullName, "chat.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-13T10:00:00Z",
                cwd = workingDirectory,
                type = "user"
            }),
            JsonSerializer.Serialize(new
            {
                timestamp = "2026-07-13T10:00:02Z",
                message = new
                {
                    id = "m1",
                    model = "claude-sonnet-4",
                    usage = new { input_tokens = 100, output_tokens = 50 }
                }
            })
        ]);

        var usage = Assert.Single(await CollectAsync(
            new ClaudeJsonlTokenSource(temp.Path).ReadAsync(Account("claude"), null, default)));

        Assert.Equal(workingDirectory, usage.Project.ProjectPath);
    }

    [Fact]
    public async Task OpenCodeDatabaseReadsExactTokenClasses()
    {
        using var temp = new TemporaryDirectory();
        var path = System.IO.Path.Combine(temp.Path, "opencode.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE message(id TEXT PRIMARY KEY, time_created INTEGER, data TEXT NOT NULL);
                INSERT INTO message VALUES('m1', 1783936800000,
                  '{"role":"assistant","providerID":"github-copilot","modelID":"claude-opus-4.8","time":{"created":1783936800000},"tokens":{"input":120,"output":40,"reasoning":6,"cache":{"read":22,"write":3}},"cost":0.12}');
                """;
            await command.ExecuteNonQueryAsync();
        }

        var discovery = new OpenCodePathDiscovery(new FakeProcessRunner(path));
        var events = await CollectAsync(new OpenCodeDatabaseTokenSource(discovery).ReadAsync(Account("opencode"), null, default));
        var usage = Assert.Single(events);
        Assert.Equal(120, usage.InputTokens);
        Assert.Equal(40, usage.OutputTokens);
        Assert.Equal(22, usage.CacheReadTokens);
        Assert.Equal(3, usage.CacheWriteTokens);
        Assert.Equal(6, usage.ReasoningTokens);
        Assert.Equal("github-copilot", usage.Service.Value);
        Assert.Equal("claude-opus-4.8", usage.RawModelId);
    }

    [Fact]
    public async Task OpenCodeJoinsMessagesToTheirSessionDirectory()
    {
        using var temp = new TemporaryDirectory();
        var projectPath = Directory.CreateDirectory(Path.Combine(temp.Path, "depolar", "görsel-işler")).FullName;
        var path = Path.Combine(temp.Path, "opencode.db");
        await using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE [session](id TEXT PRIMARY KEY, directory TEXT NOT NULL);
                CREATE TABLE message(id TEXT PRIMARY KEY, session_id TEXT, time_created INTEGER, data TEXT NOT NULL);
                INSERT INTO [session](id, directory) VALUES('s1', $directory);
                INSERT INTO message VALUES('m1', 's1', 1783936800000,
                  '{"role":"assistant","providerID":"github-copilot","modelID":"gpt-5","time":{"created":1783936800000},"tokens":{"input":12,"output":4}}');
                """;
            command.Parameters.AddWithValue("$directory", projectPath);
            await command.ExecuteNonQueryAsync();
        }

        var discovery = new OpenCodePathDiscovery(new FakeProcessRunner(path));
        var usage = Assert.Single(await CollectAsync(
            new OpenCodeDatabaseTokenSource(discovery).ReadAsync(Account("opencode"), null, default)));

        Assert.Equal(projectPath, usage.Project.ProjectPath);
    }

    [Fact]
    public void OpenCodeHistoryDoesNotImplyAnOpenCodeSubscription()
    {
        var discovery = new OpenCodePathDiscovery(new FakeProcessRunner("opencode.db"));
        var adapter = new OpenCodeProviderAdapter(discovery);
        Assert.Empty(adapter.CreateLimitStrategies(Account("opencode")));
    }


    private static ProviderAccount Account(string provider) => new(
        new AccountKey(new ProviderId(provider), "one"), "one", null, "fixture", 1, true);

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items;
    }

    private sealed class FakeProcessRunner(string path) : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken) =>
            Task.FromResult(new ProcessResult(0, path, string.Empty, TimeSpan.Zero));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public string Path { get; }
        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
