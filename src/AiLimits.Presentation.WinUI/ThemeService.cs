// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Presentation.WinUI.Theming;
using Microsoft.UI.Xaml;

namespace AiLimits.Presentation.WinUI;

/// <summary>A chosen theme plus an appearance mode. Mode Default means "follow the OS".</summary>
public sealed record ThemePreference(string ThemeId, ElementTheme Mode);

public static class ThemeService
{
    private static readonly string PreferencePath = AppDataDirectory.File("theme.json");

    private static readonly string LegacyPreferencePath = AppDataDirectory.File("theme.preference");

    private static ResourceDictionary? _injected;
    private static ThemePalette _palette = ThemeCatalog.Default;
    private static WeakReference<FrameworkElement>? _root;
    private static bool _applied;

    /// <summary>Resolved appearance mode last applied (Default resolves to the OS theme).</summary>
    public static ElementTheme Current { get; private set; } = ElementTheme.Default;

    public static ThemePreference CurrentPreference { get; private set; } =
        new(ThemeCatalog.Default.Id, ElementTheme.Default);

    /// <summary>Effective colors for the title bar and tray, recomputed on every Apply.</summary>
    public static ResolvedThemeColors CurrentColors { get; private set; } =
        ThemeDictionaryBuilder.ResolveColors(ThemeCatalog.Default, dark: true);

    /// <summary>Raised after Apply so window chrome (title bar, tray) can restyle.</summary>
    public static event Action<ElementTheme>? Applied;

    public static ThemePreference LoadPreference()
    {
        try
        {
            if (File.Exists(PreferencePath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PreferencePath));
                string themeId =
                    document.RootElement.TryGetProperty("theme", out JsonElement themeElement)
                    && themeElement.ValueKind == JsonValueKind.String
                        ? themeElement.GetString() ?? ThemeCatalog.Default.Id
                        : ThemeCatalog.Default.Id;
                ElementTheme mode =
                    document.RootElement.TryGetProperty("mode", out JsonElement modeElement)
                    && modeElement.ValueKind == JsonValueKind.String
                        ? ParseMode(modeElement.GetString())
                        : ElementTheme.Default;
                return new ThemePreference(ThemeCatalog.Resolve(themeId).Id, mode);
            }
            // One-time migration from the legacy mode-only file.
            if (File.Exists(LegacyPreferencePath))
            {
                ElementTheme mode = ParseMode(File.ReadAllText(LegacyPreferencePath).Trim());
                var migrated = new ThemePreference(ThemeCatalog.Default.Id, mode);
                Save(migrated);
                try
                {
                    File.Delete(LegacyPreferencePath);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
                return migrated;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
        return new ThemePreference(ThemeCatalog.Default.Id, ElementTheme.Default);
    }

    public static void Apply(FrameworkElement root, ThemePreference preference, bool persist)
    {
        _root = new WeakReference<FrameworkElement>(root);
        _palette = ThemeCatalog.Resolve(preference.ThemeId);
        CurrentPreference = preference with { ThemeId = _palette.Id };
        Current = preference.Mode;

        try
        {
            var app = Microsoft.UI.Xaml.Application.Current;
            if (app is not null)
            {
                ResourceDictionary generated = ThemeDictionaryBuilder.Build(
                    _palette,
                    FontPreference.ContentSource(_palette),
                    FontPreference.MetricSource(_palette)
                );
                if (_injected is not null)
                {
                    app.Resources.MergedDictionaries.Remove(_injected);
                }
                app.Resources.MergedDictionaries.Add(generated);
                _injected = generated;
            }

            if (!_applied)
            {
                // Initial apply: the generated dictionary is brand new, so a single
                // set is enough and avoids flipping theme on an unloaded root.
                root.RequestedTheme = preference.Mode;
            }
            else
            {
                // Runtime switch: WinUI only re-evaluates {ThemeResource} when the
                // effective ElementTheme transitions, so force it through the
                // opposite concrete theme first, then the target.
                ElementTheme opposite = root.ActualTheme == ElementTheme.Dark ? ElementTheme.Light : ElementTheme.Dark;
                root.RequestedTheme = opposite;
                root.RequestedTheme = preference.Mode;
            }
            _applied = true;
        }
        catch
        {
            // Never let a theme swap take the app down; fall back to whatever
            // RequestedTheme the root already has.
        }

        CurrentColors = ThemeDictionaryBuilder.ResolveColors(_palette, dark: IsEffectivelyDark(root, preference.Mode));
        Applied?.Invoke(preference.Mode);

        if (persist)
        {
            Save(CurrentPreference);
        }
    }

    /// <summary>Rebuild and reapply the current theme (e.g. after a font change).</summary>
    public static void Reapply(FrameworkElement root) => Apply(root, CurrentPreference, persist: false);

    /// <summary>Recompute colors and re-raise Applied when the OS theme flips while in System mode.</summary>
    public static void NotifySystemThemeChanged()
    {
        if (Current != ElementTheme.Default || _root is null || !_root.TryGetTarget(out FrameworkElement? root))
        {
            return;
        }
        CurrentColors = ThemeDictionaryBuilder.ResolveColors(_palette, dark: root.ActualTheme == ElementTheme.Dark);
        Applied?.Invoke(Current);
    }

    // Theme-scoped brushes live inside ThemeDictionaries; a plain lookup on
    // Application.Current.Resources can miss them, so walk them explicitly.
    public static object? ResolveThemeResource(string key)
    {
        var app = Microsoft.UI.Xaml.Application.Current;
        if (app is null)
        {
            return null;
        }
        ElementTheme effective = Current;
        if (effective == ElementTheme.Default)
        {
            effective =
                _root is not null && _root.TryGetTarget(out FrameworkElement? root)
                    ? (root.ActualTheme == ElementTheme.Dark ? ElementTheme.Dark : ElementTheme.Light)
                    : (app.RequestedTheme == ApplicationTheme.Dark ? ElementTheme.Dark : ElementTheme.Light);
        }
        string themeKey = effective == ElementTheme.Dark ? "Dark" : "Light";
        if (ResolveThemed(app.Resources, key, themeKey) is { } themedValue)
        {
            return themedValue;
        }
        return app.Resources.TryGetValue(key, out var value) ? value : null;
    }

    private static bool IsEffectivelyDark(FrameworkElement root, ElementTheme mode) =>
        mode switch
        {
            ElementTheme.Dark => true,
            ElementTheme.Light => false,
            _ => root.ActualTheme == ElementTheme.Dark,
        };

    private static ElementTheme ParseMode(string? value) =>
        value switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };

    private static void Save(ThemePreference preference)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(
                PreferencePath,
                JsonSerializer.Serialize(
                    new Dictionary<string, string>
                    {
                        ["theme"] = preference.ThemeId,
                        ["mode"] = preference.Mode.ToString(),
                    }
                )
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private static object? ResolveThemed(ResourceDictionary dictionary, string key, string themeKey)
    {
        if (
            dictionary.ThemeDictionaries.TryGetValue(themeKey, out var scoped)
            && scoped is ResourceDictionary themed
            && themed.TryGetValue(key, out var themedValue)
        )
        {
            return themedValue;
        }
        for (int i = dictionary.MergedDictionaries.Count - 1; i >= 0; i--)
        {
            if (ResolveThemed(dictionary.MergedDictionaries[i], key, themeKey) is { } found)
            {
                return found;
            }
        }
        return null;
    }
}
