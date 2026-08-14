// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;

namespace AiLimits.Domain;

public enum MeterScope
{
    Unknown,
    Account,
    Organization,
    Model,
    Feature,
    Offering,
}

public enum MeterUnit
{
    Unknown,
    Percent,
    Requests,
    Tokens,
    Credits,
    Usd,
    Time,
}

public enum MeterStatus
{
    Unknown,
    Healthy,
    Approaching,
    Critical,
    Exhausted,
    Stale,
    Unavailable,
}

public sealed record MeterProvenance(
    string StrategyId,
    string SourcePath,
    DateTimeOffset AcquiredAt,
    bool IsAuthoritative,
    string? AttemptId = null
);

public sealed record UsageMeter(
    MeterKey Key,
    string DisplayName,
    MeterScope Scope,
    MeterUnit Unit,
    decimal? Used,
    decimal? Limit,
    double? UsedPercent,
    TimeSpan? WindowDuration,
    DateTimeOffset? ResetsAt,
    string? RawModelId,
    MeterStatus Status,
    MeterProvenance Provenance,
    DateTimeOffset? FirstObservedAt = null,
    bool IsNew = false
);

public sealed record BalanceMetric(
    string Key,
    string DisplayName,
    decimal? Value,
    MeterUnit Unit,
    string? FormattedValue = null
);

public enum SnapshotCompleteness
{
    Partial,
    Authoritative,
}

public enum DataConfidence
{
    Unknown,
    Low,
    Medium,
    High,
    Exact,
}

public sealed record ProviderSnapshot(
    AccountKey Account,
    IReadOnlyList<UsageMeter> Meters,
    IReadOnlyList<BalanceMetric> Balances,
    SnapshotCompleteness Completeness,
    DateTimeOffset ObservedAt,
    DataConfidence Confidence,
    IReadOnlyDictionary<string, JsonElement> Extensions
)
{
    public static ProviderSnapshot Empty(AccountKey account, DateTimeOffset observedAt)
    {
        return new ProviderSnapshot(
            account,
            Array.Empty<UsageMeter>(),
            Array.Empty<BalanceMetric>(),
            SnapshotCompleteness.Partial,
            observedAt,
            DataConfidence.Unknown,
            new Dictionary<string, JsonElement>()
        );
    }
}
