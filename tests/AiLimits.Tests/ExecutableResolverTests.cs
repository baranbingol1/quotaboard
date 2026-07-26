// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

/// <summary>
/// Provider CLIs are launched from names the adapters discovered off PATH, so
/// the resolver is what stands between QuotaBoard and running whatever binary
/// happened to be sitting in the working directory.
/// </summary>
public sealed class ExecutableResolverTests
{
    [Fact]
    public void A_bare_name_resolves_to_a_fully_qualified_exe_on_path()
    {
        string resolved = ExecutableResolver.Resolve("ping");

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.Equal(".exe", Path.GetExtension(resolved), ignoreCase: true);
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void An_explicit_exe_suffix_is_not_doubled()
    {
        Assert.Equal(
            ExecutableResolver.Resolve("ping"),
            ExecutableResolver.Resolve("ping.exe"),
            ignoreCase: true);
    }

    [Theory]
    [InlineData("./tool.exe")]
    [InlineData(".\\tool.exe")]
    [InlineData("..\\tool.exe")]
    [InlineData("sub/tool.exe")]
    [InlineData("sub\\tool.exe")]
    // Drive-relative: resolves against the drive's own working directory.
    [InlineData("C:tool.exe")]
    public void A_relative_path_is_refused_rather_than_resolved(string executable)
    {
        Assert.Throws<ArgumentException>(() => ExecutableResolver.Resolve(executable));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void An_empty_name_is_refused(string executable)
    {
        Assert.Throws<ArgumentException>(() => ExecutableResolver.Resolve(executable));
    }

    [Theory]
    [InlineData(".cmd")]
    [InlineData(".bat")]
    [InlineData(".ps1")]
    public void A_shim_that_cannot_be_launched_directly_is_refused(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), "AiLimits.Tests",
            Guid.NewGuid().ToString("N") + extension);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "@echo off");
        try
        {
            Assert.Throws<ArgumentException>(() => ExecutableResolver.Resolve(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_fully_qualified_exe_that_does_not_exist_is_reported_as_missing()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".exe");

        Assert.Throws<FileNotFoundException>(() => ExecutableResolver.Resolve(path));
    }

    [Fact]
    public void A_name_that_is_on_no_path_entry_is_reported_as_missing()
    {
        Assert.Throws<FileNotFoundException>(
            () => ExecutableResolver.Resolve("quotaboard-no-such-cli-" + Guid.NewGuid().ToString("N")));
    }
}
