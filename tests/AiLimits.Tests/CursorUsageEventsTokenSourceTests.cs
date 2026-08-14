// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers;
using AiLimits.Infrastructure.Providers.Cursor;

namespace AiLimits.Tests;

public sealed class CursorUsageEventsTokenSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task MapsUsageEventTokensAndSendsFirstPartyOrigin()
    {
        var handler = new PagedHandler(
            Page(
                Event(Now.AddMinutes(-1), "claude-opus-4-8-thinking-xhigh", input: 2, output: 1576, cacheWrite: 112622),
                Event(Now.AddMinutes(-2), "composer-2.5-fast", input: 111020, output: 8686, cacheRead: 302784)
            )
        );

        List<TokenUsageEvent> events = await ReadAllAsync(handler);

        Assert.Equal(2, events.Count);
        // Ascending: the older composer event first.
        Assert.Equal("composer-2.5-fast", events[0].RawModelId);
        Assert.Equal(111020, events[0].InputTokens);
        Assert.Equal(302784, events[0].CacheReadTokens);
        Assert.Equal("claude-opus-4-8-thinking-xhigh", events[1].RawModelId);
        Assert.Equal(1576, events[1].OutputTokens);
        Assert.Equal(112622, events[1].CacheWriteTokens);
        Assert.All(
            events,
            item =>
            {
                Assert.Equal("cursor", item.Service.Value);
                Assert.Equal(ProjectIdentity.Unknown, item.Project);
                Assert.Equal(0, item.ReasoningTokens);
                Assert.StartsWith("cursor:", item.SourceEventId, StringComparison.Ordinal);
            }
        );

        PagedHandler.CapturedRequest request = Assert.Single(handler.Requests);
        Assert.Equal("https://cursor.com", request.Origin);
        Assert.StartsWith("WorkosCursorSessionToken=user-123%3A%3A", request.Cookie);
        Assert.Contains("\"page\":1", request.Body, StringComparison.Ordinal);
        Assert.Contains("\"pageSize\":100", request.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PaginatesUntilShortPageAndEmitsStrictlyAscending()
    {
        string[] fullPage = Enumerable
            .Range(0, 100)
            .Select(index => Event(Now.AddMinutes(-1 - index), "model-a", input: index + 1))
            .ToArray();
        string[] shortPage = [Event(Now.AddMinutes(-200), "model-b", input: 7)];
        var handler = new PagedHandler(Page(fullPage), Page(shortPage));

        List<TokenUsageEvent> events = await ReadAllAsync(handler);

        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(101, events.Count);
        Assert.Equal("model-b", events[0].RawModelId);
        for (int index = 1; index < events.Count; index++)
        {
            Assert.True(events[index].OccurredAt > events[index - 1].OccurredAt);
        }
    }

    [Fact]
    public async Task StopsAtCursorBoundaryButStillEmitsBoundaryTimestampEvents()
    {
        // Events sharing the boundary's exact millisecond may have been written
        // after the scan that persisted it, so they are re-collected (the
        // fingerprint ledger drops re-seen ones); only strictly older events
        // stop the scan.
        DateTimeOffset boundary = Now.AddMinutes(-30);
        var handler = new PagedHandler(
            Page(
                Event(Now.AddMinutes(-10), "new-1", input: 1),
                Event(Now.AddMinutes(-20), "new-2", input: 2),
                Event(boundary, "at-boundary", input: 3),
                Event(Now.AddMinutes(-40), "older", input: 4)
            )
        );

        List<TokenUsageEvent> events = await ReadAllAsync(
            handler,
            cursor: new ScannerCursor("cursor.usage-events", "incremental", boundary, null)
        );

        Assert.Single(handler.Requests);
        Assert.Equal(["at-boundary", "new-2", "new-1"], events.Select(item => item.RawModelId).ToArray());
    }

    [Fact]
    public async Task AuthFailureIsReportedRatherThanLookingLikeAnEmptyScan()
    {
        var handler = new PagedHandler();
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.Unauthorized));

        // Silently yielding nothing here is what let a signed-out Cursor
        // account be counted as a healthy scan for as long as it lasted.
        TokenScanException failure = await Assert.ThrowsAsync<TokenScanException>(() => ReadAllAsync(handler));

        Assert.Contains("Cursor", failure.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MidScanFailureYieldsNothingSoTheCursorCannotSkipAGap()
    {
        string[] fullPage = Enumerable
            .Range(0, 100)
            .Select(index => Event(Now.AddMinutes(-1 - index), "model-a", input: index + 1))
            .ToArray();
        var handler = new PagedHandler(Page(fullPage));
        handler.Enqueue(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        List<TokenUsageEvent> emitted = [];

        await Assert.ThrowsAsync<TokenScanException>(() => ReadAllAsync(handler, into: emitted));

        Assert.Equal(2, handler.Requests.Count);
        // The all-or-nothing guarantee still holds: the first page succeeded,
        // but nothing reaches the caller, so the cursor cannot advance past
        // the gap the second page left.
        Assert.Empty(emitted);
    }

    [Fact]
    public async Task AnUnrecognizedPayloadIsReportedRatherThanTreatedAsEmpty()
    {
        var handler = new PagedHandler();
        handler.Enqueue(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"somethingElse":[]}""", Encoding.UTF8, "application/json"),
            }
        );

        await Assert.ThrowsAsync<TokenScanException>(() => ReadAllAsync(handler));
    }

    [Fact]
    public async Task MalformedEventsAreSkippedWithoutFailingTheBatch()
    {
        var handler = new PagedHandler(
            Page(
                Event(Now.AddMinutes(-1), "good", input: 10),
                """{"model":"no-token-usage","timestamp":"1784000000000"}""",
                """{"model":"no-timestamp","tokenUsage":{"inputTokens":5}}""",
                """{"model":"zero-tokens","timestamp":"1784000000001","tokenUsage":{}}""",
                Event(Now.AddMinutes(-2), "also-good", output: 20)
            )
        );

        List<TokenUsageEvent> events = await ReadAllAsync(handler);

        Assert.Equal(["also-good", "good"], events.Select(item => item.RawModelId).ToArray());
    }

    [Fact]
    public async Task SourceEventIdsAreStableAcrossReads()
    {
        string page = Page(
            Event(Now.AddMinutes(-1), "model-a", input: 5, output: 6),
            Event(Now.AddMinutes(-2), "model-b", cacheRead: 7)
        );

        List<TokenUsageEvent> first = await ReadAllAsync(new PagedHandler(page));
        List<TokenUsageEvent> second = await ReadAllAsync(new PagedHandler(page));

        Assert.Equal(first.Select(item => item.SourceEventId), second.Select(item => item.SourceEventId));
        Assert.Equal(2, first.Select(item => item.SourceEventId).Distinct().Count());
    }

    [Fact]
    public async Task AccountMismatchYieldsNothingWithoutTouchingTheNetwork()
    {
        var handler = new PagedHandler(Page(Event(Now.AddMinutes(-1), "model", input: 1)));
        using var database = CursorTestDatabase.Create(Jwt("auth0|user-123", Now.AddHours(1)));
        var source = new CursorUsageEventsTokenSource(
            new HttpClient(handler),
            new FixedClock(),
            new CursorCredentialSource(database.Path, new FixedClock())
        );
        var otherAccount = new ProviderAccount(
            new AccountKey(BuiltInProviderDescriptors.Cursor.Id, "auth0|someone-else"),
            "Cursor",
            null,
            "Cursor app session",
            1,
            true
        );

        List<TokenUsageEvent> events = [];
        await foreach (TokenUsageEvent item in source.ReadAsync(otherAccount, null, default))
        {
            events.Add(item);
        }

        Assert.Empty(events);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task NumericTimestampsAreAcceptedAlongsideStrings()
    {
        long unixMs = Now.AddMinutes(-5).ToUnixTimeMilliseconds();
        var handler = new PagedHandler(
            Page("{\"model\":\"numeric\",\"timestamp\":" + unixMs + ",\"tokenUsage\":{\"inputTokens\":3}}")
        );

        TokenUsageEvent single = Assert.Single(await ReadAllAsync(handler));

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(unixMs), single.OccurredAt);
    }

    /// <param name="into">
    /// Collects what was emitted before a throw, so a failing scan can be
    /// checked for having leaked a partial window.
    /// </param>
    private static async Task<List<TokenUsageEvent>> ReadAllAsync(
        PagedHandler handler,
        ScannerCursor? cursor = null,
        List<TokenUsageEvent>? into = null
    )
    {
        using var database = CursorTestDatabase.Create(Jwt("auth0|user-123", Now.AddHours(1)));
        var clock = new FixedClock();
        var source = new CursorUsageEventsTokenSource(
            new HttpClient(handler),
            clock,
            new CursorCredentialSource(database.Path, clock)
        );
        var account = new ProviderAccount(
            new AccountKey(BuiltInProviderDescriptors.Cursor.Id, "auth0|user-123"),
            "Cursor",
            null,
            "Cursor app session",
            1,
            true
        );
        List<TokenUsageEvent> events = into ?? [];
        await foreach (TokenUsageEvent item in source.ReadAsync(account, cursor, default))
        {
            events.Add(item);
        }
        return events;
    }

    private static string Event(
        DateTimeOffset at,
        string model,
        long input = 0,
        long output = 0,
        long cacheRead = 0,
        long cacheWrite = 0
    )
    {
        var tokenUsage = new Dictionary<string, object>();
        if (input > 0)
            tokenUsage["inputTokens"] = input;
        if (output > 0)
            tokenUsage["outputTokens"] = output;
        if (cacheRead > 0)
            tokenUsage["cacheReadTokens"] = cacheRead;
        if (cacheWrite > 0)
            tokenUsage["cacheWriteTokens"] = cacheWrite;
        return JsonSerializer.Serialize(
            new Dictionary<string, object>
            {
                ["timestamp"] = at.ToUnixTimeMilliseconds().ToString(),
                ["model"] = model,
                ["kind"] = "USAGE_EVENT_KIND_INCLUDED_IN_PRO",
                ["isTokenBasedCall"] = true,
                ["tokenUsage"] = tokenUsage,
                ["conversationId"] = "conv-" + at.ToUnixTimeMilliseconds(),
            }
        );
    }

    private static string Page(params string[] events) =>
        $$"""{"totalUsageEventsCount":{{events.Length}},"usageEventsDisplay":[{{string.Join(",", events)}}]}""";

    private static string Jwt(string subject, DateTimeOffset expiresAt)
    {
        static string Base64Url(string value) =>
            Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string header = Base64Url("""{"alg":"none","typ":"JWT"}""");
        string payload = Base64Url(
            JsonSerializer.Serialize(new { sub = subject, exp = expiresAt.ToUnixTimeSeconds() })
        );
        return $"{header}.{payload}.test-signature";
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class PagedHandler : HttpMessageHandler
    {
        public sealed record CapturedRequest(string Path, string? Cookie, string? Origin, string Body);

        private readonly Queue<HttpResponseMessage> _responses = new();

        public List<CapturedRequest> Requests { get; } = [];

        public PagedHandler(params string[] pageBodies)
        {
            foreach (string body in pageBodies)
            {
                Enqueue(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(body, Encoding.UTF8, "application/json"),
                    }
                );
            }
        }

        public void Enqueue(HttpResponseMessage response) => _responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add(
                new CapturedRequest(
                    request.RequestUri!.AbsolutePath,
                    request.Headers.TryGetValues("Cookie", out var cookies) ? cookies.Single() : null,
                    request.Headers.TryGetValues("Origin", out var origins) ? origins.Single() : null,
                    request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken)
                )
            );
            return _responses.Count > 0
                ? _responses.Dequeue()
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"totalUsageEventsCount":0,"usageEventsDisplay":[]}""",
                        Encoding.UTF8,
                        "application/json"
                    ),
                };
        }
    }
}

internal sealed class CursorTestDatabase : IDisposable
{
    private readonly string _directory;

    public string Path { get; }

    private CursorTestDatabase(string directory, string path)
    {
        _directory = directory;
        Path = path;
    }

    public static CursorTestDatabase Create(string accessToken)
    {
        string directory = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            "ai-limits-cursor-events-" + Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, "state.vscdb");
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection(
            new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path }.ToString()
        );
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "CREATE TABLE ItemTable(key TEXT PRIMARY KEY, value BLOB); INSERT INTO ItemTable(key, value) VALUES('cursorAuth/accessToken', $value);";
        command.Parameters.AddWithValue("$value", accessToken);
        command.ExecuteNonQuery();
        return new CursorTestDatabase(directory, path);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch { }
    }
}
