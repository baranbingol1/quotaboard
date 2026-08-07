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
    public async Task One_unreadable_thread_does_not_discard_the_threads_that_did_export()
    {
        // Regression: an unparseable export used to throw, so the scan loop
        // caught it and never saved the cursor — the whole scan's work was
        // discarded and repeated on the next refresh, forever.
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-new","updated":"2026-07-19T12:00:00Z"},{"id":"T-old","updated":"2026-07-18T12:00:00Z"}]"""),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}"""),
            ScriptedReply.Ok("not-json"));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        List<TokenUsageEvent> emitted = await DrainAsync(source, cursor: null);

        TokenUsageEvent kept = Assert.Single(emitted);
        Assert.Equal("amp:T-new:1", kept.SourceEventId);
        Assert.Equal(3, runner.CallCount);
        Assert.NotNull(((IScanPositionSource)source).Position);
    }

    [Fact]
    public async Task An_oversized_export_is_skipped_rather_than_failing_the_scan()
    {
        // A real 6.25 MB transcript on this machine tripped the process
        // runner's capture cap. Truncation is a property of that one thread,
        // not of the source.
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-huge","updated":"2026-07-19T12:00:00Z"},{"id":"T-small","updated":"2026-07-19T11:00:00Z"}]"""),
            ScriptedReply.Capped("{\"messages\":[ truncated"),
            ScriptedReply.Ok("""{"messages":[{"messageId":9,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T11:00:00Z","outputTokens":5}}]}"""));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        List<TokenUsageEvent> emitted = await DrainAsync(source, cursor: null);

        TokenUsageEvent kept = Assert.Single(emitted);
        Assert.Equal("amp:T-small:9", kept.SourceEventId);
    }

    [Fact]
    public async Task A_scan_where_every_export_fails_is_reported_as_a_scan_failure()
    {
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            ScriptedReply.Failed("transient export failure"));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        await Assert.ThrowsAsync<TokenScanException>(() => DrainAsync(source, cursor: null));

        string? checkpoint = ((IScanFailureCheckpointSource)source).FailureCheckpoint;
        Assert.Contains(AmpScanState.RetryMarker, checkpoint);
    }

    [Fact]
    public async Task A_source_that_has_started_yielding_exposes_no_failure_checkpoint()
    {
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}"""));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("amp"), "one"), "Amp", null, "fixture", 1, true);

        await using IAsyncEnumerator<TokenUsageEvent> enumerator =
            source.ReadAsync(account, null, default).GetAsyncEnumerator();

        Assert.True(await enumerator.MoveNextAsync());
        Assert.Null(((IScanFailureCheckpointSource)source).FailureCheckpoint);
    }

    [Fact]
    public async Task An_old_pending_retry_bypasses_the_overlap_cutoff()
    {
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-ancient","updated":"2026-01-01T00:00:00Z"}]"""),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-01-01T00:00:00Z","outputTokens":20}}]}"""));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");
        var cursor = new ScannerCursor(
            source.Id,
            """{"T-ancient":"!retry"}""",
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            null);

        List<TokenUsageEvent> emitted = await DrainAsync(source, cursor);

        Assert.Single(emitted);
        Assert.Equal("amp:T-ancient:1", emitted[0].SourceEventId);
        Assert.DoesNotContain(AmpScanState.RetryMarker, ((IScanPositionSource)source).Position);
    }

    [Fact]
    public async Task A_pending_retry_skipped_by_mutable_pagination_is_exported_directly()
    {
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-other","updated":"2026-01-01T00:00:00Z"}]"""),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-01-01T00:00:00Z","outputTokens":20}}]}"""));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");
        var cursor = new ScannerCursor(
            source.Id,
            """{"T-pending":"!retry"}""",
            new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero),
            null);

        List<TokenUsageEvent> emitted = await DrainAsync(source, cursor);

        Assert.Equal("T-pending", runner.Calls[1][2]);
        Assert.Equal("amp:T-pending:1", Assert.Single(emitted).SourceEventId);
        Assert.DoesNotContain(AmpScanState.RetryMarker, ((IScanPositionSource)source).Position);
    }

    [Fact]
    public async Task A_seen_pending_retry_beyond_the_listing_safety_cap_is_exported_directly()
    {
        var replies = new List<ScriptedReply>();
        for (int page = 0; page < 9; page++)
        {
            string listing = "[" + string.Join(",", Enumerable.Range(page * 20, 20).Select(index =>
                $$"""{"id":"T-{{index}}","updated":"2026-01-01T00:00:00Z"}""")) + "]";
            replies.Add(ScriptedReply.Ok(listing));
        }
        // Mutable offset pagination can repeat entries. This page adds only ten
        // new ids, leaving room for one more full page to overflow the cap.
        string duplicatePage = "[" + string.Join(",", Enumerable.Range(0, 10)
            .Concat(Enumerable.Range(180, 10))
            .Select(index => $$"""{"id":"T-{{index}}","updated":"2026-01-01T00:00:00Z"}""")) + "]";
        replies.Add(ScriptedReply.Ok(duplicatePage));
        string overflowPage = "[" + string.Join(",", Enumerable.Range(190, 19)
            .Select(index => $$"""{"id":"T-{{index}}","updated":"2026-01-01T00:00:00Z"}"""))
            + "," + """{"id":"T-pending","updated":"2026-01-01T00:00:00Z"}]""";
        replies.Add(ScriptedReply.Ok(overflowPage));
        replies.Add(ScriptedReply.Ok(
            """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-01-01T00:00:00Z","outputTokens":20}}]}"""));
        var runner = new ScriptedRunner([.. replies]);
        var source = new AmpThreadTokenSource(runner, () => "amp-test");
        var previous = Enumerable.Range(0, 200).ToDictionary(
            index => $"T-{index}",
            _ => "2026-01-01T00:00:00.0000000+00:00",
            StringComparer.Ordinal);
        previous["T-pending"] = AmpScanState.RetryMarker;
        var cursor = new ScannerCursor(
            source.Id,
            AmpScanState.Serialize(previous),
            null,
            null);

        List<TokenUsageEvent> emitted = await DrainAsync(source, cursor);

        Assert.Equal(12, runner.CallCount);
        Assert.Equal("T-pending", runner.Calls[^1][2]);
        Assert.Equal("amp:T-pending:1", Assert.Single(emitted).SourceEventId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_unreadable_export_is_retried_at_the_same_revision(bool truncated)
    {
        const string listing = """[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]""";
        var first = new ScriptedRunner(
            ScriptedReply.Ok(listing),
            truncated ? ScriptedReply.Capped("truncated") : ScriptedReply.Ok("not-json"));
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");

        await Assert.ThrowsAsync<TokenScanException>(() => DrainAsync(firstSource, cursor: null));
        string? pendingPosition = ((IScanPositionSource)firstSource).Position;
        Assert.Contains(AmpScanState.RetryMarker, pendingPosition);

        var second = new ScriptedRunner(
            ScriptedReply.Ok(listing),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}"""));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");

        List<TokenUsageEvent> emitted = await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, pendingPosition, null, null));

        Assert.Single(emitted);
        Assert.DoesNotContain(AmpScanState.RetryMarker, ((IScanPositionSource)secondSource).Position);
    }

    [Fact]
    public async Task A_thread_still_at_its_recorded_revision_is_not_exported_again()
    {
        const string listing = """[{"id":"T-a","updated":"2026-07-19T12:00:00Z"},{"id":"T-b","updated":"2026-07-18T12:00:00Z"}]""";
        const string export = """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""";

        var first = new ScriptedRunner(
            ScriptedReply.Ok(listing), ScriptedReply.Ok(export), ScriptedReply.Ok(export));
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");
        await DrainAsync(firstSource, cursor: null);
        string? position = ((IScanPositionSource)firstSource).Position;

        Assert.Equal(3, first.CallCount);
        Assert.NotNull(position);

        // Same listing, same revisions: the second scan must cost one listing
        // call and nothing else. This is what takes the steady-state scan from
        // 35 subprocess launches down to zero.
        var second = new ScriptedRunner(ScriptedReply.Ok(listing));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");
        List<TokenUsageEvent> emitted = await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, position, null, null));

        Assert.Empty(emitted);
        Assert.Equal(1, second.CallCount);
        Assert.Equal("list", second.Calls[0][1]);
    }

    [Fact]
    public async Task A_changed_revision_is_exported_again()
    {
        const string export = """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""";
        var first = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            ScriptedReply.Ok(export));
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");
        await DrainAsync(firstSource, cursor: null);
        string? position = ((IScanPositionSource)firstSource).Position;

        // Same thread id, newer `updated` — Amp appended to the conversation.
        var second = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-20T12:00:00Z"}]"""),
            ScriptedReply.Ok(export));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");
        List<TokenUsageEvent> emitted = await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, position, null, null));

        Assert.Single(emitted);
        Assert.Equal(2, second.CallCount);
        Assert.Equal("export", second.Calls[1][1]);
    }

    [Fact]
    public async Task A_failure_next_to_already_covered_threads_does_not_fail_the_scan()
    {
        const string export = """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""";
        var first = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            ScriptedReply.Ok(export));
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");
        await DrainAsync(firstSource, cursor: null);
        string? position = ((IScanPositionSource)firstSource).Position;

        // T-a is already covered, T-b is new and unreadable. Nothing exported,
        // but the scan still knows 1 of 2 threads is accounted for, so it must
        // not report itself as a total failure — that would cost the cursor.
        var second = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"},{"id":"T-b","updated":"2026-07-19T13:00:00Z"}]"""),
            ScriptedReply.Ok("not-json"));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");

        List<TokenUsageEvent> emitted = await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, position, null, null));

        Assert.Empty(emitted);
        Assert.NotNull(((IScanPositionSource)secondSource).Position);
    }

    [Fact]
    public async Task A_thread_outside_the_cutoff_keeps_its_recorded_revision()
    {
        // The watermark must cover every listed thread, not just the ones the
        // cutoff selects; otherwise the next scan cannot recognise a page as
        // fully covered and pages through the whole listing again.
        const string export = """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""";
        var first = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-new","updated":"2026-07-19T12:00:00Z"},{"id":"T-ancient","updated":"2026-01-01T00:00:00Z"}]"""),
            ScriptedReply.Ok(export),
            ScriptedReply.Ok(export));
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");
        await DrainAsync(firstSource, cursor: null);
        string? position = ((IScanPositionSource)firstSource).Position;

        // Now rescan with a cutoff that excludes T-ancient entirely.
        var second = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-new","updated":"2026-07-19T12:00:00Z"},{"id":"T-ancient","updated":"2026-01-01T00:00:00Z"}]"""));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");
        await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, position, new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero), null));

        Assert.Contains("T-ancient", ((IScanPositionSource)secondSource).Position);
        Assert.Equal(1, second.CallCount);
    }

    [Fact]
    public async Task A_failed_process_is_retried_next_scan_rather_than_written_off()
    {
        // A CLI that exits non-zero prints a diagnostic, not JSON, so it also
        // fails to parse. It must still be treated as transient — recording it
        // would skip that thread until Amp happened to touch it again.
        var first = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            new ScriptedReply("amp: network unreachable", 1, false));
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");

        await Assert.ThrowsAsync<TokenScanException>(() => DrainAsync(firstSource, cursor: null));
        string? position = ((IScanPositionSource)firstSource).Position;
        // Recorded as owing a retry, never as a revision: the marker must not
        // compare equal to any real `updated`, or the thread would be skipped.
        Assert.Contains("T-a", position);
        Assert.Contains(AmpScanState.RetryMarker, position);
        // The revision rides along inside the marker so that new content can
        // grant a fresh retry budget. What must never happen is the thread
        // being recorded *as* that revision, which would compare equal and skip
        // it — so the check is on the recorded value, not the whole document.
        Assert.DoesNotContain("\"T-a\":\"2026-07-19T12:00:00", position);

        var second = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}"""));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");
        List<TokenUsageEvent> emitted = await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, position, null, null));

        Assert.Single(emitted);
        Assert.Equal("export", second.Calls[1][1]);
        string? recoveredPosition = ((IScanPositionSource)secondSource).Position;
        Assert.DoesNotContain(AmpScanState.RetryMarker, recoveredPosition);
        Assert.Contains("2026-07-19T12:00:00", recoveredPosition);
    }

    [Fact]
    public async Task An_outstanding_retry_keeps_the_listing_paging_past_a_covered_page()
    {
        // The "page fully covered" shortcut would otherwise stop at page one and
        // never offer the failed thread again, losing its usage until Amp
        // happened to touch it.
        string fullPage = "[" + string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{"id":"T-{{i}}","updated":"2026-07-19T12:00:00Z"}""")) + "]";
        const string export = """{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}""";

        // First scan: every thread on page one exports, the one on page two
        // fails at the process level.
        var replies = new List<ScriptedReply> { ScriptedReply.Ok(fullPage) };
        replies.Add(ScriptedReply.Ok("""[{"id":"T-late","updated":"2026-07-19T11:00:00Z"}]"""));
        for (int i = 0; i < 20; i++) replies.Add(ScriptedReply.Ok(export));
        replies.Add(new ScriptedReply("amp: transient", 1, false));

        var first = new ScriptedRunner(replies.ToArray());
        var firstSource = new AmpThreadTokenSource(first, () => "amp-test");
        await DrainAsync(firstSource, cursor: null);
        string? position = ((IScanPositionSource)firstSource).Position;
        Assert.Contains("T-late", position);

        // Second scan: page one is entirely covered, but T-late still owes a
        // retry, so the listing must go on to page two and re-attempt it.
        var second = new ScriptedRunner(
            ScriptedReply.Ok(fullPage),
            ScriptedReply.Ok("""[{"id":"T-late","updated":"2026-07-19T11:00:00Z"}]"""),
            ScriptedReply.Ok(export));
        var secondSource = new AmpThreadTokenSource(second, () => "amp-test");
        List<TokenUsageEvent> emitted = await DrainAsync(
            secondSource,
            new ScannerCursor(secondSource.Id, position, null, null));

        Assert.Single(emitted);
        Assert.Equal(3, second.CallCount);
        Assert.Equal("export", second.Calls[2][1]);
        Assert.Equal("T-late", second.Calls[2][2]);
    }

    [Fact]
    public async Task A_legacy_incremental_position_falls_back_to_a_full_rescan()
    {
        // Cursors written before per-thread tracking hold the literal string
        // "incremental"; it must not be read as a watermark.
        var runner = new ScriptedRunner(
            ScriptedReply.Ok("""[{"id":"T-a","updated":"2026-07-19T12:00:00Z"}]"""),
            ScriptedReply.Ok("""{"messages":[{"messageId":1,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-07-19T12:00:00Z","outputTokens":20}}]}"""));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        List<TokenUsageEvent> emitted = await DrainAsync(
            source,
            new ScannerCursor(source.Id, "incremental", null, null));

        Assert.Single(emitted);
        Assert.Equal(2, runner.CallCount);
    }

    [Fact]
    public async Task A_thread_that_can_never_be_parsed_stops_being_retried()
    {
        // Amp wrote `usage` without a `model` before mid-2026 and the parser
        // rejects those, so the export succeeds and is unreadable every single
        // time. Left unbounded this pins HasPendingRetries true forever, which
        // suspends both listing shortcuts on every future scan.
        const string listing = """
            [{"id":"T-old","updated":"2026-06-09T00:48:32Z"},{"id":"T-new","updated":"2026-06-09T01:00:00Z"}]
            """;
        const string modelless = """
            {"messages":[{"messageId":1,"usage":{"timestamp":"2026-06-09T00:48:32.566Z","outputTokens":55,"cacheCreationInputTokens":14669}}]}
            """;
        const string good = """
            {"messages":[{"messageId":2,"usage":{"model":"gpt-5.6-sol","timestamp":"2026-06-09T01:00:00Z","outputTokens":20}}]}
            """;

        string? position = null;
        int exportsOfOldThread = 0;
        for (int scan = 0; scan < 6; scan++)
        {
            var replies = new List<ScriptedReply> { ScriptedReply.Ok(listing) };
            // T-old is offered first and always fails; T-new exports on scan one
            // and is unchanged afterwards.
            replies.Add(ScriptedReply.Ok(modelless));
            replies.Add(ScriptedReply.Ok(good));
            var runner = new ScriptedRunner([.. replies]);
            var source = new AmpThreadTokenSource(runner, () => "amp-test");

            await DrainAsync(source, new ScannerCursor(source.Id, position, null, null));
            position = ((IScanPositionSource)source).Position;
            exportsOfOldThread += runner.Calls.Count(call => call is ["threads", "export", "T-old"]);
        }

        // Three attempts, then written off — not retried on every scan forever.
        Assert.Equal(3, exportsOfOldThread);
        Assert.DoesNotContain(AmpScanState.RetryMarker, position);
        // Written off, but never as a revision: an export we could not read must
        // not masquerade as successfully ingested.
        Assert.Contains("T-old", position);
        Assert.DoesNotContain("\"T-old\":\"2026-06-09T00:48:32", position);
    }

    [Fact]
    public async Task A_spent_retry_lets_the_listing_shortcut_resume()
    {
        // The cost of an unbounded retry is not the failed export itself, it is
        // that HasPendingRetries suspends the "page fully covered" shortcut, so
        // every refresh pages the whole listing instead of one page.
        string fullPage = "[" + string.Join(",", Enumerable.Range(0, 20).Select(i =>
            $$"""{"id":"T-{{i}}","updated":"2026-07-19T12:00:00Z"}""")) + "]";
        var previous = Enumerable.Range(0, 20).ToDictionary(
            i => $"T-{i}",
            _ => "2026-07-19T12:00:00.0000000+00:00",
            StringComparer.Ordinal);
        // T-0 already spent its budget at exactly this revision.
        previous["T-0"] = AmpScanState.GaveUp("2026-07-19T12:00:00.0000000+00:00");

        var runner = new ScriptedRunner(ScriptedReply.Ok(fullPage));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        await DrainAsync(source, new ScannerCursor(source.Id, AmpScanState.Serialize(previous), null, null));

        // One listing call, no exports: the shortcut is back.
        Assert.Equal(1, runner.CallCount);
    }

    [Fact]
    public async Task A_direct_retry_failure_alone_does_not_fail_the_whole_scan()
    {
        // The listed window is empty (everything is older than the cutoff) and
        // the only work is a known-bad thread the listing no longer offers.
        // That must stay a quiet no-op, not a provider-wide failure.
        const string listing = """[{"id":"T-listed","updated":"2026-01-01T00:00:00Z"}]""";
        var previous = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["T-listed"] = "2026-01-01T00:00:00.0000000+00:00",
            ["T-gone"] = AmpScanState.RetryMarker,
        };
        var runner = new ScriptedRunner(
            ScriptedReply.Ok(listing),
            ScriptedReply.Failed("amp: Failed to export thread"));
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        List<TokenUsageEvent> emitted = await DrainAsync(
            source,
            new ScannerCursor(source.Id, AmpScanState.Serialize(previous), new DateTimeOffset(2026, 7, 19, 0, 0, 0, TimeSpan.Zero), null));

        Assert.Empty(emitted);
        Assert.Equal("T-gone", runner.Calls[^1][2]);
    }

    [Fact]
    public async Task The_direct_retry_cap_preserves_unattempted_retry_state()
    {
        const string revision = "2026-06-09T00:48:32.0000000+00:00";
        var previous = Enumerable.Range(0, 21).ToDictionary(
            index => $"T-{index:D2}",
            _ => AmpScanState.Retry(2, revision),
            StringComparer.Ordinal);
        var replies = new List<ScriptedReply> { ScriptedReply.Ok("[]") };
        replies.AddRange(Enumerable.Repeat(ScriptedReply.Failed("amp: Failed to export thread"), 20));
        var runner = new ScriptedRunner([.. replies]);
        var source = new AmpThreadTokenSource(runner, () => "amp-test");

        await DrainAsync(source, new ScannerCursor(source.Id, AmpScanState.Serialize(previous), null, null));

        Assert.Equal(21, runner.CallCount);
        Assert.DoesNotContain(runner.Calls, call => call is ["threads", "export", "T-20"]);
        using JsonDocument position = JsonDocument.Parse(((IScanPositionSource)source).Position!);
        Assert.Equal(
            AmpScanState.Retry(2, revision),
            position.RootElement.GetProperty("T-20").GetString());
    }

    private static async Task<List<TokenUsageEvent>> DrainAsync(AmpThreadTokenSource source, ScannerCursor? cursor)
    {
        var account = new ProviderAccount(
            new AccountKey(new ProviderId("amp"), "one"), "Amp", null, "fixture", 1, true);
        var emitted = new List<TokenUsageEvent>();
        await foreach (TokenUsageEvent item in source.ReadAsync(account, cursor, default))
            emitted.Add(item);
        return emitted;
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

    private sealed record ScriptedReply(string Output, int ExitCode, bool Truncated)
    {
        public static ScriptedReply Ok(string output) => new(output, 0, false);

        public static ScriptedReply Failed(string output) => new(output, 1, false);

        public static ScriptedReply Capped(string output) => new(output, 0, true);
    }

    /// <summary>
    /// Like <see cref="SequenceRunner"/> but able to reproduce a capture-capped
    /// (truncated) or non-zero-exit reply, which is what the per-thread failure
    /// handling turns on. Only the four-argument overload is implemented; the
    /// interface's default routes the capped call here.
    /// </summary>
    private sealed class ScriptedRunner(params ScriptedReply[] replies) : IProcessRunner
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
            Assert.True(index < replies.Length,
                $"Unexpected process call #{index + 1}: {string.Join(' ', arguments)}");
            ScriptedReply reply = replies[index++];
            return Task.FromResult(new ProcessResult(
                reply.ExitCode, reply.Output, string.Empty, TimeSpan.Zero, reply.Truncated));
        }
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
