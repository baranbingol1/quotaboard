// SPDX-License-Identifier: Apache-2.0
using System.Text;
using AiLimits.Application.Pricing;
using AiLimits.Domain;
using AiLimits.Infrastructure.Pricing;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

public sealed class PricingTests
{
    [Fact]
    public void ModelsDevCatalogBuildsExactProviderModelIndex()
    {
        var catalog = ModelsDevPricingCatalog.ParseAndValidate(Encoding.UTF8.GetBytes("""
            {"openai":{"models":{"gpt-5":{"cost":{"input":2.5,"output":10,"cache_read":0.25,"reasoning":10}}}}}
            """));
        var price = catalog[("openai", "gpt-5")];
        Assert.Equal(2.5m, price.InputPerMillion);
        Assert.Equal(10m, price.OutputPerMillion);
    }

    [Fact]
    public void CatalogResponseCapClearsTheRealApiJsonSize()
    {
        // models.dev/api.json measured 3.28 MB on 2026-07-25 and grows as
        // models are added. It was briefly read under ProviderHttp's 2 MB
        // provider-reply bound, which silently pinned the catalog to its last
        // cached copy: an over-cap read returns null and the caller falls back
        // to the stale snapshot with no error surfaced anywhere.
        Assert.True(
            ModelsDevPricingCatalog.MaxCatalogBytes > ProviderHttp.DefaultMaxResponseBytes,
            "the catalog is a bulk data file and must not inherit the small provider-reply cap");
        Assert.True(
            ModelsDevPricingCatalog.MaxCatalogBytes >= 16L * 1024 * 1024,
            "leave real headroom above the observed 3.28 MB so growth cannot refreeze the catalog");
    }

    [Fact]
    public void QuoteUsesEveryObservedPricedLane()
    {
        var account = new AccountKey(new ProviderId("codex"), "one");
        var usage = new TokenUsageEvent(account, new ServiceProviderId("codex"), "gpt-5",
            DateTimeOffset.UtcNow, 1_000_000, 2_000_000, 500_000, 0, 250_000, "event");
        var price = new ModelPrice("openai", "gpt-5", 2m, 10m, 0.5m, null, 10m);
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice> { [("openai", "gpt-5")] = price });
        var quote = new PricingEngine().Quote(usage,
            new ModelResolution("openai", "gpt-5", ResolutionConfidence.Exact), catalog);
        Assert.NotNull(quote);
        Assert.Equal(24.75m, quote.CostUsd);
    }

    [Fact]
    public void MissingCacheReadRateLeavesUsageUnpriced()
    {
        var usage = new TokenUsageEvent(new AccountKey(new ProviderId("claude"), "one"),
            new ServiceProviderId("claude"), "model", DateTimeOffset.UtcNow,
            1, 1, 10, 0, 0, "event");
        var price = new ModelPrice("anthropic", "model", 1, 1, null, null, null);
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice> { [("anthropic", "model")] = price });
        Assert.Null(new PricingEngine().Quote(usage,
            new ModelResolution("anthropic", "model", ResolutionConfidence.Exact), catalog));
    }

    [Fact]
    public void MissingCacheWriteRateBillsWritesAtInputRate()
    {
        // Amp via Fireworks: models.dev lists input/output/cache_read for
        // accounts/fireworks/models/glm-5p2 but no cache_write, because the
        // host bills cache writes as ordinary input.
        var usage = new TokenUsageEvent(new AccountKey(new ProviderId("amp"), "one"),
            new ServiceProviderId("amp"), "model", DateTimeOffset.UtcNow,
            0, 0, 0, 1_000_000, 0, "event");
        var price = new ModelPrice("fireworks-ai", "model", 1.4m, 4.4m, 0.14m, null, null);
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice> { [("fireworks-ai", "model")] = price });
        var quote = new PricingEngine().Quote(usage,
            new ModelResolution("fireworks-ai", "model", ResolutionConfidence.Exact), catalog);
        Assert.NotNull(quote);
        Assert.Equal(1.4m, quote.CostUsd);
    }

    [Fact]
    public void ExplicitZeroCacheWriteRateStaysFree()
    {
        var usage = new TokenUsageEvent(new AccountKey(new ProviderId("amp"), "one"),
            new ServiceProviderId("amp"), "model", DateTimeOffset.UtcNow,
            0, 0, 0, 1_000_000, 0, "event");
        var price = new ModelPrice("zai", "model", 1.4m, 4.4m, 0.26m, 0m, null);
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice> { [("zai", "model")] = price });
        var quote = new PricingEngine().Quote(usage,
            new ModelResolution("zai", "model", ResolutionConfidence.Exact), catalog);
        Assert.NotNull(quote);
        Assert.Equal(0m, quote.CostUsd);
    }

    [Fact]
    public void ResolverDoesNotFuzzyGuessDatedOrPrefixedIds()
    {
        var resolver = new ExplicitModelResolver([
            new ModelAlias(new ServiceProviderId("copilot"), "claude-sonnet-4", "anthropic", "claude-sonnet-4")
        ]);
        Assert.NotNull(resolver.Resolve(new ServiceProviderId("copilot"), " Claude_Sonnet 4 "));
        Assert.Null(resolver.Resolve(new ServiceProviderId("copilot"), "anthropic/claude-sonnet-4-20991231"));
        Assert.Null(resolver.Resolve(new ServiceProviderId("opencode"), "claude-sonnet-4"));
    }

    [Fact]
    public void CatalogResolverUsesCurrentExactCatalogModelsWithoutHardcodedAliases()
    {
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice>
            {
                [("openai", "gpt-5.6-sol")] = new("openai", "gpt-5.6-sol", 5, 30, 0.5m, 6.25m, null),
                [("anthropic", "claude-fable-5")] = new("anthropic", "claude-fable-5", 10, 50, 1, 12.5m, null)
            });
        var resolver = new CatalogModelResolver(new ExplicitModelResolver([]));
        Assert.NotNull(resolver.Resolve(new ServiceProviderId("codex"), "gpt-5.6-sol", catalog));
        Assert.NotNull(resolver.Resolve(new ServiceProviderId("opencode"), "openai/gpt-5.6-sol", catalog));
        Assert.NotNull(resolver.Resolve(new ServiceProviderId("claude"), "claude-fable-5", catalog));
    }

    [Fact]
    public void CatalogResolverFallsBackToDashedIdWhenCatalogLacksDottedId()
    {
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice>
            {
                [("anthropic", "claude-opus-4-7")] = new("anthropic", "claude-opus-4-7", 15, 75, 1.5m, 18.75m, null),
                [("openai", "gpt-4.1")] = new("openai", "gpt-4.1", 2, 8, 0.5m, null, null)
            });
        var resolver = new CatalogModelResolver(new ExplicitModelResolver([]));
        var resolution = resolver.Resolve(new ServiceProviderId("opencode"), "claude-opus-4.7", catalog);
        Assert.NotNull(resolution);
        Assert.Equal("claude-opus-4-7", resolution.CanonicalModelId);
        Assert.Equal(ResolutionConfidence.Exact, resolution.Confidence);
        var dotted = resolver.Resolve(new ServiceProviderId("codex"), "gpt-4.1", catalog);
        Assert.NotNull(dotted);
        Assert.Equal("gpt-4.1", dotted.CanonicalModelId);
    }

    [Fact]
    public void CatalogResolverDerivesOpenAiFastVariantFromBaseModelAt2Point5X()
    {
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice>
            {
                [("openai", "gpt-5.5")] = new("openai", "gpt-5.5", 2m, 10m, 0.5m, null, 10m)
            });
        var resolver = new CatalogModelResolver(new ExplicitModelResolver([]));
        var resolution = resolver.Resolve(new ServiceProviderId("opencode"), "gpt-5.5-fast", catalog);
        Assert.NotNull(resolution);
        Assert.Equal("gpt-5.5", resolution.CanonicalModelId);
        Assert.Equal(ResolutionConfidence.DerivedMultiplier, resolution.Confidence);
        Assert.Equal(2.5m, resolution.RateMultiplier);

        var usage = new TokenUsageEvent(new AccountKey(new ProviderId("opencode"), "one"),
            new ServiceProviderId("opencode"), "gpt-5.5-fast",
            DateTimeOffset.UtcNow, 1_000_000, 2_000_000, 500_000, 0, 250_000, "event");
        var quote = new PricingEngine().Quote(usage, resolution, catalog);
        Assert.NotNull(quote);
        Assert.Equal(61.875m, quote.CostUsd);

        Assert.Null(resolver.Resolve(new ServiceProviderId("codex"), "codex-auto-review", catalog));
        Assert.Null(resolver.Resolve(new ServiceProviderId("codex"), "unknown", catalog));
        Assert.Null(resolver.Resolve(new ServiceProviderId("opencode"), "-fast", catalog));
    }

    [Fact]
    public void CatalogIndexIsLowercaseSoMixedCaseCatalogIdsStillResolve()
    {
        // models.dev keys the MiniMax vendor's models as "MiniMax-M3" while
        // local telemetry reports "minimax-m3"; the index lowers both sides.
        var index = ModelsDevPricingCatalog.ParseAndValidate(Encoding.UTF8.GetBytes("""
            {
              "minimax":{"models":{"MiniMax-M3":{"cost":{"input":0.3,"output":1.2}}}},
              "moonshotai":{"models":{"kimi-k2.7-code":{"cost":{"input":0.6,"output":2.5}}}}
            }
            """));
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null, index);
        var resolver = new CatalogModelResolver(new ExplicitModelResolver([]));

        var minimax = resolver.Resolve(new ServiceProviderId("droid"), "minimax-m3", catalog);
        Assert.NotNull(minimax);
        Assert.Equal("minimax", minimax.PricingProviderId);
        Assert.Equal("minimax-m3", minimax.CanonicalModelId);

        var kimi = resolver.Resolve(new ServiceProviderId("droid"), "kimi-k2.7-code", catalog);
        Assert.NotNull(kimi);
        Assert.Equal("moonshotai", kimi.PricingProviderId);
        Assert.Equal("kimi-k2.7-code", kimi.CanonicalModelId);
    }

    [Fact]
    public void CatalogResolverMatchesFullHostPathIdsAcrossProviders()
    {
        // Amp reports Fireworks-served models by their full host path, which
        // models.dev catalogs verbatim under the host provider.
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice>
            {
                [("fireworks-ai", "accounts/fireworks/models/glm-5p2")] =
                    new("fireworks-ai", "accounts/fireworks/models/glm-5p2", 0.55m, 2.19m, null, null, null)
            });
        var resolver = new CatalogModelResolver(new ExplicitModelResolver([]));
        var resolution = resolver.Resolve(new ServiceProviderId("amp"), "accounts/fireworks/models/glm-5p2", catalog);
        Assert.NotNull(resolution);
        Assert.Equal("fireworks-ai", resolution.PricingProviderId);
        Assert.Equal("accounts/fireworks/models/glm-5p2", resolution.CanonicalModelId);
        Assert.Equal(ResolutionConfidence.Exact, resolution.Confidence);

        Assert.Null(resolver.Resolve(new ServiceProviderId("amp"), "accounts/fireworks/models/other", catalog));
    }

    [Fact]
    public void QuoteUsesOutputRateForReasoningWhenCatalogHasNoSeparateRate()
    {
        var usage = new TokenUsageEvent(new AccountKey(new ProviderId("codex"), "one"),
            new ServiceProviderId("codex"), "gpt-5.6-sol", DateTimeOffset.UtcNow,
            0, 0, 0, 0, 1_000_000, "event");
        var price = new ModelPrice("openai", "gpt-5.6-sol", 5, 30, 0.5m, 6.25m, null);
        var catalog = new PricingCatalogSnapshot("abc", DateTimeOffset.UtcNow, null,
            new Dictionary<(string, string), ModelPrice> { [("openai", "gpt-5.6-sol")] = price });
        var quote = new PricingEngine().Quote(usage,
            new ModelResolution("openai", "gpt-5.6-sol", ResolutionConfidence.Exact), catalog);
        Assert.NotNull(quote);
        Assert.Equal(30m, quote.CostUsd);
    }
}
