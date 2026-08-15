// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI;

/// <summary>Owns the process-wide directory for QuotaBoard data and preferences.</summary>
public static class AppDataDirectory
{
    private static string? _isolatedRoot;

    public static string Root =>
        _isolatedRoot
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaBoard");

    public static string File(string name) => Path.Combine(Root, name);

    /// <summary>
    /// Redirects all QuotaBoard-owned files for an isolated E2E process. The
    /// target must be below the Windows temporary directory.
    /// </summary>
    public static void UseIsolatedRoot(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string root = Path.GetFullPath(path);
        string temporary = Path.GetFullPath(Path.GetTempPath());
        string relative = Path.GetRelativePath(temporary, root);
        if (
            relative == "."
            || relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
        )
        {
            throw new ArgumentException("The isolated data root must be below the temporary directory.", nameof(path));
        }

        _isolatedRoot = root;
    }
}
