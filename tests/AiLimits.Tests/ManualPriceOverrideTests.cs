// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;

namespace AiLimits.Tests;

public sealed class ManualPriceOverrideTests
{
    [Fact]
    public void Quote_prices_each_lane_per_million()
    {
        var price = new ManualModelPrice(
            InputPerMillion: 3.0m,
            OutputPerMillion: 15.0m,
            CacheReadPerMillion: 0.3m,
            CacheWritePerMillion: 3.75m
        );

        decimal quote = price.QuoteUsd(
            inputTokens: 1_000_000,
            outputTokens: 2_000_000,
            cacheReadTokens: 1_000_000,
            cacheWriteTokens: 1_000_000
        );

        // 3 + 30 + 0.3 + 3.75
        Assert.Equal(37.05m, quote);
    }

    [Fact]
    public void Missing_cache_read_rate_prices_that_lane_at_zero()
    {
        var price = new ManualModelPrice(InputPerMillion: 3.0m, OutputPerMillion: 15.0m);

        decimal quote = price.QuoteUsd(
            inputTokens: 1_000_000,
            outputTokens: 0,
            cacheReadTokens: 5_000_000,
            cacheWriteTokens: 0
        );

        Assert.Equal(3.0m, quote);
    }

    [Fact]
    public void Missing_cache_write_rate_falls_back_to_the_input_rate()
    {
        var price = new ManualModelPrice(InputPerMillion: 3.0m, OutputPerMillion: 15.0m);

        decimal quote = price.QuoteUsd(
            inputTokens: 0,
            outputTokens: 0,
            cacheReadTokens: 0,
            cacheWriteTokens: 2_000_000
        );

        Assert.Equal(6.0m, quote);
    }

    [Fact]
    public void Explicit_zero_cache_write_rate_stays_free()
    {
        var price = new ManualModelPrice(
            InputPerMillion: 3.0m,
            OutputPerMillion: 15.0m,
            CacheReadPerMillion: null,
            CacheWritePerMillion: 0m
        );

        decimal quote = price.QuoteUsd(
            inputTokens: 0,
            outputTokens: 0,
            cacheReadTokens: 0,
            cacheWriteTokens: 2_000_000
        );

        Assert.Equal(0m, quote);

        var reloaded = ManualPriceOverrideSet.Parse(
            ManualPriceOverrideSet.Empty.With("amp", "model", price).Serialize()
        );
        Assert.True(reloaded.TryGet("amp", "model", out ManualModelPrice roundTripped));
        Assert.Equal(0m, roundTripped.CacheWritePerMillion);
    }

    [Fact]
    public void Reasoning_tokens_fold_into_the_output_lane()
    {
        var price = new ManualModelPrice(InputPerMillion: 0m + 1m, OutputPerMillion: 10m);

        decimal quote = price.QuoteUsd(
            inputTokens: 0,
            outputTokens: 1_000_000,
            cacheReadTokens: 0,
            cacheWriteTokens: 0,
            reasoningTokens: 1_000_000
        );

        Assert.Equal(20m, quote);
    }

    [Fact]
    public void Parse_serialize_round_trips()
    {
        var set = ManualPriceOverrideSet
            .Empty.With("cursor", "composer-2.5-fast", new ManualModelPrice(1.5m, 6m, 0.15m))
            .With("cursor", "claude-opus-4-8-thinking-xhigh", new ManualModelPrice(3m, 15m));

        var reloaded = ManualPriceOverrideSet.Parse(set.Serialize());

        Assert.Equal(2, reloaded.Count);
        Assert.True(reloaded.TryGet("cursor", "composer-2.5-fast", out ManualModelPrice composer));
        Assert.Equal(1.5m, composer.InputPerMillion);
        Assert.Equal(0.15m, composer.CacheReadPerMillion);
        Assert.Null(composer.CacheWritePerMillion);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"cursor\":{\"model\":{\"inputPer1M\":\"oops\"}}}")]
    public void Malformed_data_parses_to_empty(string? json)
    {
        Assert.Equal(0, ManualPriceOverrideSet.Parse(json).Count);
    }

    [Fact]
    public void Keys_are_case_insensitive_and_trimmed()
    {
        var set = ManualPriceOverrideSet.Empty.With(" Cursor ", " Composer-2.5 ", new ManualModelPrice(1m, 2m));

        Assert.True(set.TryGet("cursor", "composer-2.5", out _));
        Assert.True(ManualPriceOverrideSet.Parse(set.Serialize()).TryGet("CURSOR", "COMPOSER-2.5", out _));
    }

    [Fact]
    public void Null_or_zero_price_removes_the_entry()
    {
        var set = ManualPriceOverrideSet
            .Empty.With("cursor", "model", new ManualModelPrice(3m, 15m))
            .With("cursor", "model", null);

        Assert.False(set.TryGet("cursor", "model", out _));

        var zeroed = ManualPriceOverrideSet
            .Empty.With("cursor", "model", new ManualModelPrice(3m, 15m))
            .With("cursor", "model", new ManualModelPrice(0m, 0m));

        Assert.False(zeroed.TryGet("cursor", "model", out _));
    }

    [Fact]
    public void Negative_rates_are_rejected()
    {
        var set = ManualPriceOverrideSet.Empty.With("cursor", "model", new ManualModelPrice(-1m, 5m));

        Assert.Equal(0, set.Count);
    }
}
