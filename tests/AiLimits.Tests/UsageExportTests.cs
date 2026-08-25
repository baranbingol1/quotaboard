// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Application.Usage;
using Xunit;

namespace AiLimits.Tests;

public sealed class UsageExportTests
{
    private static UsageAnalyticsRecord Record(
        DateOnly day,
        string provider = "codex",
        string model = "gpt-5",
        long tokens = 100,
        decimal? cost = null,
        string? project = null
    ) =>
        new(
            day,
            provider,
            provider,
            "cli",
            "CLI",
            project ?? "proj",
            project ?? "Project",
            model,
            model,
            tokens,
            60,
            40,
            0,
            0,
            0,
            cost
        );

    [Fact]
    public void Csv_neutralizes_fields_that_a_spreadsheet_would_run_as_a_formula()
    {
        // A local directory name is legitimate input, and Excel executes a
        // leading = when the export is opened.
        string csv = UsageExport.Csv([Record(new DateOnly(2026, 8, 20), project: "=HYPERLINK(\"http://x\",\"open\")")]);

        string row = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.DoesNotContain(",=HYPERLINK", row, StringComparison.Ordinal);
        Assert.Contains("\"'=HYPERLINK", row, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("=cmd")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData("@SUM(A1)")]
    public void Csv_prefixes_every_formula_lead_in(string value)
    {
        string csv = UsageExport.Csv([Record(new DateOnly(2026, 8, 20), project: value)]);

        string row = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Contains("'" + value, row, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_leaves_ordinary_fields_untouched()
    {
        string csv = UsageExport.Csv([Record(new DateOnly(2026, 8, 20), project: "my-project")]);

        string row = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries)[1];
        Assert.Contains(",my-project,", row, StringComparison.Ordinal);
        Assert.DoesNotContain("'my-project", row, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_writes_header_then_rows_with_invariant_numbers()
    {
        string csv = UsageExport.Csv([Record(new DateOnly(2026, 8, 20), cost: 1.5m)]);

        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(
            "day,provider,harness,project,model,tokens,inputTokens,outputTokens,cacheReadTokens,cacheWriteTokens,reasoningTokens,apiEquivalentCostUsd",
            lines[0]
        );
        Assert.EndsWith(",100,60,40,0,0,0,1.5", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2026-08-20,", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_escapes_commas_quotes_and_newlines()
    {
        string csv = UsageExport.Csv([Record(new DateOnly(2026, 8, 20), provider: "a,b \"quoted\"")]);

        Assert.Contains("\"a,b \"\"quoted\"\"\"", csv, StringComparison.Ordinal);
    }

    [Fact]
    public void Csv_orders_rows_by_day_then_provider()
    {
        string csv = UsageExport.Csv(
            [
                Record(new DateOnly(2026, 8, 21), provider: "zeta"),
                Record(new DateOnly(2026, 8, 20), provider: "zeta"),
                Record(new DateOnly(2026, 8, 20), provider: "alpha"),
            ]
        );

        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.StartsWith("2026-08-20,alpha", lines[1], StringComparison.Ordinal);
        Assert.StartsWith("2026-08-20,zeta", lines[2], StringComparison.Ordinal);
        Assert.StartsWith("2026-08-21,zeta", lines[3], StringComparison.Ordinal);
    }

    [Fact]
    public void Json_is_valid_and_round_trips_the_rows()
    {
        var query = new UsageAnalyticsQuery(
            new DateOnly(2026, 7, 22),
            new DateOnly(2026, 8, 20),
            UsageTimeGrain.Day,
            UsageBreakdownDimension.Provider
        );
        string json = UsageExport.Json([Record(new DateOnly(2026, 8, 20), tokens: 1234, cost: 2.25m)], query);

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        var root = document.RootElement;
        Assert.Equal("2026-07-22", root.GetProperty("window").GetProperty("from").GetString());
        Assert.Equal(1, root.GetProperty("recordCount").GetInt32());
        var record = root.GetProperty("records")[0];
        Assert.Equal("2026-08-20", record.GetProperty("day").GetString());
        Assert.Equal(1234, record.GetProperty("tokens").GetInt64());
        Assert.Equal(2.25m, record.GetProperty("apiEquivalentCostUsd").GetDecimal());
    }

    [Fact]
    public void Json_writes_null_for_unpriced_rows()
    {
        string json = UsageExport.Json([Record(new DateOnly(2026, 8, 20), cost: null)], Query());

        using System.Text.Json.JsonDocument document = System.Text.Json.JsonDocument.Parse(json);
        Assert.Equal(
            System.Text.Json.JsonValueKind.Null,
            document.RootElement.GetProperty("records")[0].GetProperty("apiEquivalentCostUsd").ValueKind
        );
    }

    [Fact]
    public void Matching_records_honor_query_filters_for_export()
    {
        UsageAnalyticsRecord[] records =
        [
            Record(new DateOnly(2026, 8, 20), provider: "codex"),
            Record(new DateOnly(2026, 8, 20), provider: "claude"),
        ];
        UsageAnalyticsResult result = UsageAnalyticsQueryEngine.Run(records, Query() with { Providers = ["claude"] });

        Assert.Equal(1, result.MatchingRecordCount);
        UsageAnalyticsRecord exported = Assert.Single(result.Records);
        Assert.Equal("claude", exported.ProviderKey);
    }

    [Theory]
    [InlineData(0, 100, 0)] // an empty day vanishes
    [InlineData(1, 10000, 2)] // sub-pixel usage stays visible as a 2px sliver
    [InlineData(100, 100, 210)] // the tallest bar fills the plot
    [InlineData(50, 100, 105)] // half scale
    public void Bar_height_keeps_every_nonempty_bucket_visible(long tokens, long maximum, double expectedHeight)
    {
        Assert.Equal(expectedHeight, UsageChartLayout.BarHeight(tokens, maximum, plotHeight: 210), precision: 3);
    }

    [Theory]
    [InlineData(0, 0)] // zero-token segments collapse
    [InlineData(100, 210)]
    [InlineData(25, 52.5)]
    public void Segment_height_is_proportional_without_a_floor(long tokens, double expectedHeight)
    {
        Assert.Equal(
            expectedHeight,
            UsageChartLayout.SegmentHeight(tokens, maximum: 100, plotHeight: 210),
            precision: 3
        );
    }

    [Fact]
    public void Csv_and_json_are_culture_independent()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
            string csv = UsageExport.Csv([Record(new DateOnly(2026, 8, 20), cost: 3.75m)]);
            string json = UsageExport.Json([Record(new DateOnly(2026, 8, 20), cost: 3.75m)], Query());

            Assert.Contains("3.75", csv, StringComparison.Ordinal);
            Assert.Contains("3.75", json, StringComparison.Ordinal);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    private static UsageAnalyticsQuery Query() =>
        new(new DateOnly(2026, 7, 22), new DateOnly(2026, 8, 20), UsageTimeGrain.Day, UsageBreakdownDimension.Provider);
}
