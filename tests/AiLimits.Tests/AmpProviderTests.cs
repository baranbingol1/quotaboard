// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Amp;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

public sealed class AmpProviderTests
{
    [Fact]
    public void Thread_export_parser_maps_exact_token_lanes_without_double_counting_cached_input()
    {
        const string export = """
            {
              "messages": [
                {
                  "messageId": 2,
                  "protocolMessageID": "M-first",
                  "role": "assistant",
                  "usage": {
                    "model": "gpt-5.6-sol",
                    "timestamp": "2026-07-19T00:44:24.606Z",
                    "inputTokens": 7,
                    "outputTokens": 368,
                    "totalInputTokens": 14504,
                    "cacheReadInputTokens": 12000,
                    "cacheCreationInputTokens": 2497
                  }
                }
              ]
            }
            """;

        Assert.True(AmpThreadParser.TryParseUsage("T-test", export, null, out IReadOnlyList<AmpThreadUsage> parsed));
        AmpThreadUsage usage = Assert.Single(parsed);

        Assert.Equal("gpt-5.6-sol", usage.Model);
        Assert.Equal(7, usage.InputTokens);
        Assert.Equal(368, usage.OutputTokens);
        Assert.Equal(12000, usage.CacheReadTokens);
        Assert.Equal(2497, usage.CacheWriteTokens);
        Assert.Equal("amp:T-test:M-first", usage.SourceEventId);
    }

    [Fact]
    public void Thread_export_parser_orders_events_and_honors_overlap_cutoff()
    {
        const string export = """
            {"messages":[
              {"messageId":3,"usage":{"model":"claude-opus-4.8","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}},
              {"messageId":2,"usage":{"model":"claude-opus-4.8","timestamp":"2026-07-18T12:00:00Z","outputTokens":10}}
            ]}
            """;

        Assert.True(AmpThreadParser.TryParseUsage(
            "T-test",
            export,
            new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero),
            out IReadOnlyList<AmpThreadUsage> usage));

        AmpThreadUsage item = Assert.Single(usage);
        Assert.Equal(20, item.OutputTokens);
        Assert.Equal("amp:T-test:3", item.SourceEventId);
    }

    [Fact]
    public void Thread_list_parser_reads_ids_and_update_times()
    {
        const string list = """
            [{"id":"T-one","updated":"2026-07-19T00:43:36.632Z","title":"One"}]
            """;

        Assert.True(AmpThreadParser.TryParseThreadList(list, out IReadOnlyList<AmpThreadSummary> threads));
        AmpThreadSummary thread = Assert.Single(threads);
        Assert.Equal("T-one", thread.Id);
        Assert.Equal(new DateTimeOffset(2026, 7, 19, 0, 43, 36, 632, TimeSpan.Zero), thread.UpdatedAt);
    }

    [Fact]
    public async Task Thread_source_does_not_emit_or_advance_when_any_selected_export_fails()
    {
        var runner = new SequenceRunner(
            """[{"id":"T-new","updated":"2026-07-19T12:00:00Z"},{"id":"T-old","updated":"2026-07-18T12:00:00Z"}]""",
            """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""",
            "not-json");
        var source = new AmpThreadTokenSource(runner, () => "amp-test");
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("amp"), "one"),
            "Amp",
            null,
            "fixture",
            1,
            true);
        var emitted = new List<TokenUsageEvent>();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            await foreach (TokenUsageEvent item in source.ReadAsync(account, null, default))
                emitted.Add(item);
        });

        Assert.Empty(emitted);
        Assert.Equal(3, runner.CallCount);
    }

    [Fact]
    public async Task Thread_listing_pages_past_the_first_twenty_threads()
    {
        // Page one is full (20 threads, one updated inside the rescan window),
        // page two is short, so the source must request offsets 0 and 20 and
        // export only the thread the cutoff keeps.
        string pageOne = "[" + string.Join(",", Enumerable.Range(0, 20).Select(index =>
            $$"""{"id":"T-{{index}}","updated":"{{(index == 0 ? "2026-07-19T12:00:00Z" : "2026-07-10T12:00:00Z")}}"}""")) + "]";
        string pageTwo = """[{"id":"T-20","updated":"2026-07-09T12:00:00Z"}]""";
        string export = """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""";
        var runner = new SequenceRunner(pageOne, pageTwo, export);
        var source = new AmpThreadTokenSource(runner, () => "amp-test");
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("amp"), "one"), "Amp", null, "fixture", 1, true);
        var cursor = new ScannerCursor(source.Id, "incremental", new DateTimeOffset(2026, 7, 16, 0, 0, 0, TimeSpan.Zero), null);

        var emitted = new List<TokenUsageEvent>();
        await foreach (TokenUsageEvent item in source.ReadAsync(account, cursor, default))
            emitted.Add(item);

        Assert.Equal(3, runner.CallCount);
        Assert.Contains("0", runner.Calls[0]);
        Assert.Contains("20", runner.Calls[1]);
        Assert.Equal("export", runner.Calls[2][1]);
        var usageEvent = Assert.Single(emitted);
        Assert.Equal("amp:T-0:1", usageEvent.SourceEventId);
    }

    [Fact]
    public void Subscription_usage_creates_agent_and_orb_monthly_meters()
    {
        const string output = """
            Signed in as dev@example.com
            Subscription Megawatt: 73% other usage and 41% orb usage remaining - resets upon renewal in 1 month
            Individual credits: $14.03 remaining (set up auto-reload to avoid running out)
            """;

        Assert.True(AmpParser.TryParse(output, out AmpUsage? usage));

        ProviderSnapshot snapshot = AmpParser.Snapshot(
            new AccountKey(new ProviderId("amp"), "default"),
            usage!,
            new DateTimeOffset(2026, 7, 18, 0, 0, 0, TimeSpan.Zero),
            "test",
            "cli");

        Assert.Collection(
            snapshot.Meters,
            meter =>
            {
                Assert.Equal("amp:agent", meter.Key.Value);
                Assert.Equal("Agent usage", meter.DisplayName);
                Assert.Equal(27, meter.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(30), meter.WindowDuration);
            },
            meter =>
            {
                Assert.Equal("amp:orb", meter.Key.Value);
                Assert.Equal("Orb usage", meter.DisplayName);
                Assert.Equal(59, meter.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(30), meter.WindowDuration);
            });
        Assert.Equal("Megawatt", snapshot.Extensions["plan_type"].GetString());
        Assert.Equal("dev@example.com", snapshot.Extensions["email"].GetString());
    }

    [Fact]
    public void Subscription_parser_accepts_official_agent_usage_label()
    {
        const string output = "Subscription Gigawatt: 99.5% agent usage and 88% orb usage remaining";

        Assert.True(AmpParser.TryParse(output, out AmpUsage? usage));
        Assert.Equal("Gigawatt", usage!.SubscriptionPlan);
        Assert.Equal(99.5, usage.AgentRemainingPercent);
        Assert.Equal(88, usage.OrbRemainingPercent);
    }

    [Fact]
    public void Credit_balances_are_usd_denominated_and_preserve_account_email()
    {
        const string output = """
            Signed in as dev@example.com
            Individual credits: $14.03 remaining (set up auto-reload to avoid running out)
            Workspace credits: $30.00 remaining
            """;

        Assert.True(AmpParser.TryParse(output, out AmpUsage? usage));

        ProviderSnapshot snapshot = AmpParser.Snapshot(
            new AccountKey(new ProviderId("amp"), "default"),
            usage!,
            new DateTimeOffset(2026, 7, 15, 0, 0, 0, TimeSpan.Zero),
            "test",
            "cli");

        Assert.Collection(
            snapshot.Balances,
            balance =>
            {
                Assert.Equal("amp:individual", balance.Key);
                Assert.Equal(14.03m, balance.Value);
                Assert.Equal(MeterUnit.Usd, balance.Unit);
            },
            balance =>
            {
                Assert.Equal("amp:workspace", balance.Key);
                Assert.Equal(30m, balance.Value);
                Assert.Equal(MeterUnit.Usd, balance.Unit);
            });
        Assert.Equal("dev@example.com", snapshot.Extensions["email"].GetString());
    }

    private sealed class SequenceRunner(params string[] outputs) : IProcessRunner
    {
        private int index;
        public int CallCount => index;
        public List<string[]> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(
            string executable,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            string output = outputs[index++];
            return Task.FromResult(new ProcessResult(0, output, string.Empty, TimeSpan.Zero));
        }
    }
}
