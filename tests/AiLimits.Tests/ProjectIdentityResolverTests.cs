// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Usage;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class ProjectIdentityResolverTests
{
    [Fact]
    public void PathsAreGroupedCaseInsensitivelyWithoutLosingUnicodeDisplayText()
    {
        using var temporary = new TemporaryDirectory();
        var path = Directory.CreateDirectory(Path.Combine(temporary.Path, "Türkçe", "çalışma")).FullName;

        var first = new ProjectIdentityResolver().Resolve(path + Path.DirectorySeparatorChar);
        var recased = new ProjectIdentityResolver().Resolve(path.ToUpperInvariant());

        Assert.Equal(first.ProjectKey, recased.ProjectKey);
        Assert.Equal(path, first.ProjectPath);
        Assert.Contains("Türkçe", first.ProjectPath, StringComparison.Ordinal);
        Assert.Null(first.RepositoryRootPath);
    }

    [Fact]
    public void LinkedWorktreesKeepDistinctProjectPathAndSharedRepositoryRoot()
    {
        using var temporary = new TemporaryDirectory();
        var repository = Directory.CreateDirectory(Path.Combine(temporary.Path, "ana-depo"));
        var commonGit = Directory.CreateDirectory(Path.Combine(repository.FullName, ".git"));
        var worktree = Directory.CreateDirectory(Path.Combine(temporary.Path, "worktrees", "özellik"));
        var nested = Directory.CreateDirectory(Path.Combine(worktree.FullName, "src", "uygulama"));
        var worktreeGit = Directory.CreateDirectory(Path.Combine(commonGit.FullName, "worktrees", "feature"));
        File.WriteAllText(Path.Combine(worktree.FullName, ".git"), $"gitdir: {worktreeGit.FullName}");
        File.WriteAllText(
            Path.Combine(worktreeGit.FullName, "commondir"),
            Path.GetRelativePath(worktreeGit.FullName, commonGit.FullName));

        var identity = new ProjectIdentityResolver().Resolve(nested.FullName);

        Assert.Equal(worktree.FullName, identity.ProjectPath);
        Assert.Equal(repository.FullName, identity.RepositoryRootPath);
        Assert.False(identity.IsUnknown);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/path")]
    public void MissingOrRelativeWorkingDirectoriesUseExplicitUnknownBucket(string? path)
    {
        Assert.True(new ProjectIdentityResolver().Resolve(path).IsUnknown);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
