// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;

namespace AiLimits.Presentation.WinUI.Theming;

/// <summary>One color role with a dark and light variant, each an #RRGGBB hex string.</summary>
public sealed record ThemeColor(string Dark, string Light);

/// <summary>
/// A named theme's full color model, mirroring the OpenCode theme role set.
/// Every color carries both a dark and a light variant so the mode selector
/// (Dark / Light / System) can switch independently of the chosen theme.
///
/// A theme also names the fonts it looks best in. Those are defaults only: an
/// explicit pick in Settings always wins, and most themes are happy with the
/// catalog defaults.
/// </summary>
public sealed record ThemePalette(
    string Id,
    string DisplayName,
    ThemeColor Primary,
    ThemeColor Secondary,
    ThemeColor Accent,
    ThemeColor Background,
    ThemeColor BackgroundPanel,
    ThemeColor BackgroundElement,
    ThemeColor Text,
    ThemeColor TextMuted,
    ThemeColor Border,
    ThemeColor BorderActive,
    ThemeColor BorderSubtle,
    ThemeColor Success,
    ThemeColor Warning,
    ThemeColor Error,
    ThemeColor Info,
    IReadOnlyList<ThemeColor> ChartSeries,
    string ContentFontId = FontCatalog.DefaultContentFontId,
    string MetricFontId = FontCatalog.DefaultMetricFontId
);
