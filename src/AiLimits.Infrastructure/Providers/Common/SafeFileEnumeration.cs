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
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    public static EnumerationOptions TopLevel { get; } = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = FileAttributes.Hidden | FileAttributes.System | FileAttributes.ReparsePoint
    };

    public static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}
