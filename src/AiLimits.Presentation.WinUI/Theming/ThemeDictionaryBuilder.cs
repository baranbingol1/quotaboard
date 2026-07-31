// SPDX-License-Identifier: Apache-2.0
using System;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AiLimits.Presentation.WinUI.Theming;

/// <summary>Effective colors for surfaces that can't read XAML brushes (title bar, tray).</summary>
public sealed record ResolvedThemeColors(
    Color TitleBarBackground,
    Color TitleBarForeground,
    Color TitleBarInactiveForeground,
    Color TitleBarHover,
    Color PopupBackground,
    Color PopupBorder,
    Color TextMuted,
    Color Healthy,
    Color Warning,
    Color Critical,
    bool IsDarkBackground);

/// <summary>
/// Materializes a <see cref="ThemePalette"/> into a WinUI ResourceDictionary with
/// Light/Dark/HighContrast theme dictionaries carrying every brush key the app's
/// XAML and converters reference. One C# palette is the single source of truth,
/// also feeding the non-XAML title bar and tray surfaces via <see cref="ResolveColors"/>.
/// </summary>
public static class ThemeDictionaryBuilder
{
    public static ResourceDictionary Build(ThemePalette palette, string contentFontSource, string metricFontSource)
    {
        var dictionary = new ResourceDictionary();
        dictionary.ThemeDictionaries["Light"] = BuildVariant(palette, dark: false, contentFontSource, metricFontSource);
        dictionary.ThemeDictionaries["Dark"] = BuildVariant(palette, dark: true, contentFontSource, metricFontSource);
        // Do not add a generated HighContrast variant. The static theme
        // dictionary maps these semantic slots to Windows system colors; a
        // palette-generated variant merged last would override those mappings.
        return dictionary;
    }

    public static ResolvedThemeColors ResolveColors(ThemePalette palette, bool dark)
    {
        Color background = Pick(palette.Background, dark);
        Color element = Pick(palette.BackgroundElement, dark);
        return new ResolvedThemeColors(
            TitleBarBackground: background,
            TitleBarForeground: Pick(palette.Text, dark),
            TitleBarInactiveForeground: Pick(palette.TextMuted, dark),
            TitleBarHover: element,
            PopupBackground: Pick(palette.BackgroundPanel, dark),
            PopupBorder: Pick(palette.Border, dark),
            TextMuted: Pick(palette.TextMuted, dark),
            Healthy: Pick(palette.Success, dark),
            Warning: Pick(palette.Warning, dark),
            Critical: Pick(palette.Error, dark),
            IsDarkBackground: IsDark(background));
    }

    private static ResourceDictionary BuildVariant(ThemePalette palette, bool dark, string contentFontSource, string metricFontSource)
    {
        Color background = Pick(palette.Background, dark);
        Color panel = Pick(palette.BackgroundPanel, dark);
        Color element = Pick(palette.BackgroundElement, dark);
        Color text = Pick(palette.Text, dark);
        Color textMuted = Pick(palette.TextMuted, dark);
        Color border = Pick(palette.Border, dark);
        Color borderActive = Pick(palette.BorderActive, dark);
        Color success = Pick(palette.Success, dark);
        Color warning = Pick(palette.Warning, dark);
        Color error = Pick(palette.Error, dark);
        Color info = Pick(palette.Info, dark);
        Color primary = Pick(palette.Primary, dark);
        Color accent = Pick(palette.Accent, dark);

        var dictionary = new ResourceDictionary
        {
            ["PageBackgroundBrush"] = Brush(background),
            ["SurfaceBrush"] = Brush(panel),
            ["SurfaceSecondaryBrush"] = Brush(element),
            ["SurfaceStrokeBrush"] = Brush(border),
            ["SurfaceStrokeActiveBrush"] = Brush(borderActive),
            ["TextPrimaryBrush"] = Brush(text),
            ["TextSecondaryBrush"] = Brush(textMuted),
            ["TextQuietBrush"] = Brush(Blend(textMuted, background, 0.15)),
            ["HealthyBrush"] = Brush(success),
            ["WarningBrush"] = Brush(warning),
            ["CriticalBrush"] = Brush(error),
            ["FocusBrush"] = Brush(primary),
            ["AccentBrush"] = Brush(accent),
            ["PillPositiveBackgroundBrush"] = Brush(Blend(success, panel, 0.82)),
            ["PillPositiveForegroundBrush"] = Brush(success),
            ["PillInfoBackgroundBrush"] = Brush(Blend(info, panel, 0.82)),
            ["PillInfoForegroundBrush"] = Brush(info),
            ["PillWarningBackgroundBrush"] = Brush(Blend(warning, panel, 0.82)),
            ["PillWarningForegroundBrush"] = Brush(warning),
            ["PillNeutralBackgroundBrush"] = Brush(element),
            ["PillNeutralForegroundBrush"] = Brush(textMuted),
            ["ChartBarBrush"] = VerticalGradient(primary, Blend(primary, background, 0.45)),
            ["ChartSeriesOthersBrush"] = Brush(Blend(textMuted, background, 0.2)),
            ["IncidentBackgroundBrush"] = Brush(WithAlpha(warning, 0x24)),
            ["IncidentBorderBrush"] = Brush(WithAlpha(warning, 0x99)),
            ["IncidentForegroundBrush"] = Brush(warning),
        };
        for (int index = 0; index < 6; index++)
        {
            ThemeColor series = palette.ChartSeries.Count > index ? palette.ChartSeries[index] : palette.Primary;
            dictionary[$"ChartSeries{index + 1}Brush"] = Brush(Pick(series, dark));
        }
        dictionary["ContentFont"] = new FontFamily(contentFontSource);
        dictionary["MetricFont"] = new FontFamily(metricFontSource);
        return dictionary;
    }

    private static Color Pick(ThemeColor color, bool dark) => Parse(dark ? color.Dark : color.Light);

    internal static Color Parse(string hex)
    {
        string value = hex.TrimStart('#');
        if (value.Length == 6)
        {
            byte r = byte.Parse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(0xFF, r, g, b);
        }
        if (value.Length == 8)
        {
            byte a = byte.Parse(value.Substring(0, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte r = byte.Parse(value.Substring(2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte g = byte.Parse(value.Substring(4, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            byte b = byte.Parse(value.Substring(6, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return Color.FromArgb(a, r, g, b);
        }
        return Color.FromArgb(0xFF, 0x80, 0x80, 0x80);
    }

    internal static Color Blend(Color from, Color to, double amount)
    {
        double t = Math.Clamp(amount, 0.0, 1.0);
        return Color.FromArgb(
            0xFF,
            (byte)Math.Round(from.R * (1 - t) + to.R * t),
            (byte)Math.Round(from.G * (1 - t) + to.G * t),
            (byte)Math.Round(from.B * (1 - t) + to.B * t));
    }

    internal static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    private static bool IsDark(Color color)
    {
        // Rec. 601 relative luminance; below mid = dark background.
        double luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return luminance < 0.5;
    }

    private static SolidColorBrush Brush(Color color) => new(color);

    private static LinearGradientBrush VerticalGradient(Color top, Color bottom)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0), EndPoint = new Windows.Foundation.Point(0, 1) };
        brush.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = bottom, Offset = 1 });
        return brush;
    }
}
