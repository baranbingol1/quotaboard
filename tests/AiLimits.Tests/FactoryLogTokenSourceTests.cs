// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Droid;

namespace AiLimits.Tests;

public sealed class FactoryLogTokenSourceTests
{
    private static readonly DateTimeOffset FirstTimestamp = new(2026, 7, 14, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ReadsOnlyStructuredStreamingResultsAndKeepsTokenLanesSeparate()
    {
        using var temp = new TemporaryDirectory();
        var projectPath = Directory.CreateDirectory(Path.Combine(temp.Path, "projects", "factory-demo")).FullName;
        await WriteSessionIndexAsync(temp.Path, "session-1", projectPath);
        var logDirectory = Directory.CreateDirectory(Path.Combine(temp.Path, "logs"));
        var valid = StreamingLine(FirstTimestamp, input: 101, totalInput: 141, cacheRead: 40, output: 23, reasoning: 7);
        await File.WriteAllLinesAsync(
            Path.Combine(logDirectory.FullName, "droid-log-single.log"),
            [
                "ordinary log text that must be ignored",
                $"[{FirstTimestamp:O}] [Session] Saving session settings | Context: {{\"tokenUsage\":{{\"inputTokens\":99999}}}}",
                $"[{FirstTimestamp:O}] [Agent] Streaming result | Context: not-json",
                valid,
            ]
        );

        var source = new FactoryLogTokenSource(temp.Path);
        var usage = Assert.Single(await CollectAsync(source.ReadAsync(Account(), null, default)));

        Assert.Equal("factory", usage.Service.Value);
        Assert.Equal("claude-sonnet-4", usage.RawModelId);
        Assert.Equal(101, usage.InputTokens);
        Assert.Equal(40, usage.CacheReadTokens);
        // Anthropic outputTokens include the reasoning tokens; the source must
        // hand the pricing engine disjoint lanes (23 - 7).
        Assert.Equal(16, usage.OutputTokens);
        Assert.Equal(7, usage.ReasoningTokens);
        Assert.Equal(0, usage.CacheWriteTokens);
        Assert.Equal(projectPath, usage.Project.ProjectPath);
        Assert.StartsWith("factory:trace-1:span-1:", usage.SourceEventId, StringComparison.Ordinal);
        Assert.DoesNotContain("ordinary log text", usage.SourceEventId, StringComparison.Ordinal);
    }

    [Theory]
    // Anthropic/OpenAI report reasoning inside outputTokens: subtract.
    [InlineData("claude-opus-4-8", 100, 40, 60)]
    [InlineData("gpt-5.5", 100, 40, 60)]
    // MiniMax reports the lanes disjointly (observed reasoning > output in
    // real droid logs): keep outputTokens as written.
    [InlineData("minimax-m3", 100, 40, 100)]
    // Unknown/custom models cannot be classified: keep the raw lanes.
    [InlineData("custom:GPT-5.5-Proxy", 100, 40, 100)]
    public void ReasoningLaneIsMadeDisjointOnlyForVendorsThatNestItInOutput(
        string modelId,
        long output,
        long reasoning,
        long expectedOutput
    )
    {
        var line = StreamingLine(
            FirstTimestamp,
            input: 10,
            totalInput: 10,
            cacheRead: 0,
            output: output,
            reasoning: reasoning,
            modelId: modelId
        );

        Assert.True(FactoryLogTokenSource.TryParseLine(line, out var usage));

        Assert.Equal(expectedOutput, usage.OutputTokens);
        Assert.Equal(reasoning, usage.ReasoningTokens);
    }

    [Fact]
    public async Task CursorSkipsEventsOlderThanTheRescanOverlapButReplaysTheWindow()
    {
        using var temp = new TemporaryDirectory();
        var logs = Directory.CreateDirectory(Path.Combine(temp.Path, "logs"));
        await File.WriteAllLinesAsync(
            Path.Combine(logs.FullName, "droid-log-single.log"),
            [
                // Well behind the overlap window: covered by the previous scan.
                StreamingLine(
                    FirstTimestamp.AddMinutes(-10),
                    input: 10,
                    totalInput: 12,
                    cacheRead: 2,
                    output: 3,
                    reasoning: 1
                ),
                // Inside the overlap window behind the cursor: replayed so a
                // same-timestamp late write can never be lost (fingerprints dedupe).
                StreamingLine(FirstTimestamp, input: 15, totalInput: 15, cacheRead: 0, output: 4, reasoning: 1),
                StreamingLine(
                    FirstTimestamp.AddMinutes(1),
                    input: 20,
                    totalInput: 24,
                    cacheRead: 4,
                    output: 6,
                    reasoning: 2
                ),
            ]
        );
        var source = new FactoryLogTokenSource(temp.Path);
        var cursor = new ScannerCursor(source.Id, null, FirstTimestamp, null);

        var usage = await CollectAsync(source.ReadAsync(Account(), cursor, default));

        Assert.Equal(2, usage.Count);
        Assert.Equal(FirstTimestamp, usage[0].OccurredAt);
        Assert.Equal(FirstTimestamp.AddMinutes(1), usage[1].OccurredAt);
        Assert.Equal(20, usage[1].InputTokens);
    }

    [Fact]
    public void EventIdentityIsStableAcrossRotatedCopiesButDoesNotCollapseSeparateResults()
    {
        var first = StreamingLine(FirstTimestamp, input: 10, totalInput: 12, cacheRead: 2, output: 3, reasoning: 1);
        var later = StreamingLine(
            FirstTimestamp.AddSeconds(1),
            input: 10,
            totalInput: 12,
            cacheRead: 2,
            output: 3,
            reasoning: 1
        );

        Assert.True(FactoryLogTokenSource.TryParseLine(first, out var original));
        Assert.True(FactoryLogTokenSource.TryParseLine(first, out var rotatedCopy));
        Assert.True(FactoryLogTokenSource.TryParseLine(later, out var separateResult));

        Assert.Equal(original.SourceEventId, rotatedCopy.SourceEventId);
        Assert.NotEqual(original.SourceEventId, separateResult.SourceEventId);
        Assert.StartsWith("factory:trace-1:span-1:", original.SourceEventId, StringComparison.Ordinal);
    }

    private static string StreamingLine(
        DateTimeOffset timestamp,
        long input,
        long totalInput,
        long cacheRead,
        long output,
        long reasoning,
        string modelId = "claude-sonnet-4"
    )
    {
        var context = JsonSerializer.Serialize(
            new
            {
                inputTokens = input,
                totalInputTokens = totalInput,
                cacheReadInputTokens = cacheRead,
                outputTokens = output,
                reasoningTokens = reasoning,
                count = 2,
                contextCount = 8,
                tags = new
                {
                    traceId = "trace-1",
                    spanId = "span-1",
                    sessionId = "session-1",
                    modelId,
                },
            }
        );
        return $"[{timestamp:O}] [INFO] [Agent] Streaming result | Context: {context}";
    }

    private static async Task WriteSessionIndexAsync(string factoryHome, string sessionId, string cwd)
    {
        var index = JsonSerializer.Serialize(new { version = 1, entries = new[] { new { sessionId, cwd } } });
        await File.WriteAllTextAsync(Path.Combine(factoryHome, "sessions-index.json"), index);
    }

    private static ProviderAccount Account() =>
        new(new AccountKey(new ProviderId("droid"), "default"), "Factory / Droid", null, "fixture", 1, true);

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source)
            items.Add(item);
        return items;
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
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
