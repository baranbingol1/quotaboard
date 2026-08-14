// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Screenshot-privacy switch: when enabled, account emails render masked
/// everywhere. On by default, because the first thing a new user is likely to
/// do with a dashboard like this is screenshot it — revealing an address is a
/// choice they should make deliberately, not one the default makes for them.
/// A missing or unreadable preference therefore stays masked.
/// </summary>
public static class EmailPrivacyPreference
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard",
        "email-privacy.preference"
    );
    private static bool _enabled = LoadCore();

    public static bool Enabled => _enabled;
    public static event Action<bool>? Changed;

    /// <summary>
    /// Masks an account label ("jane.doe@example.com" → "j•••@•••") while the
    /// preference is on. Non-email labels (localized fallbacks like "No local
    /// account") pass through untouched so status text stays readable.
    /// </summary>
    public static string Apply(string account)
    {
        if (!_enabled || string.IsNullOrEmpty(account))
        {
            return account;
        }
        int at = account.IndexOf('@');
        return at <= 0 ? account : account[0] + "•••@•••";
    }

    public static void Save(bool enabled)
    {
        if (_enabled == enabled)
        {
            return;
        }
        _enabled = enabled;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, enabled ? "On" : "Off");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        Changed?.Invoke(enabled);
    }

    private static bool LoadCore()
    {
        try
        {
            // Only an explicit "Off" unmasks: an absent file is a first run and
            // an unreadable one is a failure, and neither is consent to show an
            // address.
            return !File.Exists(PreferencePath)
                || !string.Equals(File.ReadAllText(PreferencePath).Trim(), "Off", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
