// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Platform.Windows.Startup;

public static class PortableLauncherPath
{
    public static string Resolve(string executable)
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
