// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Usage;

namespace AiLimits.Tests;

public sealed class ChartSeriesBrushResolverTests
{
    [Theory]
    [InlineData(0, false, "ChartSeries1Brush")]
    [InlineData(1, false, "ChartSeries2Brush")]
    [InlineData(2, false, "ChartSeries3Brush")]
    [InlineData(3, false, "ChartSeries4Brush")]
    [InlineData(4, false, "ChartSeries5Brush")]
    [InlineData(5, false, "ChartSeries6Brush")]
    public void Non_Others_legend_entries_map_to_ordered_series_brushes(
        int legendIndex, bool isOthers, string expected)
    {
        Assert.Equal(expected, ChartSeriesBrushResolver.ResolveResourceKey(legendIndex, isOthers));
    }

    [Fact]
    public void Others_legend_entry_maps_to_others_brush()
    {
        Assert.Equal("ChartSeriesOthersBrush",
            ChartSeriesBrushResolver.ResolveResourceKey(0, true));
    }

    [Theory]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(100)]
    public void Overflow_past_six_slots_maps_to_others_brush(int legendIndex)
    {
        Assert.Equal("ChartSeriesOthersBrush",
            ChartSeriesBrushResolver.ResolveResourceKey(legendIndex, false));
    }

    [Fact]
    public void Others_at_any_index_still_maps_to_others_brush()
    {
        Assert.Equal("ChartSeriesOthersBrush",
            ChartSeriesBrushResolver.ResolveResourceKey(5, true));
    }
}