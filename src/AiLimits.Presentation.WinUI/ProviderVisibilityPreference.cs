// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Preferences;

namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Persists the "Show on Overview" choice per provider id. Stored next to the
/// other preference files; parse/serialize semantics live in
/// <see cref="ProviderVisibilitySet"/> so they stay unit-testable.
/// </summary>
public static class ProviderVisibilityPreference
{
    private static readonly string PreferencePath = AppDataDirectory.File("provider-visibility.json");

    public static ProviderVisibilitySet Load()
    {
        try
        {
            return File.Exists(PreferencePath)
                ? ProviderVisibilitySet.Parse(File.ReadAllText(PreferencePath))
                : ProviderVisibilitySet.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ProviderVisibilitySet.Empty;
        }
    }

    public static void SetVisible(string providerId, bool visible)
    {
        try
        {
            PreferenceFile.WriteAtomic(PreferencePath, Load().WithVisibility(providerId, visible).Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
