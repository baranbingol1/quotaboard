// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Domain;

/// <summary>
/// Identifies the working project that produced a usage event.
/// </summary>
/// <param name="ProjectKey">
/// A stable, case-insensitive key for grouping the normalized project path.
/// </param>
/// <param name="ProjectPath">
/// The normalized worktree root, or the normalized working directory when the
/// session is not inside a Git worktree.
/// </param>
/// <param name="RepositoryRootPath">
/// The normalized root of the shared Git repository. This differs from
/// <paramref name="ProjectPath"/> for linked worktrees.
/// </param>
public sealed record ProjectIdentity(
    string ProjectKey,
    string ProjectPath,
    string? RepositoryRootPath)
{
    public static ProjectIdentity Unknown { get; } = new("unknown", "Unknown", null);

    public bool IsUnknown => string.Equals(ProjectKey, Unknown.ProjectKey, StringComparison.Ordinal);
}
