// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Usage;
using AiLimits.Presentation.WinUI.Theming;
using System.Xml.Linq;

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

    [Fact]
    public void Precision_observatory_chart_ramp_matches_the_default_palette()
    {
        string repositoryRoot = FindRepositoryRoot();
        XDocument document = XDocument.Load(Path.Combine(
            repositoryRoot,
            "src",
            "AiLimits.Presentation.WinUI",
            "Themes",
            "PrecisionObservatory.xaml"));
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        XElement themeDictionaries = document.Descendants()
            .Single(element => element.Name.LocalName == "ResourceDictionary.ThemeDictionaries");

        AssertVariant("Light", color => color.Light);
        AssertVariant("Dark", color => color.Dark);

        void AssertVariant(string key, Func<ThemeColor, string> expectedColor)
        {
            XElement dictionary = themeDictionaries.Elements()
                .Single(element => (string?)element.Attribute(xaml + "Key") == key);
            for (int index = 0; index < ThemeCatalog.Tokyonight.ChartSeries.Count; index++)
            {
                string brushKey = $"ChartSeries{index + 1}Brush";
                XElement brush = dictionary.Elements()
                    .Single(element => (string?)element.Attribute(xaml + "Key") == brushKey);
                string actual = ((string?)brush.Attribute("Color"))![3..];
                Assert.Equal(
                    expectedColor(ThemeCatalog.Tokyonight.ChartSeries[index]).TrimStart('#'),
                    actual,
                    ignoreCase: true);
            }
        }
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiLimits.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
