// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Usage;

/// <summary>
/// Turns provider-owned session working directories into stable project
/// identities without invoking Git or reading session contents beyond paths.
/// </summary>
public sealed class ProjectIdentityResolver
{
    private readonly ConcurrentDictionary<string, ProjectIdentity> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProjectIdentity Resolve(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
            return ProjectIdentity.Unknown;

        try
        {
            if (!Path.IsPathFullyQualified(workingDirectory))
                return ProjectIdentity.Unknown;
            var normalized = NormalizePath(workingDirectory);
            return _cache.GetOrAdd(normalized, ResolveNormalized);
        }
        catch (Exception exception)
            when (exception is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            return ProjectIdentity.Unknown;
        }
    }

    internal static string NormalizePath(string path)
    {
        var normalized = Path.GetFullPath(path)
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .Normalize(NormalizationForm.FormC);
        var root = Path.GetPathRoot(normalized) ?? string.Empty;
        while (normalized.Length > root.Length && normalized.EndsWith(Path.DirectorySeparatorChar))
        {
            normalized = normalized[..^1];
        }
        return normalized;
    }

    private static ProjectIdentity ResolveNormalized(string workingDirectory)
    {
        var (worktreeRoot, repositoryRoot) = FindRepository(workingDirectory);
        var projectPath = worktreeRoot ?? workingDirectory;
        return new ProjectIdentity(CreateProjectKey(projectPath), projectPath, repositoryRoot);
    }

    private static (string? WorktreeRoot, string? RepositoryRoot) FindRepository(string workingDirectory)
    {
        for (var directory = new DirectoryInfo(workingDirectory); directory is not null; directory = directory.Parent)
        {
            var marker = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(marker))
            {
                var root = NormalizePath(directory.FullName);
                return (root, root);
            }
            if (!File.Exists(marker))
                continue;

            var worktreeRoot = NormalizePath(directory.FullName);
            var gitDirectory = ReadGitDirectory(marker, directory.FullName);
            var repositoryRoot = gitDirectory is null
                ? worktreeRoot
                : ReadSharedRepositoryRoot(gitDirectory) ?? worktreeRoot;
            return (worktreeRoot, repositoryRoot);
        }
        return (null, null);
    }

    private static string? ReadGitDirectory(string markerPath, string worktreeRoot)
    {
        try
        {
            var marker = File.ReadLines(markerPath).FirstOrDefault();
            const string prefix = "gitdir:";
            if (marker is null || !marker.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return null;
            var path = marker[prefix.Length..].Trim();
            return NormalizePath(Path.IsPathFullyQualified(path) ? path : Path.Combine(worktreeRoot, path));
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string? ReadSharedRepositoryRoot(string gitDirectory)
    {
        try
        {
            var commonDirectoryFile = Path.Combine(gitDirectory, "commondir");
            if (!File.Exists(commonDirectoryFile))
                return null;
            var commonDirectory = File.ReadLines(commonDirectoryFile).FirstOrDefault()?.Trim();
            if (string.IsNullOrWhiteSpace(commonDirectory))
                return null;
            var resolved = NormalizePath(
                Path.IsPathFullyQualified(commonDirectory)
                    ? commonDirectory
                    : Path.Combine(gitDirectory, commonDirectory)
            );
            var info = new DirectoryInfo(resolved);
            return string.Equals(info.Name, ".git", StringComparison.OrdinalIgnoreCase) && info.Parent is not null
                ? NormalizePath(info.Parent.FullName)
                : null;
        }
        catch (Exception exception)
            when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static string CreateProjectKey(string projectPath)
    {
        // Windows paths are case-insensitive. Unicode normalization plus an
        // invariant fold ensures Turkish casing and composed/decomposed forms
        // produce one stable key while preserving the original path for UI.
        var keyPath = projectPath.Replace('\\', '/').Normalize(NormalizationForm.FormC).ToUpperInvariant();
        return "project:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyPath))).ToLowerInvariant();
    }
}
