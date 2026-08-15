// SPDX-License-Identifier: Apache-2.0
using AiLimits.Platform.Windows.Startup;
using AiLimits.Presentation.WinUI;
using Microsoft.Win32;

namespace AiLimits.App;

internal sealed class WindowsStartupRegistration : IStartupRegistrationService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "QuotaBoard";

    public bool IsStartupEnabled
    {
        get
        {
            try
            {
                using RegistryKey currentUser = RegistryKey.OpenBaseKey(
                    RegistryHive.CurrentUser,
                    RegistryView.Registry64
                );
                using RegistryKey? key = currentUser.OpenSubKey(RunKeyPath, writable: false);
                string? value = key?.GetValue(ValueName) as string;
                return string.Equals(value?.Trim(), BuildCommand(), StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
                when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
            {
                return false;
            }
        }
    }

    public bool SetStartupEnabled(bool enabled)
    {
        try
        {
            using RegistryKey currentUser = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
            using RegistryKey key = currentUser.CreateSubKey(RunKeyPath, writable: true);
            if (enabled)
            {
                key.SetValue(ValueName, BuildCommand(), RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            return IsStartupEnabled == enabled;
        }
        catch (Exception ex)
            when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return false;
        }
    }

    internal static string BuildCommand()
    {
        string executable = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "QuotaBoard.exe");
        return $"{(char)34}{PortableLauncherPath.Resolve(executable)}{(char)34} --minimized";
    }
}
