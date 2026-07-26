// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Snapshots;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Shared;

namespace AiLimits.Tests;

public sealed class DynamicMeterTests
{
    private static readonly ProviderId Provider = new("test");
    private static readonly AccountKey Account = new(Provider, "one");
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void WeeklyOnlyPayloadDoesNotCreatePlaceholderLane()
    {
        using var document = JsonDocument.Parse("""
            {"rate_limit":{"weekly":{"id":"weekly","used_percent":42,"reset_at":1784059200}}}
            """);
        var meters = new DynamicMeterExtractor().Extract(Provider, document.RootElement, "fixture", Now, true);
        var meter = Assert.Single(meters);
        Assert.Equal(42, meter.UsedPercent);
        Assert.DoesNotContain(meters, item => item.DisplayName.Contains("5", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void UnknownFableLikeObjectBecomesStableMeter()
    {
        using var first = JsonDocument.Parse("""
            {"limits":{"fable_weekly":{"used_percent":17,"resets_at":"2026-07-20T00:00:00Z","model_id":"fable-2"}}}
            """);
        using var renamed = JsonDocument.Parse("""
            {"limits":{"fable_weekly":{"used_percent":19,"resets_at":"2026-07-20T00:00:00Z","model_id":"fable-2"}}}
            """);
        var extractor = new DynamicMeterExtractor();
        var a = Assert.Single(extractor.Extract(Provider, first.RootElement, "fixture", Now, true));
        var b = Assert.Single(extractor.Extract(Provider, renamed.RootElement, "fixture", Now.AddMinutes(1), true));
        Assert.Equal(a.Key, b.Key);
        Assert.Equal("Fable Weekly", a.DisplayName);
        Assert.Equal(MeterScope.Model, a.Scope);
    }

    [Fact]
    public void ReorderedProviderArrayKeepsMeterIdentity()
    {
        using var first = JsonDocument.Parse("""
            {"meters":[{"id":"alpha","used":2,"limit":10},{"id":"beta","used":8,"limit":10}]}
            """);
        using var second = JsonDocument.Parse("""
            {"meters":[{"id":"beta","used":9,"limit":10},{"id":"alpha","used":3,"limit":10}]}
            """);
        var extractor = new DynamicMeterExtractor();
        var a = extractor.Extract(Provider, first.RootElement, "fixture", Now, true).ToDictionary(item => item.DisplayName);
        var b = extractor.Extract(Provider, second.RootElement, "fixture", Now, true).ToDictionary(item => item.DisplayName);
        Assert.Equal(a["Alpha"].Key, b["Alpha"].Key);
        Assert.Equal(a["Beta"].Key, b["Beta"].Key);
    }

    [Fact]
    public void WindowEndSnakeCaseIsRecognizedAsResetTime()
    {
        using var document = JsonDocument.Parse("""
            {"limits":{"weekly":{"usedPercent":36,"window_end":"2026-07-20T18:30:00Z"}}}
            """);

        var meter = Assert.Single(new DynamicMeterExtractor().Extract(
            Provider, document.RootElement, "fixture", Now, true));

        Assert.Equal(new DateTimeOffset(2026, 7, 20, 18, 30, 0, TimeSpan.Zero), meter.ResetsAt);
    }

    [Fact]
    public void PartialSnapshotPreservesMissingMeterAsStale()
    {
        var previous = Snapshot(SnapshotCompleteness.Authoritative,
            Meter("short", 30), Meter("weekly", 40));
        var incoming = Snapshot(SnapshotCompleteness.Partial, Meter("weekly", 45)) with { ObservedAt = Now.AddMinutes(5) };
        var merged = new SnapshotMerger().Merge(previous, incoming);
        Assert.Equal(2, merged.Meters.Count);
        Assert.Equal(MeterStatus.Stale, merged.Meters.Single(item => item.Key.Value == "short").Status);
    }

    [Fact]
    public void AuthoritativeSnapshotRetiresMissingMeterImmediately()
    {
        var previous = Snapshot(SnapshotCompleteness.Authoritative,
            Meter("short", 30), Meter("weekly", 40));
        var incoming = Snapshot(SnapshotCompleteness.Authoritative, Meter("weekly", 45)) with { ObservedAt = Now.AddMinutes(5) };
        var merged = new SnapshotMerger().Merge(previous, incoming);
        Assert.Single(merged.Meters);
        Assert.Equal("weekly", merged.Meters[0].Key.Value);
    }

    [Fact]
    public void FactoryPoolsStayGroupedInWindowOrder()
    {
        UsageMeter[] meters =
        [
            DisplayMeter("core-monthly", "Droid Core monthly limit", 19, Now.AddDays(15)),
            DisplayMeter("weekly", "Weekly limit", 92, Now.AddDays(1)),
            DisplayMeter("core-five-hour", "Droid Core 5-hour limit", 5, Now),
            DisplayMeter("monthly", "Monthly limit", 38, Now.AddDays(20)),
            DisplayMeter("five-hour", "5-hour limit", 22, Now.AddMinutes(22)),
            DisplayMeter("core-weekly", "Droid Core weekly limit", 33, Now)
        ];

        Assert.Equal(
        [
            "five-hour",
            "weekly",
            "monthly",
            "core-five-hour",
            "core-weekly",
            "core-monthly"
        ], OrderedKeys(meters));
    }

    [Fact]
    public void UsageAndResetChangesDoNotChangeDisplayOrder()
    {
        UsageMeter[] first =
        [
            DisplayMeter("weekly", "Weekly limit", 10, Now.AddDays(6)),
            DisplayMeter("five-hour", "5-hour limit", 90, Now.AddHours(1)),
            DisplayMeter("monthly", "Monthly limit", 50, Now.AddDays(20))
        ];
        UsageMeter[] refreshed =
        [
            DisplayMeter("monthly", "Monthly limit", 1, Now.AddMinutes(1)),
            DisplayMeter("five-hour", "5-hour limit", 2, Now.AddDays(30)),
            DisplayMeter("weekly", "Weekly limit", 99, Now.AddHours(2))
        ];

        string[] firstOrder = OrderedKeys(first);
        string[] refreshedOrder = OrderedKeys(refreshed);

        Assert.Equal(new[] { "five-hour", "weekly", "monthly" }, firstOrder);
        Assert.Equal(firstOrder, refreshedOrder);
    }

    private static ProviderSnapshot Snapshot(SnapshotCompleteness completeness, params UsageMeter[] meters) =>
        new(Account, meters, [], completeness, Now, DataConfidence.High, new Dictionary<string, JsonElement>());

    private static string[] OrderedKeys(IEnumerable<UsageMeter> meters) => meters
        .OrderBy(meter => meter, MeterDisplayOrderComparer.Instance)
        .Select(meter => meter.Key.Value)
        .ToArray();

    private static UsageMeter DisplayMeter(string key, string name, double percent, DateTimeOffset reset) =>
        new(new MeterKey(key), name, MeterScope.Account, MeterUnit.Percent, null, null, percent, null,
            reset, null, MeterStatus.Healthy, new MeterProvenance("fixture", "$", Now, true));

    private static UsageMeter Meter(string key, double percent) =>
        new(new MeterKey(key), key, MeterScope.Account, MeterUnit.Percent, null, null, percent, null,
            Now.AddDays(1), null, MeterStatus.Healthy, new MeterProvenance("fixture", "$", Now, true));
}
