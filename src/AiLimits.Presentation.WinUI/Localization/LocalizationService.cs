// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using Microsoft.Windows.ApplicationModel.Resources;
using Microsoft.Windows.Globalization;

namespace AiLimits.Presentation.WinUI.Localization;

/// <summary>
/// Owns QuotaBoard's persisted UI-language override. ApplySavedOverride must be
/// called before the first XAML surface is created so MRT selects the matching
/// .resw resources. Changing the override while the app is running requires
/// rebuilding the UI (or restarting the process) for existing x:Uid values.
/// </summary>
public static class LocalizationService
{
    private const string ResourceMapName = "AiLimits.Presentation.WinUI/Resources";
    public const string SystemLanguageTag = "";
    public const string EnglishLanguageTag = "en-US";
    public const string TurkishLanguageTag = "tr-TR";

    private const string SystemPreferenceValue = "system";
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentCulture;
    private static readonly CultureInfo SystemUiCulture = CultureInfo.CurrentUICulture;
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard",
        "language.txt");

    public static IReadOnlyList<string> SupportedLanguageTags { get; } =
        [EnglishLanguageTag, TurkishLanguageTag];

    public static string AppliedLanguageTag { get; private set; } = SystemLanguageTag;

    public static string SelectedLanguageTag => LoadSavedOverride();

    public static bool RestartRequired { get; private set; }

    public static event EventHandler? LanguageChanged;

    /// <summary>Applies the saved choice without marking the process for restart.</summary>
    public static string ApplySavedOverride()
    {
        string languageTag = LoadSavedOverride();
        ApplyOverride(languageTag);
        RestartRequired = false;
        return languageTag;
    }

    /// <summary>
    /// Persists and applies a supported BCP-47 tag. Pass null, an empty string,
    /// or "system" to resume following the Windows language preference list.
    /// Returns true when the selection changed.
    /// </summary>
    public static bool SetLanguageOverride(string? languageTag)
    {
        string normalized = Normalize(languageTag);
        if (string.Equals(normalized, SelectedLanguageTag, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        ApplyOverride(normalized);
        PersistOverride(normalized);
        RestartRequired = true;
        LanguageChanged?.Invoke(null, EventArgs.Empty);
        return true;
    }

    public static string GetLanguageDisplayName(string? languageTag) => Normalize(languageTag) switch
    {
        EnglishLanguageTag => GetString("Language_English"),
        TurkishLanguageTag => GetString("Language_Turkish"),
        _ => GetString("Language_SystemDefault"),
    };

    /// <summary>Looks up a string in the active Resources.resw resource map.</summary>
    public static string GetString(string resourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resourceId);
        try
        {
            // The resources live in a referenced WinUI library and are merged
            // into the executable PRI under a named subtree. Windows App SDK
            // 2.x needs both the active PRI path and that subtree here; the
            // one-argument overload treats its value as a resource file name.
            string value = new ResourceLoader(
                ResourceLoader.GetDefaultResourceFilePath(),
                ResourceMapName).GetString(resourceId);
            return string.IsNullOrWhiteSpace(value) ? resourceId : value;
        }
        catch
        {
            try
            {
                string value = new ResourceLoader(ResourceMapName).GetString(resourceId);
                return string.IsNullOrWhiteSpace(value) ? resourceId : value;
            }
            catch
            {
                try
                {
                    // The executable project can flatten referenced PRIs into
                    // its root map depending on publish settings.
                    string value = new ResourceLoader().GetString(resourceId);
                    return string.IsNullOrWhiteSpace(value) ? resourceId : value;
                }
                catch
                {
                    // A visible resource identifier is preferable to an empty
                    // control if a deployment is missing its PRI file.
                    return resourceId;
                }
            }
        }
    }

    private static void ApplyOverride(string languageTag)
    {
        // An unpackaged process starts without a persisted MRT override. Keep
        // it that way for the system-default choice: an empty string is not a
        // valid BCP-47 tag for the Windows App SDK setter. Explicit overrides
        // must be applied before XAML loads so x:Uid and ResourceLoader agree.
        if (languageTag.Length > 0)
        {
            ApplicationLanguages.PrimaryLanguageOverride = languageTag;
        }

        CultureInfo culture = languageTag.Length == 0
            ? SystemCulture
            : CultureInfo.GetCultureInfo(languageTag);
        CultureInfo uiCulture = languageTag.Length == 0
            ? SystemUiCulture
            : culture;

        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = uiCulture;
        CultureInfo.CurrentCulture = culture;
        CultureInfo.CurrentUICulture = uiCulture;
        AppliedLanguageTag = languageTag;
    }

    private static string LoadSavedOverride()
    {
        try
        {
            return File.Exists(PreferencePath)
                ? Normalize(File.ReadAllText(PreferencePath))
                : SystemLanguageTag;
        }
        catch
        {
            return SystemLanguageTag;
        }
    }

    private static void PersistOverride(string languageTag)
    {
        string? directory = Path.GetDirectoryName(PreferencePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        File.WriteAllText(
            PreferencePath,
            languageTag.Length == 0 ? SystemPreferenceValue : languageTag);
    }

    private static string Normalize(string? languageTag)
    {
        string candidate = languageTag?.Trim() ?? SystemLanguageTag;
        if (candidate.Length == 0 ||
            candidate.Equals(SystemPreferenceValue, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("default", StringComparison.OrdinalIgnoreCase))
        {
            return SystemLanguageTag;
        }

        if (candidate.Equals(EnglishLanguageTag, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("en", StringComparison.OrdinalIgnoreCase))
        {
            return EnglishLanguageTag;
        }

        if (candidate.Equals(TurkishLanguageTag, StringComparison.OrdinalIgnoreCase) ||
            candidate.Equals("tr", StringComparison.OrdinalIgnoreCase))
        {
            return TurkishLanguageTag;
        }

        throw new ArgumentOutOfRangeException(
            nameof(languageTag),
            languageTag,
            "Only the system default, en-US, and tr-TR languages are supported.");
    }
}
