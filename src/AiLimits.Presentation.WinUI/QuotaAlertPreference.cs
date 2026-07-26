// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Persistent hook for the Settings quota-alert toggle. Missing or unreadable
/// preferences intentionally fall back to enabled.
/// </summary>
public static class QuotaAlertPreference
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard",
        "quota-alerts.preference");

    public static bool LoadEnabled()
    {
        try
        {
            if (!File.Exists(PreferencePath))
            {
                return true;
            }

            return !string.Equals(
                File.ReadAllText(PreferencePath).Trim(),
                bool.FalseString,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    public static void SaveEnabled(bool enabled)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, enabled.ToString());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
