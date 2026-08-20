// SPDX-License-Identifier: Apache-2.0
using AiLimits.Presentation.WinUI.Theming;

namespace AiLimits.Tests;

/// <summary>
/// Guards the light column of every palette. A theme transcribed from a dark-only
/// upstream definition used to carry its dark colors in both columns, which left
/// Light/System mode rendering dark text on a dark page.
/// </summary>
public sealed class ThemePaletteLightVariantTests
{
    /// <summary>
    /// Foreground roles that sit on the page or panel background as text, meter fills
    /// or pill labels, so a color that vanishes against the background is a real defect.
    /// </summary>
    private static readonly (string Role, Func<ThemePalette, ThemeColor> Select)[] ForegroundRoles =
    {
        ("Text", palette => palette.Text),
        ("TextMuted", palette => palette.TextMuted),
        ("Primary", palette => palette.Primary),
        ("Accent", palette => palette.Accent),
        ("Success", palette => palette.Success),
        ("Warning", palette => palette.Warning),
        ("Error", palette => palette.Error),
        ("Info", palette => palette.Info),
    };

    public static TheoryData<string> AllThemeIds
    {
        get
        {
            TheoryData<string> data = new();
            foreach (ThemePalette palette in ThemeCatalog.All)
            {
                data.Add(palette.Id);
            }
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(AllThemeIds))]
    public void Light_variant_uses_a_light_background_and_dark_variant_a_dark_one(string themeId)
    {
        ThemePalette palette = ThemeCatalog.Resolve(themeId);

        Assert.True(
            Luminance(palette.Background.Light) > 0.5,
            $"{palette.DisplayName}: light background {palette.Background.Light} is not light."
        );
        Assert.True(
            Luminance(palette.Background.Dark) < 0.5,
            $"{palette.DisplayName}: dark background {palette.Background.Dark} is not dark."
        );
    }

    [Theory]
    [MemberData(nameof(AllThemeIds))]
    public void Every_foreground_role_stays_visible_against_its_own_background(string themeId)
    {
        ThemePalette palette = ThemeCatalog.Resolve(themeId);

        foreach ((string role, Func<ThemePalette, ThemeColor> select) in ForegroundRoles)
        {
            ThemeColor color = select(palette);
            // 2.0:1 is deliberately below the WCAG thresholds: several palettes ship
            // upstream light accents in the 2.3-3.0 range and those stay as authored.
            // This only catches a dark-mode color left sitting on a light background.
            AssertVisible(palette, role, "light", color.Light, palette.Background.Light);
            AssertVisible(palette, role, "dark", color.Dark, palette.Background.Dark);
        }

        for (int index = 0; index < palette.ChartSeries.Count; index++)
        {
            ThemeColor series = palette.ChartSeries[index];
            AssertVisible(palette, $"ChartSeries{index + 1}", "light", series.Light, palette.Background.Light);
            AssertVisible(palette, $"ChartSeries{index + 1}", "dark", series.Dark, palette.Background.Dark);
        }
    }

    private static void AssertVisible(
        ThemePalette palette,
        string role,
        string variant,
        string foreground,
        string background
    )
    {
        double contrast = Contrast(foreground, background);
        Assert.True(
            contrast >= 2.0,
            $"{palette.DisplayName}: {variant} {role} {foreground} on {background} is only {contrast:F2}:1."
        );
    }

    private static double Contrast(string foreground, string background)
    {
        double first = Luminance(foreground);
        double second = Luminance(background);
        double lighter = Math.Max(first, second);
        double darker = Math.Min(first, second);
        return (lighter + 0.05) / (darker + 0.05);
    }

    /// <summary>WCAG relative luminance of an #RRGGBB string.</summary>
    private static double Luminance(string hex)
    {
        string value = hex.TrimStart('#');
        double red = Channel(value[..2]);
        double green = Channel(value.Substring(2, 2));
        double blue = Channel(value.Substring(4, 2));
        return (0.2126 * red) + (0.7152 * green) + (0.0722 * blue);
    }

    private static double Channel(string component)
    {
        double channel = Convert.ToInt32(component, 16) / 255.0;
        return channel <= 0.03928 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
    }
}
