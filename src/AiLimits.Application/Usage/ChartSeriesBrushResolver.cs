// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Usage;

/// <summary>
/// Maps a legend entry's position to the theme resource key for its brush.
/// Non-Others entries are assigned <c>ChartSeries1Brush</c> through
/// <c>ChartSeries6Brush</c> in legend order; the pooled "Others" bucket and
/// any overflow past the six-slot ramp use <c>ChartSeriesOthersBrush</c>.
/// This keeps categorical chart colors readable (especially Series-by-model,
/// where many same-vendor models would otherwise collapse into one brand hue)
/// while <c>ProviderColors</c> stays the source of truth for brand identity
/// on product chrome (cards, connections, tray, model-mix accent strips).
/// </summary>
public static class ChartSeriesBrushResolver
{
    private static readonly string[] SeriesKeys =
    [
        "ChartSeries1Brush",
        "ChartSeries2Brush",
        "ChartSeries3Brush",
        "ChartSeries4Brush",
        "ChartSeries5Brush",
        "ChartSeries6Brush",
    ];

    /// <summary>
    /// Returns the theme resource key for the brush that should colour the
    /// legend entry at <paramref name="legendIndex"/>. The "Others" bucket
    /// and any entry past the six-slot ramp resolve to
    /// <c>ChartSeriesOthersBrush</c>.
    /// </summary>
    public static string ResolveResourceKey(int legendIndex, bool isOthers) =>
        isOthers || legendIndex >= SeriesKeys.Length
            ? "ChartSeriesOthersBrush"
            : SeriesKeys[legendIndex];
}