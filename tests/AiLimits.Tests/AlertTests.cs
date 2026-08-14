// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Alerts;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class AlertTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 8, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Suggested_policy_emits_both_thresholds_and_reset_reminder()
    {
        ProviderSnapshot snapshot = Snapshot(usedPercent: 96, resetsAt: Now.AddMinutes(20));

        IReadOnlyList<AlertCandidate> alerts = new AlertEvaluator().Evaluate(snapshot, AlertPolicy.Suggested, Now);

        Assert.Equal(3, alerts.Count);
        Assert.Equal(2, alerts.Count(alert => alert.Kind == AlertKind.UsageThreshold));
        Assert.Single(alerts, alert => alert.Kind == AlertKind.ResetReminder);
        Assert.Equal(3, alerts.Select(alert => alert.DeduplicationKey).Distinct().Count());
    }

    [Fact]
    public void Disabled_policy_silences_all_alerts()
    {
        ProviderSnapshot snapshot = Snapshot(usedPercent: 100, resetsAt: Now.AddMinutes(5));
        AlertPolicy disabled = AlertPolicy.Suggested with { Enabled = false };

        Assert.Empty(new AlertEvaluator().Evaluate(snapshot, disabled, Now));
    }

    [Fact]
    public void Evaluator_uses_the_injected_alert_text_provider()
    {
        var text = new RecordingAlertTextProvider();
        ProviderSnapshot snapshot = Snapshot(usedPercent: 80, resetsAt: Now.AddMinutes(20));

        IReadOnlyList<AlertCandidate> alerts = new AlertEvaluator(text).Evaluate(snapshot, AlertPolicy.Suggested, Now);

        Assert.Collection(
            alerts,
            alert =>
            {
                Assert.Equal("usage-title", alert.Title);
                Assert.Equal("usage-message", alert.Message);
            },
            alert =>
            {
                Assert.Equal("reset-title", alert.Title);
                Assert.Equal("reset-message", alert.Message);
            }
        );
        Assert.Equal(1, text.UsageTitleCalls);
        Assert.Equal(1, text.UsageMessageCalls);
        Assert.Equal(1, text.ResetTitleCalls);
        Assert.Equal(1, text.ResetMessageCalls);
    }

    [Fact]
    public async Task Processor_delivers_each_event_exactly_once()
    {
        var store = new InMemoryAlertStateStore();
        var sink = new RecordingNotificationSink();
        var processor = new AlertProcessor(new AlertEvaluator(), store, sink);
        ProviderSnapshot snapshot = Snapshot(usedPercent: 96, resetsAt: Now.AddMinutes(20));

        Assert.Equal(3, await processor.ProcessAsync([snapshot], AlertPolicy.Suggested, Now, default));
        Assert.Equal(0, await processor.ProcessAsync([snapshot], AlertPolicy.Suggested, Now.AddMinutes(1), default));
        Assert.Equal(3, sink.Delivered.Count);
    }

    [Fact]
    public async Task Failed_delivery_releases_claim_for_next_refresh()
    {
        var store = new InMemoryAlertStateStore();
        var sink = new RecordingNotificationSink { Succeeds = false };
        var processor = new AlertProcessor(new AlertEvaluator(), store, sink);
        ProviderSnapshot snapshot = Snapshot(usedPercent: 80, resetsAt: Now.AddHours(2));

        Assert.Equal(0, await processor.ProcessAsync([snapshot], AlertPolicy.Suggested, Now, default));
        sink.Succeeds = true;
        Assert.Equal(1, await processor.ProcessAsync([snapshot], AlertPolicy.Suggested, Now.AddMinutes(1), default));
    }

    [Fact]
    public async Task Sqlite_claim_survives_repository_instances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "alerts.db");

        try
        {
            var database = new SqliteDatabase(path);
            await database.InitializeAsync();
            AlertCandidate candidate = Assert.Single(
                new AlertEvaluator().Evaluate(
                    Snapshot(usedPercent: 80, resetsAt: Now.AddHours(2)),
                    AlertPolicy.Suggested,
                    Now
                )
            );

            Assert.True(await new SqliteAlertStateRepository(database).TryClaimAsync(candidate, Now, default));
            Assert.False(
                await new SqliteAlertStateRepository(database).TryClaimAsync(candidate, Now.AddMinutes(1), default)
            );
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    private static ProviderSnapshot Snapshot(double usedPercent, DateTimeOffset? resetsAt)
    {
        var account = new AccountKey(new ProviderId("codex"), "personal");
        var meter = new UsageMeter(
            new MeterKey("codex:five-hour"),
            "Five-hour limit",
            MeterScope.Account,
            MeterUnit.Percent,
            (decimal)usedPercent,
            100,
            usedPercent,
            TimeSpan.FromHours(5),
            resetsAt,
            null,
            MeterStatus.Critical,
            new MeterProvenance("test", "$.limit", Now, true)
        );
        return new ProviderSnapshot(
            account,
            [meter],
            [],
            SnapshotCompleteness.Authoritative,
            Now,
            DataConfidence.Exact,
            new Dictionary<string, JsonElement>()
        );
    }

    private sealed class InMemoryAlertStateStore : IAlertStateStore
    {
        private readonly HashSet<string> _claims = new(StringComparer.Ordinal);

        public Task<bool> TryClaimAsync(
            AlertCandidate candidate,
            DateTimeOffset claimedAt,
            CancellationToken cancellationToken
        ) => Task.FromResult(_claims.Add(candidate.DeduplicationKey));

        public Task ReleaseAsync(AlertCandidate candidate, CancellationToken cancellationToken)
        {
            _claims.Remove(candidate.DeduplicationKey);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingNotificationSink : IAlertNotificationSink
    {
        public bool Succeeds { get; set; } = true;
        public List<AlertCandidate> Delivered { get; } = [];

        public Task<bool> TryShowAsync(AlertCandidate candidate, CancellationToken cancellationToken)
        {
            if (Succeeds)
            {
                Delivered.Add(candidate);
            }
            return Task.FromResult(Succeeds);
        }
    }

    private sealed class RecordingAlertTextProvider : IAlertTextProvider
    {
        public int UsageTitleCalls { get; private set; }
        public int UsageMessageCalls { get; private set; }
        public int ResetTitleCalls { get; private set; }
        public int ResetMessageCalls { get; private set; }

        public string UsageTitle(string providerId, string meterKey, string meterName, double threshold)
        {
            UsageTitleCalls++;
            return "usage-title";
        }

        public string UsageMessage(double usedPercent, DateTimeOffset? resetsAt)
        {
            UsageMessageCalls++;
            return "usage-message";
        }

        public string ResetTitle(string providerId, string meterKey, string meterName)
        {
            ResetTitleCalls++;
            return "reset-title";
        }

        public string ResetMessage(TimeSpan remaining)
        {
            ResetMessageCalls++;
            return "reset-message";
        }
    }
}
