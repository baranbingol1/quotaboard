// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Domain;

public enum ResolutionConfidence
{
    Unresolved,
    Exact,
    ExplicitAlias,
    DerivedMultiplier,
}

public sealed record ModelResolution(
    string PricingProviderId,
    string CanonicalModelId,
    ResolutionConfidence Confidence,
    decimal RateMultiplier = 1m
);

public sealed record ModelPrice(
    string PricingProviderId,
    string CanonicalModelId,
    decimal? InputPerMillion,
    decimal? OutputPerMillion,
    decimal? CacheReadPerMillion,
    decimal? CacheWritePerMillion,
    decimal? ReasoningPerMillion,
    decimal? LongContextInputPerMillion = null,
    decimal? LongContextOutputPerMillion = null,
    long? LongContextThreshold = null
);

public sealed record PricingCatalogSnapshot(
    string Hash,
    DateTimeOffset FetchedAt,
    string? ETag,
    IReadOnlyDictionary<(string Provider, string Model), ModelPrice> ExactIndex
)
{
    public TimeSpan Age(DateTimeOffset now)
    {
        return now - FetchedAt;
    }
}

public sealed record ApiEquivalentQuote(
    decimal CostUsd,
    string CatalogHash,
    DateTimeOffset CatalogFetchedAt,
    ResolutionConfidence Resolution
);
