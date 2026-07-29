// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Infrastructure.Providers.Common;

/// <summary>
/// Enumeration guards against junctions and symlinks. The SearchOption-based
/// <see cref="Directory"/> overloads use compatibility defaults
/// (AttributesToSkip = 0) that DO descend into reparse points, so a junction
/// planted inside a provider's config tree could make a scanner read an
/// unrelated directory elsewhere on disk, or loop forever through a junction
/// cycle. These options never follow reparse points;
/// <see cref="IsReparsePoint"/> covers entries a caller walks by hand.
/// Inaccessible subtrees are skipped rather than aborting a whole scan.
/// </summary>
public static class SafeFileEnumeration
{
    public static EnumerationOptions Recursive { get; } = new()
    {
        RecurseSubdirectories = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static EnumerationOptions TopLevel { get; } = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="path"/> exists and
    /// is a real directory (not a junction or symlink). A reparse-point scan
    /// root would bypass the child-level <c>AttributesToSkip</c> guard because
    /// the root itself is never filtered — only its children are — so the scan
    /// would walk the junction target.
    /// </summary>
    public static bool IsSafeDirectory(string path)
    {
        try
        {
            return Directory.Exists(path) && !IsReparsePoint(path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
