// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Presentation.WinUI.Theming;

namespace AiLimits.Presentation.WinUI;

/// <summary>The chosen content and metric font ids (null = match the theme).</summary>
public sealed record FontSelection(string? ContentFontId, string? MetricFontId);

/// <summary>Persists the font selection next to the other preference files.</summary>
public static class FontPreference
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard", "font.json");

    public static FontSelection Load()
    {
        try
        {
            if (File.Exists(PreferencePath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(PreferencePath));
                string? content = Read(document.RootElement, "content");
                string? metric = Read(document.RootElement, "metric");
                return new FontSelection(content, metric);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        return new FontSelection(null, null);
    }

    public static void Save(FontSelection selection)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            var payload = new Dictionary<string, string>();
            if (!string.IsNullOrWhiteSpace(selection.ContentFontId)) payload["content"] = selection.ContentFontId!;
            if (!string.IsNullOrWhiteSpace(selection.MetricFontId)) payload["metric"] = selection.MetricFontId!;
            File.WriteAllText(PreferencePath, JsonSerializer.Serialize(payload));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// The interface font to render with: the user's explicit pick if there is
    /// one, otherwise whatever <paramref name="palette"/> asks for, resolved
    /// down its fallback chain to something this machine actually has.
    /// </summary>
    public static string ContentSource(ThemePalette palette) =>
        FontCatalog.ResolveContentSource(Load().ContentFontId, palette.ContentFontId);

    /// <summary>The metric (monospace) font. See <see cref="ContentSource"/>.</summary>
    public static string MetricSource(ThemePalette palette) =>
        FontCatalog.ResolveMetricSource(Load().MetricFontId, palette.MetricFontId);

    private static string? Read(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
