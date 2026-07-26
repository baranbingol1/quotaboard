// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Snapshots;
using AiLimits.Domain;

namespace AiLimits.Tests;

/// <summary>
/// A snapshot that reports itself Authoritative tells the merger "these are
/// all the meters that exist now", so anything missing is deleted from the
/// card. Until this change nothing ever produced <c>Partial</c>, which left
/// the merger's stale-carry path unreachable and let a provider's meters
/// vanish the moment one response came back thin.
/// </summary>
public sealed class PartialSnapshotTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 26, 12, 0, 0, TimeSpan.Zero);

    private static readonly AccountKey Account =
        new(new ProviderId("copilot"), "github");

    [Fact]
    public void A_partial_snapshot_carries_prior_meters_forward_and_badges_them_stale()
    {
        ProviderSnapshot previous = Snapshot(SnapshotCompleteness.Authoritative, Meter("chat"), Meter("completions"));
        ProviderSnapshot incoming = Snapshot(SnapshotCompleteness.Partial, Meter("chat"));

        ProviderSnapshot merged = new SnapshotMerger().Merge(previous, incoming);

        Assert.Equal(2, merged.Meters.Count);
        UsageMeter carried = merged.Meters.Single(meter => meter.Key.Value == "completions");
        Assert.Equal(MeterStatus.Stale, carried.Status);
        Assert.Equal(MeterStatus.Healthy, merged.Meters.Single(meter => meter.Key.Value == "chat").Status);
    }

    [Fact]
    public void An_empty_partial_snapshot_keeps_the_whole_card_rather_than_emptying_it()
    {
        // This is the Copilot token_based_billing case: GitHub reports no quota
        // windows at all, which used to arrive as an empty authoritative
        // snapshot and wipe every meter off the card.
        ProviderSnapshot previous = Snapshot(SnapshotCompleteness.Authoritative, Meter("chat"), Meter("completions"));
        ProviderSnapshot incoming = Snapshot(SnapshotCompleteness.Partial);

        ProviderSnapshot merged = new SnapshotMerger().Merge(previous, incoming);

        Assert.Equal(2, merged.Meters.Count);
        Assert.All(merged.Meters, meter => Assert.Equal(MeterStatus.Stale, meter.Status));
    }

    [Fact]
    public void An_authoritative_snapshot_still_drops_a_meter_the_provider_retired()
    {
        ProviderSnapshot previous = Snapshot(SnapshotCompleteness.Authoritative, Meter("chat"), Meter("completions"));
        ProviderSnapshot incoming = Snapshot(SnapshotCompleteness.Authoritative, Meter("chat"));

        ProviderSnapshot merged = new SnapshotMerger().Merge(previous, incoming);

        Assert.Equal("chat", Assert.Single(merged.Meters).Key.Value);
    }

    private static ProviderSnapshot Snapshot(SnapshotCompleteness completeness, params UsageMeter[] meters) =>
        new(Account, meters, [], completeness, Now, DataConfidence.High,
            new Dictionary<string, System.Text.Json.JsonElement>());

    private static UsageMeter Meter(string key) => new(
        new MeterKey(key), key, MeterScope.Account, MeterUnit.Percent, 10m, 100m, 10, null, null, null,
        MeterStatus.Healthy, new MeterProvenance("test", "test", Now, true));
}
