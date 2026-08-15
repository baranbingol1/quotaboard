// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Domain;

public sealed record TokenUsageEvent(
    AccountKey Account,
    ServiceProviderId Service,
    string RawModelId,
    DateTimeOffset OccurredAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    string SourceEventId,
    ProjectIdentity Project
)
{
    public TokenUsageEvent(
        AccountKey account,
        ServiceProviderId service,
        string rawModelId,
        DateTimeOffset occurredAt,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long reasoningTokens,
        string sourceEventId
    )
        : this(
            account,
            service,
            rawModelId,
            occurredAt,
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens,
            reasoningTokens,
            sourceEventId,
            ProjectIdentity.Unknown
        ) { }

    public long TotalTokens =>
        checked(InputTokens + OutputTokens + CacheReadTokens + CacheWriteTokens + ReasoningTokens);
}

public sealed record DailyUsageAggregate(
    DateOnly Day,
    AccountKey Account,
    ServiceProviderId Service,
    string RawModelId,
    ModelResolution? Resolution,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    long ReasoningTokens,
    decimal? ReportedServiceCostUsd,
    ProjectIdentity Project
)
{
    public DailyUsageAggregate(
        DateOnly day,
        AccountKey account,
        ServiceProviderId service,
        string rawModelId,
        ModelResolution? resolution,
        long inputTokens,
        long outputTokens,
        long cacheReadTokens,
        long cacheWriteTokens,
        long reasoningTokens,
        decimal? reportedServiceCostUsd = null
    )
        : this(
            day,
            account,
            service,
            rawModelId,
            resolution,
            inputTokens,
            outputTokens,
            cacheReadTokens,
            cacheWriteTokens,
            reasoningTokens,
            reportedServiceCostUsd,
            ProjectIdentity.Unknown
        ) { }
}
