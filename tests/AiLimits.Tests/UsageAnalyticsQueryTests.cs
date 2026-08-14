// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Usage;

namespace AiLimits.Tests;

public sealed class UsageAnalyticsQueryTests
{
    [Fact]
    public void ChartGeometry_distributes_every_bucket_across_the_available_width()
    {
        UsageChartGeometry geometry = UsageChartLayout.Calculate(availableWidth: 1_200, itemCount: 30, gap: 3);

        Assert.Equal(37.1, geometry.ItemWidth, precision: 1);
        Assert.InRange(geometry.BarWidth, 25, 26);
        Assert.Equal(1_200, geometry.ConsumedWidth, precision: 6);
    }

    [Fact]
    public void All_first_class_filters_change_the_result()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, "copilot", "GitHub Copilot", "opencode", "OpenCode", "alpha", "Alpha", "claude", 100, 1m),
            Row(0, "openai", "OpenAI", "codex", "Codex", "beta", "Beta", "gpt", 200, 2m),
            Row(1, "copilot", "GitHub Copilot", "opencode", "OpenCode", "beta", "Beta", "claude", 300, 3m),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(1),
                Providers = ["copilot"],
                Harnesses = ["opencode"],
                Projects = ["beta"],
                Models = ["claude"],
            }
        );

        Assert.Equal(300, result.TotalTokens);
        Assert.Equal(3m, result.ApiEquivalentCostUsd);
        Assert.Single(result.Breakdown);
    }

    [Fact]
    public void Facets_cascade_but_ignore_their_own_selection()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, "copilot", "GitHub Copilot", "opencode", "OpenCode", "alpha", "Alpha", "claude", 100, 1m),
            Row(0, "openai", "OpenAI", "codex", "Codex", "beta", "Beta", "gpt", 200, 2m),
            Row(0, "openai", "OpenAI", "opencode", "OpenCode", "alpha", "Alpha", "gpt", 300, 3m),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Providers = ["openai"],
                Harnesses = ["opencode"],
            }
        );

        Assert.Equal(["openai", "copilot"], result.Facets.Providers.Select(option => option.Key));
        Assert.Equal(["opencode", "codex"], result.Facets.Harnesses.Select(option => option.Key));
        Assert.Equal(["alpha"], result.Facets.Projects.Select(option => option.Key));
        Assert.Equal(300, result.TotalTokens);
    }

    [Fact]
    public void Chart_fills_empty_day_buckets()
    {
        UsageAnalyticsRecord[] records = [Row(0, tokens: 100), Row(2, tokens: 300)];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(2),
                TimeGrain = UsageTimeGrain.Day,
            }
        );

        Assert.Equal([100, 0, 300], result.Chart.Select(bucket => bucket.Tokens));
    }

    [Fact]
    public void Unfiltered_chart_series_uses_top_three_and_others()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, "p1", "Provider 1", tokens: 500),
            Row(0, "p2", "Provider 2", tokens: 400),
            Row(0, "p3", "Provider 3", tokens: 300),
            Row(0, "p4", "Provider 4", tokens: 200),
            Row(0, "p5", "Provider 5", tokens: 100),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query());

        Assert.Equal(
            ["Provider 1", "Provider 2", "Provider 3", "Others"],
            result.ChartLegend.Select(item => item.Label)
        );
        Assert.Equal([500, 400, 300, 300], result.Chart[0].Segments.Select(segment => segment.Tokens));
        Assert.Equal(result.Chart[0].Tokens, result.Chart[0].Segments.Sum(segment => segment.Tokens));
    }

    [Fact]
    public void Explicit_provider_selection_becomes_the_chart_legend()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, "p1", "Provider 1", tokens: 500),
            Row(0, "p2", "Provider 2", tokens: 400),
            Row(0, "p3", "Provider 3", tokens: 300),
            Row(0, "p4", "Provider 4", tokens: 200),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query() with { Providers = ["p2", "p4"] });

        Assert.Equal(["p2", "p4"], result.ChartLegend.Select(item => item.Key));
        Assert.Equal([400, 200], result.Chart[0].Segments.Select(segment => segment.Tokens));
    }

    [Fact]
    public void Explicit_selection_uses_six_series_and_pools_the_remainder()
    {
        UsageAnalyticsRecord[] records = Enumerable
            .Range(1, 8)
            .Select(index => Row(0, modelKey: $"model-{index}", tokens: 900 - (index * 100)))
            .ToArray();

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                ChartSeries = UsageChartSeriesDimension.Model,
                Models = records.Select(record => record.ModelKey).ToArray(),
            }
        );

        Assert.Equal(7, result.ChartLegend.Count);
        UsageChartLegendItem others = Assert.Single(result.ChartLegend, item => item.IsOthers);
        Assert.Equal(2, others.PooledSeriesCount);
        Assert.All(result.Chart, bucket => Assert.Equal(bucket.Tokens, bucket.Segments.Sum(segment => segment.Tokens)));
    }

    [Fact]
    public void Chart_series_can_switch_to_recording_tool()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, toolKey: "codex", toolLabel: "Codex", tokens: 500),
            Row(0, toolKey: "opencode", toolLabel: "OpenCode", tokens: 300),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                ChartSeries = UsageChartSeriesDimension.Harness,
            }
        );

        Assert.Equal(["codex", "opencode"], result.ChartLegend.Select(item => item.Key));
        Assert.Equal([500, 300], result.Chart[0].Segments.Select(segment => segment.Tokens));
    }

    [Fact]
    public void Previous_period_uses_the_same_filters()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(-2, accessKey: "openai", tokens: 50),
            Row(-1, accessKey: "copilot", tokens: 900),
            Row(0, accessKey: "openai", tokens: 100),
            Row(1, accessKey: "copilot", tokens: 900),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(1),
                Providers = ["openai"],
            }
        );

        Assert.NotNull(result.Comparison);
        Assert.Equal(100, result.Comparison!.CurrentTokens);
        Assert.Equal(50, result.Comparison.PreviousTokens);
        Assert.Equal(100, result.Comparison.PercentChange);
    }

    [Fact]
    public void Breakdown_always_covers_the_whole_window()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, projectKey: "alpha", tokens: 100),
            Row(1, projectKey: "beta", tokens: 300),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(1),
                Breakdown = UsageBreakdownDimension.Project,
            }
        );

        Assert.Equal(400, result.TotalTokens);
        Assert.Equal(2, result.Chart.Count);
        Assert.Equal(2, result.Breakdown.Count);
        Assert.Equal(400, result.Breakdown.Sum(item => item.Tokens));
    }

    [Fact]
    public void Chart_series_can_switch_to_model()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, modelKey: "claude-fable-5", tokens: 500),
            Row(0, modelKey: "gpt-5.6", tokens: 300),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                ChartSeries = UsageChartSeriesDimension.Model,
            }
        );

        Assert.Equal(["claude-fable-5", "gpt-5.6"], result.ChartLegend.Select(item => item.Key));
        Assert.Equal([500, 300], result.Chart[0].Segments.Select(segment => segment.Tokens));
    }

    [Fact]
    public void One_provider_spelled_two_ways_stays_one_facet_row()
    {
        // The label depends on the recording harness as well as the service
        // id, so the same provider can arrive under two labels. Facets are
        // filtered by key, so a split here means one row filters half the data.
        UsageAnalyticsRecord[] records =
        [
            Row(0, accessKey: "openai-codex", accessLabel: "OpenAI (Codex)", toolKey: "codex", tokens: 500),
            Row(0, accessKey: "openai-codex", accessLabel: "OpenAI", toolKey: "opencode", tokens: 300),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query());

        UsageFacetOption facet = Assert.Single(result.Facets.Providers);
        Assert.Equal("openai-codex", facet.Key);
        Assert.Equal(800, facet.Tokens);
        // The dominant spelling wins so the row is named after most of its usage.
        Assert.Equal("OpenAI (Codex)", facet.Label);
    }

    [Fact]
    public void A_key_under_two_labels_is_not_double_counted_in_the_stack()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, accessKey: "openai-codex", accessLabel: "OpenAI (Codex)", tokens: 500),
            Row(0, accessKey: "openai-codex", accessLabel: "OpenAI", tokens: 300),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query());

        Assert.Single(result.ChartLegend);
        Assert.Equal(800, result.Chart[0].Tokens);
        Assert.Equal(800, result.Chart[0].Segments.Sum(segment => segment.Tokens));
        Assert.Equal(result.TotalTokens, result.Chart[0].Tokens);
    }

    [Fact]
    public void Model_trends_line_up_with_the_chart_buckets()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, modelKey: "opus", tokens: 100),
            Row(2, modelKey: "opus", tokens: 300),
            Row(1, modelKey: "haiku", tokens: 50),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(2),
            }
        );

        Assert.Equal(3, result.Chart.Count);
        UsageModelTrend opus = result.ModelTrends.First(trend => trend.Key == "opus");
        Assert.Equal(result.Chart.Count, opus.BucketTokens.Count);
        Assert.Equal([100, 0, 300], opus.BucketTokens);
        Assert.Equal(400, opus.Tokens);
        Assert.Equal(300, opus.PeakBucketTokens);
        Assert.Equal(2, opus.ActiveBuckets);
        Assert.Equal(Today, opus.FirstUsed);
        Assert.Equal(Today.AddDays(2), opus.LastUsed);
    }

    [Fact]
    public void Composition_counts_what_matched_not_what_could_be_picked()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, toolKey: "droid", modelKey: "claude-sonnet-5", projectKey: "alpha", tokens: 400),
            Row(0, toolKey: "droid", modelKey: "glm-5.2", projectKey: "beta", tokens: 100),
            Row(1, toolKey: "codex", modelKey: "gpt-5.6", projectKey: "gamma", tokens: 900),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(1),
                Harnesses = ["droid"],
            }
        );

        // Facets deliberately still offer codex so the selection can be
        // widened; the composition must report only what actually matched.
        Assert.Equal(2, result.Facets.Harnesses.Count);
        Assert.Equal(1, result.Composition.Harnesses);
        Assert.Equal(2, result.Composition.Models);
        Assert.Equal(2, result.Composition.Projects);
        Assert.Equal(1, result.Composition.Providers);
        Assert.Equal(1, result.Composition.ActiveBuckets);
    }

    [Fact]
    public void Model_trends_are_ranked_and_share_sums_to_the_window()
    {
        UsageAnalyticsRecord[] records = [Row(0, modelKey: "small", tokens: 100), Row(0, modelKey: "big", tokens: 300)];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query());

        Assert.Equal(["big", "small"], result.ModelTrends.Select(trend => trend.Key));
        Assert.Equal(75, result.ModelTrends[0].SharePercent, precision: 6);
        Assert.Equal(100, result.ModelTrends.Sum(trend => trend.SharePercent), precision: 6);
    }

    [Fact]
    public void Model_trends_honour_the_active_filters()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, toolKey: "droid", toolLabel: "Droid", modelKey: "claude-sonnet-5", tokens: 400),
            Row(0, toolKey: "droid", toolLabel: "Droid", modelKey: "glm-5.2", tokens: 100),
            Row(0, toolKey: "codex", toolLabel: "Codex", modelKey: "gpt-5.6", tokens: 900),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query() with { Harnesses = ["droid"] });

        Assert.Equal(["claude-sonnet-5", "glm-5.2"], result.ModelTrends.Select(trend => trend.Key));
        Assert.Equal(80, result.ModelTrends[0].SharePercent, precision: 6);
    }

    [Fact]
    public void Series_by_model_legend_is_a_rank_prefix_of_the_model_trends()
    {
        // UsagePage colours the Model Mix by row rank and the chart by legend
        // index, both off ChartSeriesBrushResolver. That only yields one colour
        // per model because these two lists are ranked identically — assert it,
        // because a divergence here would silently repaint the mix.
        UsageAnalyticsRecord[] records =
        [
            Row(0, modelKey: "claude-opus-5", tokens: 500),
            Row(0, modelKey: "gpt-5.6-sol", tokens: 900),
            Row(0, modelKey: "claude-fable-5", tokens: 700),
            Row(0, modelKey: "claude-sonnet-5", tokens: 300),
            Row(0, modelKey: "codex-auto-review", tokens: 100),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                ChartSeries = UsageChartSeriesDimension.Model,
            }
        );

        string[] named = result.ChartLegend.Where(item => !item.IsOthers).Select(item => item.Key).ToArray();

        Assert.NotEmpty(named);
        Assert.Equal(named, result.ModelTrends.Take(named.Length).Select(trend => trend.Key));
    }

    [Fact]
    public void Ties_in_the_model_ranking_break_the_same_way_on_both_surfaces()
    {
        // Equal token counts are the case where two differently-written sorts
        // drift apart; both must fall through to the same label tie-break.
        UsageAnalyticsRecord[] records =
        [
            Row(0, modelKey: "b-model", tokens: 400),
            Row(0, modelKey: "a-model", tokens: 400),
            Row(0, modelKey: "c-model", tokens: 400),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                ChartSeries = UsageChartSeriesDimension.Model,
            }
        );

        string[] named = result.ChartLegend.Where(item => !item.IsOthers).Select(item => item.Key).ToArray();

        Assert.Equal(named, result.ModelTrends.Take(named.Length).Select(trend => trend.Key));
    }

    [Fact]
    public void Weekly_grain_buckets_model_trends_by_week()
    {
        UsageAnalyticsRecord[] records =
        [
            Row(0, modelKey: "opus", tokens: 100),
            Row(1, modelKey: "opus", tokens: 200),
            Row(8, modelKey: "opus", tokens: 400),
        ];

        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(
            records,
            Query() with
            {
                Through = Today.AddDays(8),
                TimeGrain = UsageTimeGrain.Week,
            }
        );

        UsageModelTrend opus = Assert.Single(result.ModelTrends);
        Assert.Equal(result.Chart.Count, opus.BucketTokens.Count);
        Assert.Equal(700, opus.BucketTokens.Sum());
        Assert.Equal(opus.Tokens, opus.BucketTokens.Sum());
    }

    [Theory]
    [InlineData(30, 40)]
    [InlineData(52, 12)]
    [InlineData(365, 3)]
    [InlineData(7, 90)]
    public void Date_axis_always_labels_the_last_bucket_without_crowding(int count, double itemWidth)
    {
        bool[] labelled = UsageChartLayout.LabelledBuckets(count, itemWidth);

        Assert.Equal(count, labelled.Length);
        Assert.True(labelled[0]);
        Assert.True(labelled[^1]);

        // Consecutive labels must be far enough apart in real pixels that the
        // text they carry cannot collide.
        double needed = itemWidth >= UsageChartLayout.WideLabelWidth ? 62 : 30;
        int[] positions = Enumerable.Range(0, count).Where(index => labelled[index]).ToArray();
        foreach (int gap in positions.Zip(positions.Skip(1), (first, second) => second - first))
        {
            Assert.True(
                gap * itemWidth >= needed,
                $"labels {gap} buckets apart at {itemWidth}px pitch leaves only {gap * itemWidth}px for {needed}px of text"
            );
        }
    }

    [Fact]
    public void Date_axis_survives_an_empty_window()
    {
        Assert.Empty(UsageChartLayout.LabelledBuckets(0, 30));
    }

    private static readonly DateOnly Today = new(2026, 7, 1);

    private static UsageAnalyticsQuery Query() =>
        new(Today, Today, UsageTimeGrain.Day, UsageBreakdownDimension.Provider);

    private static UsageAnalyticsRecord Row(
        int dayOffset,
        string accessKey = "openai",
        string accessLabel = "OpenAI",
        string toolKey = "codex",
        string toolLabel = "Codex",
        string projectKey = "alpha",
        string projectLabel = "Alpha",
        string modelKey = "gpt",
        long tokens = 10,
        decimal? cost = 0.1m
    ) =>
        new(
            Today.AddDays(dayOffset),
            accessKey,
            accessLabel,
            toolKey,
            toolLabel,
            projectKey,
            projectLabel,
            modelKey,
            modelKey,
            tokens,
            tokens,
            0,
            0,
            0,
            0,
            cost
        );
}
