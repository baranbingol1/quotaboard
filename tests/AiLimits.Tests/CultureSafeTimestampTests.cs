// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Amp;
using AiLimits.Infrastructure.Providers.Claude;
using AiLimits.Infrastructure.Providers.Codex;

namespace AiLimits.Tests;

public sealed class CultureSafeTimestampTests
{
    /// <summary>
    /// ar-SA uses the Um-Al-Qura (Hijri) calendar; without InvariantCulture
    /// the default DateTimeOffset.TryParse overload can reject ISO-8601 dates.
    /// </summary>
    private static IDisposable EnterArabicCulture()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("ar-SA");
        return new CultureRestore(original);
    }

    [Fact]
    public void Amp_thread_list_parser_parses_iso8601_under_arabic_culture()
    {
        using (EnterArabicCulture())
        {
            const string list = """
                [{"id":"T-one","updated":"2026-07-19T00:43:36.632Z","title":"One"}]
                """;

            Assert.True(AmpThreadParser.TryParseThreadList(list, out IReadOnlyList<AmpThreadSummary> threads));
            AmpThreadSummary thread = Assert.Single(threads);
            Assert.Equal("T-one", thread.Id);
            Assert.Equal(new DateTimeOffset(2026, 7, 19, 0, 43, 36, 632, TimeSpan.Zero), thread.UpdatedAt);
        }
    }

    [Fact]
    public void Amp_usage_parser_parses_iso8601_under_arabic_culture()
    {
        using (EnterArabicCulture())
        {
            const string export = """
                {"messages":[{"messageId":1,"usage":{"model":"gpt-5","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}
                """;

            Assert.True(AmpThreadParser.TryParseUsage("T-test", export, null, out IReadOnlyList<AmpThreadUsage> parsed));
            AmpThreadUsage usage = Assert.Single(parsed);
            Assert.Equal(new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero), usage.OccurredAt);
        }
    }

    [Fact]
    public async Task Claude_jsonl_source_parses_iso8601_under_arabic_culture()
    {
        using var temp = new TempDir();
        using (EnterArabicCulture())
        {
            var projects = Directory.CreateDirectory(Path.Combine(temp.Path, "projects", "demo"));
            var file = Path.Combine(projects.FullName, "chat.jsonl");
            await File.WriteAllLinesAsync(file,
            [
                "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":100,\"output_tokens\":50}}}"
            ]);

            var events = await CollectAsync(
                new ClaudeJsonlTokenSource(temp.Path).ReadAsync(Account("claude"), null, default));

            var evt = Assert.Single(events);
            Assert.Equal(new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero), evt.OccurredAt);
            Assert.Equal(100, evt.InputTokens);
        }
    }

    [Fact]
    public async Task Codex_session_source_parses_iso8601_under_arabic_culture()
    {
        using var temp = new TempDir();
        using (EnterArabicCulture())
        {
            var sessions = Directory.CreateDirectory(Path.Combine(temp.Path, "sessions"));
            var file = Path.Combine(sessions.FullName, "one.jsonl");
            await File.WriteAllLinesAsync(file,
            [
                "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"payload\":{\"model\":\"gpt-5\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":50,\"cached_input_tokens\":20,\"reasoning_output_tokens\":10}}}}"
            ]);

            var events = await CollectAsync(
                new CodexSessionTokenSource(temp.Path).ReadAsync(Account("codex"), null, default));

            var evt = Assert.Single(events);
            Assert.Equal(new DateTimeOffset(2026, 7, 13, 10, 0, 0, TimeSpan.Zero), evt.OccurredAt);
            // InputTokens = raw input (100) - cached input (20) = 80.
            Assert.Equal(80, evt.InputTokens);
        }
    }

    [Fact]
    public async Task Claude_offsetless_timestamp_is_treated_as_utc()
    {
        using var temp = new TempDir();
        var projects = Directory.CreateDirectory(Path.Combine(temp.Path, "projects", "demo"));
        var file = Path.Combine(projects.FullName, "chat.jsonl");
        // No "Z" suffix and no offset — must still parse as UTC.
        await File.WriteAllLinesAsync(file,
        [
            "{\"timestamp\":\"2026-07-13T10:00:00\",\"message\":{\"id\":\"m1\",\"model\":\"c\",\"usage\":{\"input_tokens\":10,\"output_tokens\":5}}}"
        ]);

        var events = await CollectAsync(
            new ClaudeJsonlTokenSource(temp.Path).ReadAsync(Account("claude"), null, default));

        var evt = Assert.Single(events);
        Assert.Equal(TimeSpan.Zero, evt.OccurredAt.Offset);
    }

    [Fact]
    public async Task Codex_offsetless_timestamp_is_treated_as_utc()
    {
        using var temp = new TempDir();
        var sessions = Directory.CreateDirectory(Path.Combine(temp.Path, "sessions"));
        var file = Path.Combine(sessions.FullName, "one.jsonl");
        await File.WriteAllLinesAsync(file,
        [
            "{\"timestamp\":\"2026-07-13T10:00:00\",\"payload\":{\"model\":\"gpt-5\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":50}}}}"
        ]);

        var events = await CollectAsync(
            new CodexSessionTokenSource(temp.Path).ReadAsync(Account("codex"), null, default));

        var evt = Assert.Single(events);
        Assert.Equal(TimeSpan.Zero, evt.OccurredAt.Offset);
    }

    private static ProviderAccount Account(string provider) => new(
        new AccountKey(new ProviderId(provider), "one"), "one", null, "fixture", 1, true);

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items;
    }

    private sealed class CultureRestore(CultureInfo original) : IDisposable
    {
        public void Dispose() => CultureInfo.CurrentCulture = original;
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
            if (Directory.Exists(Path)) Directory.Delete(Path, true);
        }
    }
}
