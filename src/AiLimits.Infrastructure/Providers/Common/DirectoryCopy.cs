// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Infrastructure.Providers.Common;

/// <summary>
/// Recursive best-effort copy used by the legacy data-folder migration.
/// Junctions and symlinks are neither copied nor descended: following one
/// could pull an unrelated directory into the app's data folder or recurse
/// forever through a junction cycle.
/// </summary>
public static class DirectoryCopy
{
    public static void CopyMissing(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.EnumerateFiles(source, "*", SafeFileEnumeration.TopLevel))
        {
            string target = Path.Combine(destination, Path.GetFileName(file));
            if (!File.Exists(target) && !SafeFileEnumeration.IsReparsePoint(file))
            {
                try { File.Copy(file, target, overwrite: false); } catch { }
            }
        }
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SafeFileEnumeration.TopLevel))
        {
            if (SafeFileEnumeration.IsReparsePoint(directory))
            {
                continue;
            }
            try { CopyMissing(directory, Path.Combine(destination, Path.GetFileName(directory))); } catch { }
        }
    }
}
