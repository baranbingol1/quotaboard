// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Claude;

namespace AiLimits.Tests;

public sealed class ClaudeLockedFileTests
{
    [Fact]
    public async Task LockedFileIsSkippedAndOtherFilesStillYieldEvents()
    {
        using var temp = new TempDir();
        var projects = Directory.CreateDirectory(Path.Combine(temp.Path, "projects", "demo"));

        // Good file: two events.
        var goodFile = Path.Combine(projects.FullName, "good.jsonl");
        await File.WriteAllLinesAsync(
            goodFile,
            [
                "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}",
                "{\"timestamp\":\"2026-07-13T10:01:00Z\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":30,\"output_tokens\":20}}}",
            ]
        );

        // Locked file: held with an exclusive write lock so FileShare.ReadWrite
        // cannot open it (actually FileShare.None to simulate Claude's lock).
        var lockedFile = Path.Combine(projects.FullName, "locked.jsonl");
        await File.WriteAllTextAsync(
            lockedFile,
            "{\"timestamp\":\"2026-07-13T11:00:00Z\",\"message\":{\"id\":\"x\",\"model\":\"c\",\"usage\":{\"input_tokens\":999}}}"
        );

        // The name "locked.jsonl" sorts after "good.jsonl" so the source visits
        // the good file first; the locked file is simply skipped.
        await using var lockedStream = new FileStream(lockedFile, FileMode.Open, FileAccess.Write, FileShare.None);

        var events = await CollectAsync(
            new ClaudeJsonlTokenSource(temp.Path).ReadAsync(Account("claude"), null, default)
        );

        Assert.Equal(2, events.Count);
        Assert.All(events, e => Assert.NotEqual(999, e.InputTokens));
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
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
