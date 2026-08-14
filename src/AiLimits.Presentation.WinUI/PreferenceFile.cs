// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Crash-safe persistence for the small JSON preference files. A direct
/// File.WriteAllText truncates in place, so a crash mid-write leaves a torn
/// file and the next load silently falls back to defaults; writing a sibling
/// temp file and renaming over the target is atomic on NTFS.
/// </summary>
internal static class PreferenceFile
{
    internal static void WriteAtomic(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, content);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }
}
