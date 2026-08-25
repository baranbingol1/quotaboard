// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;
using AiLimits.Application.Usage;
using AiLimits.Infrastructure.Providers.Copilot;

namespace AiLimits.Tests;

public sealed class CopilotLocalActivityScannerTests
{
    [Fact]
    public async Task Reads_only_copilot_requests_from_vscode_jsonl()
    {
        using var temp = new TempDir();
        string vscode = temp.Dir("Code/User");
        string sessions = temp.Dir("Code/User/workspaceStorage/workspace/chatSessions");
        string path = Path.Combine(sessions, "session.jsonl");
        object copilot = Request("copilot-request", "copilot/model-a", "github.copilot.editsAgent", 1_775_000_000_000);
        object other = Request("other-request", "other/model", "other.agent", 1_775_000_001_000);
        await File.WriteAllLinesAsync(
            path,
            [
                JsonSerializer.Serialize(
                    new
                    {
                        kind = 0,
                        v = new
                        {
                            sessionId = "session-1",
                            creationDate = 1_775_000_000_000,
                            requests = Array.Empty<object>(),
                        },
                    }
                ),
                JsonSerializer.Serialize(
                    new
                    {
                        kind = 2,
                        k = new object[] { "requests", 0 },
                        v = new[] { copilot, other },
                    }
                ),
                "{partial",
            ]
        );

        IReadOnlyList<CopilotActivityEvent> events = await new CopilotLocalActivityScanner(
            vscode,
            temp.Dir("copilot")
        ).ScanAsync(default);

        Assert.Equal(2, events.Count);
        Assert.Contains(events, item => item.Kind == CopilotActivityKind.Session && item.SessionId == "session-1");
        CopilotActivityEvent request = Assert.Single(events, item => item.Kind == CopilotActivityKind.Request);
        Assert.Equal("copilot/model-a", request.Model);
    }

    [Fact]
    public async Task One_session_with_wrongly_typed_json_does_not_abort_the_whole_scan()
    {
        // JsonElement.TryGetInt32/TryGetInt64 throw rather than returning false
        // when the element is not a number, so a single null or string where a
        // number was expected used to take down every session's scan.
        using var temp = new TempDir();
        string vscode = temp.Dir("Code/User");
        string sessions = temp.Dir("Code/User/workspaceStorage/workspace/chatSessions");
        await File.WriteAllLinesAsync(
            Path.Combine(sessions, "hostile.jsonl"),
            [
                // "kind" as a string, and a null creationDate.
                """{ "kind": "0", "v": { "sessionId": "bad-1", "creationDate": null, "requests": [] } }""",
                """{ "kind": 0, "v": { "sessionId": "bad-2", "creationDate": "2026-04-01T00:00:00Z", "requests": [] } }""",
            ]
        );
        await File.WriteAllLinesAsync(
            Path.Combine(sessions, "healthy.jsonl"),
            [
                JsonSerializer.Serialize(
                    new
                    {
                        kind = 0,
                        v = new
                        {
                            sessionId = "session-ok",
                            creationDate = 1_775_000_000_000,
                            requests = new[]
                            {
                                Request(
                                    "copilot-request",
                                    "copilot/model-a",
                                    "github.copilot.editsAgent",
                                    1_775_000_000_000
                                ),
                            },
                        },
                    }
                ),
            ]
        );

        IReadOnlyList<CopilotActivityEvent> events = await new CopilotLocalActivityScanner(
            vscode,
            temp.Dir("copilot")
        ).ScanAsync(default);

        Assert.Contains(events, item => item.SessionId == "session-ok");
    }

    [Fact]
    public async Task Reads_completed_cli_turns_with_the_model_active_at_each_turn()
    {
        using var temp = new TempDir();
        string vscode = temp.Dir("Code/User");
        string session = temp.Dir("copilot/session-state/session-2");
        string path = Path.Combine(session, "events.jsonl");
        await File.WriteAllLinesAsync(
            path,
            [
                Event(
                    "session.start",
                    "2026-04-01T10:00:00Z",
                    new { sessionId = "session-2", selectedModel = "model-a" }
                ),
                Event("assistant.turn_end", "2026-04-01T10:01:00Z", new { turnId = "turn-1" }),
                Event("session.model_change", "2026-04-01T10:02:00Z", new { newModel = "model-b" }),
                Event("assistant.turn_end", "2026-04-01T10:03:00Z", new { turnId = "turn-2" }),
                "not-json",
            ]
        );

        IReadOnlyList<CopilotActivityEvent> events = await new CopilotLocalActivityScanner(
            vscode,
            Path.Combine(temp.Path, "copilot")
        ).ScanAsync(default);

        CopilotActivityEvent[] turns = events.Where(item => item.Kind == CopilotActivityKind.Request).ToArray();
        Assert.Equal(["model-a", "model-b"], turns.Select(item => item.Model));
        Assert.Single(events, item => item.Kind == CopilotActivityKind.Session);
    }

    [Fact]
    public void Query_keeps_activity_counts_separate_from_tokens()
    {
        CopilotActivityEvent[] events =
        [
            new("copilot-cli", "Copilot CLI", "one", Date("2026-04-01"), CopilotActivityKind.Session, null),
            new("copilot-cli", "Copilot CLI", "one", Date("2026-04-02"), CopilotActivityKind.Request, "model-a"),
            new("copilot-cli", "Copilot CLI", "two", Date("2026-05-01"), CopilotActivityKind.Request, "model-b"),
        ];

        CopilotActivitySummary summary = Assert.Single(
            CopilotActivityQuery.Run(events, new DateOnly(2026, 4, 1), new DateOnly(2026, 4, 30))
        );

        Assert.Equal(1, summary.Sessions);
        Assert.Equal(1, summary.Requests);
        Assert.Equal(["model-a"], summary.Models);
    }

    private static object Request(string id, string model, string agentId, long timestamp) =>
        new
        {
            requestId = id,
            modelId = model,
            timestamp,
            agent = new { id = agentId },
        };

    private static string Event(string type, string timestamp, object data) =>
        JsonSerializer.Serialize(
            new
            {
                type,
                timestamp,
                data,
            }
        );

    private static DateTimeOffset Date(string value) =>
        DateTimeOffset.Parse(value + "T12:00:00Z", CultureInfo.InvariantCulture);

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string Dir(string relative) =>
            Directory.CreateDirectory(System.IO.Path.Combine(Path, relative)).FullName;

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, true);
            }
        }
    }
}
