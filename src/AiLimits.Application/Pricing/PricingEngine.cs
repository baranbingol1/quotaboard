// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Pricing;

public sealed class PricingEngine
{
    private const decimal PerMillion = 1000000m;

    public ApiEquivalentQuote? Quote(TokenUsageEvent usage, ModelResolution? resolution, PricingCatalogSnapshot catalog)
    {
        if (resolution is null || !catalog.ExactIndex.TryGetValue((resolution.PricingProviderId, resolution.CanonicalModelId), out ModelPrice value))
        {
            return null;
        }
        decimal? rate = value.ReasoningPerMillion ?? value.OutputPerMillion;
        // Most hosts bill cache writes as ordinary input (no surcharge), so
        // models.dev omits cache_write for them; an explicit 0 still means free.
        // Cache reads get no such fallback: the discount cannot be guessed.
        decimal? cacheWriteRate = value.CacheWritePerMillion ?? value.InputPerMillion;
        if (!CanPrice(usage.InputTokens, value.InputPerMillion) || !CanPrice(usage.OutputTokens, value.OutputPerMillion) || !CanPrice(usage.CacheReadTokens, value.CacheReadPerMillion) || !CanPrice(usage.CacheWriteTokens, cacheWriteRate) || !CanPrice(usage.ReasoningTokens, rate))
        {
            return null;
        }
        decimal costUsd = resolution.RateMultiplier * (Lane(usage.InputTokens, value.InputPerMillion) + Lane(usage.OutputTokens, value.OutputPerMillion) + Lane(usage.CacheReadTokens, value.CacheReadPerMillion) + Lane(usage.CacheWriteTokens, cacheWriteRate) + Lane(usage.ReasoningTokens, rate));
        return new ApiEquivalentQuote(costUsd, catalog.Hash, catalog.FetchedAt, resolution.Confidence);
    }

    private static bool CanPrice(long tokens, decimal? rate)
    {
        return tokens == 0L || rate.HasValue;
    }

    private static decimal Lane(long tokens, decimal? rate)
    {
        return (decimal)tokens * rate.GetValueOrDefault() / 1000000m;
    }
}
