// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Cline;
using AiLimits.Infrastructure.Usage;

namespace AiLimits.Tests;

public sealed class ClineTaskHistoryTokenSourceTests
{
    private static readonly DateTimeOffset Base = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Task_layout_emits_per_request_events_with_project_attribution()
    {
        using var temp = new TempDir();
        string project = temp.Dir("work\\demo");
        long sayTs = Base.AddMinutes(-1).ToUnixTimeMilliseconds();
        long taskTs = Base.ToUnixTimeMilliseconds();
        long codexTs = Base.AddMinutes(1).ToUnixTimeMilliseconds();
        long pendingTs = Base.AddMinutes(2).ToUnixTimeMilliseconds();
        long fallbackTs = Base.AddMinutes(3).ToUnixTimeMilliseconds();
        WriteTaskHistory(temp.Path, ("task1", project, "gpt-5.3-codex"));
        WriteUiMessages(temp.Path, "task1",
            Say(sayTs, "task", "analyze the diffs"),
            ApiReq(taskTs, Tokens(14363, 1512, cacheReads: 256), ModelInfo("cline-pass", "cline-pass/kimi-k3")),
            ApiReq(codexTs, Tokens(100, 10), ModelInfo("openai-codex", "gpt-5.3-codex")),
            ApiReq(pendingTs, "{\"request\":\"still running\"}", ModelInfo("cline-pass", "cline-pass/kimi-k3")),
            ApiReq(fallbackTs, Tokens(7, 3)));

        var events = await CollectAsync(
            new ClineTaskHistoryTokenSource([temp.Path]).ReadAsync(Account(), null, default));

        Assert.Equal(3, events.Count);
        TokenUsageEvent first = events[0];
        Assert.Equal(14363, first.InputTokens);
        Assert.Equal(1512, first.OutputTokens);
        Assert.Equal(256, first.CacheReadTokens);
        Assert.Equal(0, first.CacheWriteTokens);
        Assert.Equal(0, first.ReasoningTokens);
        Assert.Equal("cline-pass/kimi-k3", first.RawModelId);
        Assert.Equal(new ServiceProviderId("cline"), first.Service);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(taskTs), first.OccurredAt);
        Assert.Equal($"cline:task:task1:{taskTs}", first.SourceEventId);
        Assert.Equal(Normalize(project), first.Project.ProjectPath);

        // A bare logged model id keeps its routing backend on the event.
        TokenUsageEvent codex = events[1];
        Assert.Equal(100, codex.InputTokens);
        Assert.Equal(10, codex.OutputTokens);
        Assert.Equal("openai-codex/gpt-5.3-codex", codex.RawModelId);
        Assert.Equal($"cline:task:task1:{codexTs}", codex.SourceEventId);
        Assert.Equal(Normalize(project), codex.Project.ProjectPath);

        // No modelInfo on the message: the task-history model id is the fallback,
        // and the still-running request above emitted nothing.
        TokenUsageEvent fallback = events[2];
        Assert.Equal("gpt-5.3-codex", fallback.RawModelId);
        Assert.Equal(7, fallback.InputTokens);
        Assert.Equal(3, fallback.OutputTokens);
        Assert.Equal($"cline:task:task1:{fallbackTs}", fallback.SourceEventId);
    }

    [Fact]
    public async Task Session_layout_emits_only_metered_assistant_messages()
    {
        using var temp = new TempDir();
        string project = temp.Dir("work\\cli");
        long firstTs = Base.ToUnixTimeMilliseconds();
        long secondTs = Base.AddMinutes(1).ToUnixTimeMilliseconds();
        long fallbackTs = Base.AddMinutes(2).ToUnixTimeMilliseconds();
        WriteSession(temp.Path, "sess1",
            SessionMessages(
                User(firstTs),
                Assistant("m_a1", firstTs, Metrics(4969, 371), SessionModelInfo("cline-pass", "cline-pass/kimi-k3")),
                Assistant("m_a2", secondTs, null, SessionModelInfo("cline-pass", "cline-pass/kimi-k3"))),
            Sibling(cwd: project));
        WriteSession(temp.Path, "sess2",
            SessionMessages(
                Assistant(null, fallbackTs, Metrics(10, 2, cacheRead: 4), SessionModelInfo("cline-pass", "cline-pass/kimi-k3"))),
            Sibling(workspaceRoot: project.Replace('\\', '/')));

        var events = await CollectAsync(
            new ClineTaskHistoryTokenSource([temp.Path]).ReadAsync(Account(), null, default));

        Assert.Equal(2, events.Count);
        TokenUsageEvent first = events[0];
        Assert.Equal(4969, first.InputTokens);
        Assert.Equal(371, first.OutputTokens);
        Assert.Equal(0, first.CacheReadTokens);
        Assert.Equal("cline-pass/kimi-k3", first.RawModelId);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(firstTs), first.OccurredAt);
        Assert.Equal("cline:session:sess1:m_a1", first.SourceEventId);
        Assert.Equal(Normalize(project), first.Project.ProjectPath);

        // No message id: the timestamp keys both dedupe and the source event id;
        // workspace_root stands in when cwd is absent.
        TokenUsageEvent fallback = events[1];
        Assert.Equal(10, fallback.InputTokens);
        Assert.Equal(2, fallback.OutputTokens);
        Assert.Equal(4, fallback.CacheReadTokens);
        Assert.Equal($"cline:session:sess2:{fallbackTs}", fallback.SourceEventId);
        Assert.Equal(Normalize(project), fallback.Project.ProjectPath);
    }

    [Fact]
    public async Task Rewritten_messages_emit_only_the_delta()
    {
        using var temp = new TempDir();
        long ts = Base.ToUnixTimeMilliseconds();
        WriteUiMessages(temp.Path, "task1",
            ApiReq(ts, Tokens(100, 10), ModelInfo("cline-pass", "cline-pass/kimi-k3")),
            ApiReq(ts, Tokens(250, 25, cacheReads: 40), ModelInfo("cline-pass", "cline-pass/kimi-k3")));

        var events = await CollectAsync(
            new ClineTaskHistoryTokenSource([temp.Path]).ReadAsync(Account(), null, default));

        Assert.Equal(2, events.Count);
        Assert.Equal(100, events[0].InputTokens);
        Assert.Equal(10, events[0].OutputTokens);
        Assert.Equal(150, events[1].InputTokens);
        Assert.Equal(15, events[1].OutputTokens);
        Assert.Equal(40, events[1].CacheReadTokens);
        Assert.Equal(events[0].SourceEventId, events[1].SourceEventId);
        // Totals across the deltas equal the final absolute counts: no double-count.
        Assert.Equal(250, events.Sum(e => e.InputTokens));
        Assert.Equal(25, events.Sum(e => e.OutputTokens));
    }

    [Fact]
    public async Task Cursor_skips_covered_events_and_reemits_overlap_with_stable_ids()
    {
        using var temp = new TempDir();
        long oldTs = Base.AddMinutes(-6).ToUnixTimeMilliseconds();
        long overlapTs = Base.AddMinutes(-4).ToUnixTimeMilliseconds();
        long newTs = Base.AddMinutes(1).ToUnixTimeMilliseconds();
        WriteUiMessages(temp.Path, "task1",
            ApiReq(oldTs, Tokens(1, 1), ModelInfo("cline-pass", "cline-pass/kimi-k3")),
            ApiReq(overlapTs, Tokens(2, 2), ModelInfo("cline-pass", "cline-pass/kimi-k3")),
            ApiReq(newTs, Tokens(3, 3), ModelInfo("cline-pass", "cline-pass/kimi-k3")));

        var source = new ClineTaskHistoryTokenSource([temp.Path]);
        var full = await CollectAsync(source.ReadAsync(Account(), null, default));
        var cursor = new ScannerCursor(source.Id, null, Base, null);
        var incremental = await CollectAsync(source.ReadAsync(Account(), cursor, default));

        Assert.Equal(3, full.Count);
        Assert.Equal(2, incremental.Count);
        Assert.Equal(
            full.Skip(1).Select(e => e.SourceEventId),
            incremental.Select(e => e.SourceEventId));
        Assert.Equal(
            [$"cline:task:task1:{overlapTs}", $"cline:task:task1:{newTs}"],
            incremental.Select(e => e.SourceEventId).ToArray());
    }

    [Fact]
    public async Task Both_layouts_across_roots_scan_together_with_distinct_ids()
    {
        using var temp = new TempDir();
        string cliRoot = temp.Dir("cli");
        string editorRoot = temp.Dir("editor");
        long taskTs = Base.ToUnixTimeMilliseconds();
        long sessionTs = Base.AddMinutes(1).ToUnixTimeMilliseconds();
        WriteUiMessages(cliRoot, "task1",
            ApiReq(taskTs, Tokens(5, 1), ModelInfo("openai-codex", "gpt-5.3-codex")));
        WriteSession(editorRoot, "sess1",
            SessionMessages(Assistant("m1", sessionTs, Metrics(9, 4), SessionModelInfo("cline-pass", "cline-pass/kimi-k3"))),
            null);

        var events = await CollectAsync(
            new ClineTaskHistoryTokenSource([cliRoot, editorRoot]).ReadAsync(Account(), null, default));

        Assert.Equal(2, events.Count);
        Assert.Equal(2, events.Select(e => e.SourceEventId).Distinct().Count());
        Assert.Contains(events, e => e.SourceEventId == $"cline:task:task1:{taskTs}");
        TokenUsageEvent session = Assert.Single(events, e => e.SourceEventId == "cline:session:sess1:m1");
        // No sibling metadata: project attribution degrades, never the event.
        Assert.Equal(ProjectIdentity.Unknown, session.Project);
        Assert.Equal(9, session.InputTokens);
        Assert.Equal(4, session.OutputTokens);
    }

    [Fact]
    public async Task Locked_and_half_written_files_are_skipped_without_failing_the_scan()
    {
        using var temp = new TempDir();
        long ts = Base.ToUnixTimeMilliseconds();
        string brokenFile = WriteUiMessages(temp.Path, "a-broken", ApiReq(ts, Tokens(999, 999)));
        await File.WriteAllTextAsync(brokenFile, "{not json");
        WriteUiMessages(temp.Path, "b-good",
            ApiReq(ts, Tokens(42, 7), ModelInfo("cline-pass", "cline-pass/kimi-k3")));
        string lockedFile = WriteUiMessages(temp.Path, "c-locked", ApiReq(ts, Tokens(999, 999)));
        WriteSession(temp.Path, "z-broken", "{also not json", null);

        await using var lockStream = new FileStream(lockedFile, FileMode.Open, FileAccess.Write, FileShare.None);
        var events = await CollectAsync(
            new ClineTaskHistoryTokenSource([temp.Path]).ReadAsync(Account(), null, default));

        TokenUsageEvent good = Assert.Single(events);
        Assert.Equal(42, good.InputTokens);
        Assert.Equal(7, good.OutputTokens);
        Assert.Equal($"cline:task:b-good:{ts}", good.SourceEventId);
    }
    private static string Normalize(string path) => new ProjectIdentityResolver().Resolve(path).ProjectPath;

    private static ProviderAccount Account() => new(
        new AccountKey(new ProviderId("cline"), "default"), "Cline", null, "fixture", 1, true);

    private static string Say(long ts, string say, string text) =>
        JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["ts"] = ts, ["type"] = "say", ["say"] = say, ["text"] = text
        });

    private static string ApiReq(long ts, string payload, string? modelInfo = null)
    {
        var entry = new Dictionary<string, object?>
        {
            ["ts"] = ts,
            ["type"] = "say",
            ["say"] = "api_req_started",
            ["text"] = payload
        };
        if (modelInfo is not null) entry["modelInfo"] = JsonSerializer.Deserialize<JsonElement>(modelInfo);
        return JsonSerializer.Serialize(entry);
    }

    private static string Tokens(long input, long output, long cacheReads = 0, long cacheWrites = 0) =>
        "{\"request\":\"x\",\"tokensIn\":" + input + ",\"tokensOut\":" + output +
        ",\"cacheReads\":" + cacheReads + ",\"cacheWrites\":" + cacheWrites + ",\"cost\":0}";

    private static string ModelInfo(string providerId, string modelId) =>
        "{\"providerId\":\"" + providerId + "\",\"modelId\":\"" + modelId + "\",\"mode\":\"act\"}";

    private static string SessionMessages(params string[] messages) =>
        "{\"version\":1,\"sessionId\":\"s\",\"updated_at\":1,\"agent\":\"cline\",\"messages\":[" +
        string.Join(",", messages) + "]}";

    private static string User(long ts) =>
        "{\"id\":\"u" + ts + "\",\"role\":\"user\",\"ts\":" + ts + ",\"content\":[]}";

    private static string Assistant(string? id, long ts, string? metrics, string? modelInfo = null)
    {
        var message = new Dictionary<string, object?>
        {
            ["role"] = "assistant",
            ["ts"] = ts,
            ["content"] = Array.Empty<object>()
        };
        if (id is not null) message["id"] = id;
        if (modelInfo is not null) message["modelInfo"] = JsonSerializer.Deserialize<JsonElement>(modelInfo);
        if (metrics is not null) message["metrics"] = JsonSerializer.Deserialize<JsonElement>(metrics);
        return JsonSerializer.Serialize(message);
    }

    private static string Metrics(long input, long output, long cacheRead = 0, long cacheWrite = 0) =>
        "{\"inputTokens\":" + input + ",\"outputTokens\":" + output +
        ",\"cacheReadTokens\":" + cacheRead + ",\"cacheWriteTokens\":" + cacheWrite + ",\"cost\":0.01}";

    private static string SessionModelInfo(string provider, string id) =>
        "{\"id\":\"" + id + "\",\"provider\":\"" + provider + "\"}";

    private static string Sibling(string? cwd = null, string? workspaceRoot = null)
    {
        var sibling = new Dictionary<string, object?> { ["provider"] = "cline-pass", ["model"] = "cline-pass/kimi-k3" };
        if (cwd is not null) sibling["cwd"] = cwd;
        if (workspaceRoot is not null) sibling["workspace_root"] = workspaceRoot;
        return JsonSerializer.Serialize(sibling);
    }

    private static void WriteTaskHistory(string root, params (string Id, string? Cwd, string? ModelId)[] tasks)
    {
        Directory.CreateDirectory(Path.Combine(root, "state"));
        var rows = tasks.Select(task =>
        {
            var row = new Dictionary<string, object?> { ["id"] = task.Id };
            if (task.Cwd is not null) row["cwdOnTaskInitialization"] = task.Cwd;
            if (task.ModelId is not null) row["modelId"] = task.ModelId;
            return row;
        });
        File.WriteAllText(Path.Combine(root, "state", "taskHistory.json"), JsonSerializer.Serialize(rows));
    }

    private static string WriteUiMessages(string root, string taskId, params string[] entries)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "tasks", taskId)).FullName;
        string file = Path.Combine(directory, "ui_messages.json");
        File.WriteAllText(file, "[" + string.Join(",", entries) + "]");
        return file;
    }

    private static void WriteSession(string root, string sessionId, string messagesJson, string? siblingJson)
    {
        string directory = Directory.CreateDirectory(Path.Combine(root, "sessions", sessionId)).FullName;
        File.WriteAllText(Path.Combine(directory, sessionId + ".messages.json"), messagesJson);
        if (siblingJson is not null)
        {
            File.WriteAllText(Path.Combine(directory, sessionId + ".json"), siblingJson);
        }
    }

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (T item in source) items.Add(item);
        return items;
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Dir(string relative) => Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}