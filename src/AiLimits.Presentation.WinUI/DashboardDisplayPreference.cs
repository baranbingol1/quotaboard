// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;

namespace AiLimits.Presentation.WinUI;

public sealed record DashboardDisplayOptions(
    bool ShowUsageAsRemaining = false,
    bool ShowResetTimeAsAbsolute = false,
    bool HideZeroOrNotApplicableMeters = false);

/// <summary>
/// Persistent, process-wide display choices. The dashboard listens to
/// <see cref="Changed"/> and re-projects its meter cards immediately.
/// </summary>
public static class DashboardDisplayPreference
{
    private static readonly object Gate = new();
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard", "display-preferences.json");
    private static DashboardDisplayOptions _current = LoadCore();

    public static DashboardDisplayOptions Current
    {
        get { lock (Gate) return _current; }
    }

    public static event Action<DashboardDisplayOptions>? Changed;

    public static void Update(
        bool? showUsageAsRemaining = null,
        bool? showResetTimeAsAbsolute = null,
        bool? hideZeroOrNotApplicableMeters = null)
    {
        DashboardDisplayOptions updated;
        lock (Gate)
        {
            updated = _current with
            {
                ShowUsageAsRemaining = showUsageAsRemaining ?? _current.ShowUsageAsRemaining,
                ShowResetTimeAsAbsolute = showResetTimeAsAbsolute ?? _current.ShowResetTimeAsAbsolute,
                HideZeroOrNotApplicableMeters = hideZeroOrNotApplicableMeters ?? _current.HideZeroOrNotApplicableMeters
            };
            if (updated == _current)
            {
                return;
            }
            _current = updated;
            SaveCore(updated);
        }
        Changed?.Invoke(updated);
    }

    private static DashboardDisplayOptions LoadCore()
    {
        try
        {
            if (!File.Exists(PreferencePath))
            {
                return new DashboardDisplayOptions();
            }
            return JsonSerializer.Deserialize<DashboardDisplayOptions>(File.ReadAllText(PreferencePath))
                ?? new DashboardDisplayOptions();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new DashboardDisplayOptions();
        }
    }

    private static void SaveCore(DashboardDisplayOptions options)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PreferencePath)!);
            File.WriteAllText(PreferencePath, JsonSerializer.Serialize(options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}

public static class TrayMonitorPreference
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard", "tray-monitor.preference");
    private static bool _enabled = LoadCore();

    public static bool Enabled => _enabled;
    public static event Action<bool>? Changed;

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
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        Changed?.Invoke(enabled);
    }

    private static bool LoadCore()
    {
        try
        {
            return !File.Exists(PreferencePath)
                || !string.Equals(File.ReadAllText(PreferencePath).Trim(), "Off", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }
}
