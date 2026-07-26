// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;

namespace AiLimits.Presentation.WinUI.Theming;

/// <summary>
/// A selectable font: a display name plus an ordered fallback chain. Each
/// candidate is either a system family name or a bundled <c>ms-appx://</c>
/// source; the last entry must be something every Windows install ships.
/// </summary>
public sealed record FontChoice(string Id, string DisplayName, IReadOnlyList<string> Candidates)
{
    /// <summary>The first candidate this machine can actually render.</summary>
    public string Source => FontCatalog.FirstAvailable(Candidates);
}

/// <summary>
/// The fonts offered by the font selector.
///
/// Defaults are the fonts Windows already has, so the app looks native out of
/// the box. The two bundled variable fonts stay on offer — they ship in
/// <c>Assets/Fonts</c>, so they are always selectable — but they are no longer
/// what an untouched install shows.
/// </summary>
public static class FontCatalog
{
    public const string BundledSansSource = "ms-appx:///Assets/Fonts/FamiljenGrotesk-VariableFont_wght.ttf#Familjen Grotesk";
    public const string BundledMonoSource = "ms-appx:///Assets/Fonts/AzeretMono-VariableFont_wght.ttf#Azeret Mono";

    /// <summary>Used when neither the user nor the theme names a font.</summary>
    public const string DefaultContentFontId = "segoe-ui-variable";
    public const string DefaultMetricFontId = "cascadia-mono";

    public static IReadOnlyList<FontChoice> ContentFonts { get; } =
    [
        // Windows 11's own UI face, with the Windows 10 fallback behind it.
        new("segoe-ui-variable", "Segoe UI Variable", ["Segoe UI Variable Text", "Segoe UI"]),
        new("segoe-ui", "Segoe UI", ["Segoe UI"]),
        new("cascadia-mono", "Cascadia Mono", ["Cascadia Mono", "Consolas"]),
        new("familjen-grotesk", "Familjen Grotesk", [BundledSansSource]),
        new("georgia", "Georgia", ["Georgia", "Segoe UI"]),
    ];

    public static IReadOnlyList<FontChoice> MetricFonts { get; } =
    [
        // Cascadia ships with Windows 11 and with Windows Terminal; Consolas is
        // on every Windows since Vista, so the chain can always terminate there.
        new("cascadia-mono", "Cascadia Mono", ["Cascadia Mono", "Consolas"]),
        new("cascadia-code", "Cascadia Code", ["Cascadia Code", "Cascadia Mono", "Consolas"]),
        new("consolas", "Consolas", ["Consolas"]),
        new("azeret-mono", "Azeret Mono", [BundledMonoSource]),
    ];

    /// <summary>
    /// Resolves the interface font source. <paramref name="selectedId"/> is the
    /// user's explicit pick and wins; a null one means "match the theme", which
    /// is what <paramref name="themeFontId"/> carries.
    /// </summary>
    public static string ResolveContentSource(string? selectedId, string? themeFontId = null) =>
        Resolve(ContentFonts, selectedId, themeFontId, DefaultContentFontId);

    /// <summary>Resolves the metric (monospace) font source. See <see cref="ResolveContentSource"/>.</summary>
    public static string ResolveMetricSource(string? selectedId, string? themeFontId = null) =>
        Resolve(MetricFonts, selectedId, themeFontId, DefaultMetricFontId);

    /// <summary>
    /// The first candidate that is installed, or the last one unconditionally.
    /// A bundled <c>ms-appx://</c> source ships with the app, so it never needs
    /// probing.
    /// </summary>
    internal static string FirstAvailable(IReadOnlyList<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return "Segoe UI";
        }
        for (int index = 0; index < candidates.Count - 1; index++)
        {
            string candidate = candidates[index];
            if (IsBundled(candidate) || SystemFonts.IsInstalled(candidate))
            {
                return candidate;
            }
        }
        return candidates[^1];
    }

    private static bool IsBundled(string candidate) =>
        candidate.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase);

    private static string Resolve(
        IReadOnlyList<FontChoice> choices,
        string? selectedId,
        string? themeFontId,
        string defaultId)
    {
        foreach (string? id in new[] { selectedId, themeFontId, defaultId })
        {
            if (Find(choices, id) is { } choice)
            {
                return choice.Source;
            }
        }
        return choices[0].Source;
    }

    private static FontChoice? Find(IReadOnlyList<FontChoice> choices, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }
        foreach (FontChoice choice in choices)
        {
            if (string.Equals(choice.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return choice;
            }
        }
        return null;
    }
}
