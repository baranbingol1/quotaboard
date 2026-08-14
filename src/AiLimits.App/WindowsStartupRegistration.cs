// SPDX-License-Identifier: Apache-2.0
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
        return $"{(char)34}{ResolveLauncherPath(executable)}{(char)34} --minimized";
    }

    internal static string ResolveLauncherPath(string executable)
    {
        string current = Path.GetFullPath(executable);
        string? currentDirectory = Path.GetDirectoryName(current);
        if (
            currentDirectory is null
            || !string.Equals(Path.GetFileName(currentDirectory), "current", StringComparison.OrdinalIgnoreCase)
        )
        {
            return current;
        }

        string? root = Directory.GetParent(currentDirectory)?.FullName;
        if (
            root is null
            || !File.Exists(Path.Combine(root, ".portable"))
            || !File.Exists(Path.Combine(root, "Update.exe"))
            || !File.Exists(Path.Combine(root, "QuotaBoard.exe"))
        )
        {
            return current;
        }

        return Path.Combine(root, "QuotaBoard.exe");
    }
}
