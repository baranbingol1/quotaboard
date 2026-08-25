// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Alerts;

namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Persistent hook for the Settings quota-alert controls. The file carries the
/// full <see cref="AlertPolicyOptions"/>; older installs stored only a bare
/// True/False, which is still honored. Missing or unreadable preferences
/// intentionally fall back to the suggested policy.
/// </summary>
public static class QuotaAlertPreference
{
    private static readonly string PreferencePath = AppDataDirectory.File("quota-alerts.preference");

    public static AlertPolicyOptions Load()
    {
        try
        {
            return File.Exists(PreferencePath)
                ? AlertPolicyOptions.Parse(File.ReadAllText(PreferencePath))
                : AlertPolicyOptions.Suggested;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return AlertPolicyOptions.Suggested;
        }
    }

    public static void Save(AlertPolicyOptions options)
    {
        try
        {
            PreferenceFile.WriteAtomic(PreferencePath, options.Clamped().Serialize());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public static bool LoadEnabled() => Load().Enabled;

    public static void SaveEnabled(bool enabled)
    {
        Save(Load() with { Enabled = enabled });
    }
}
