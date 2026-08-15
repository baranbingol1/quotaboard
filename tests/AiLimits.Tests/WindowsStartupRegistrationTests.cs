// SPDX-License-Identifier: Apache-2.0
using AiLimits.Platform.Windows.Startup;

namespace AiLimits.Tests;

public sealed class WindowsStartupRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "QuotaBoard startup path tests",
        Guid.NewGuid().ToString("N")
    );

    [Fact]
    public void Raw_publish_keeps_current_executable()
    {
        string executable = Path.Combine(_root, "QuotaBoard.exe");

        Assert.Equal(Path.GetFullPath(executable), PortableLauncherPath.Resolve(executable));
    }

    [Fact]
    public void Velopack_current_resolves_stable_root_launcher()
    {
        string executable = CreatePortableLayout();

        string resolved = PortableLauncherPath.Resolve(executable);

        Assert.Equal(Path.Combine(_root, "QuotaBoard.exe"), resolved);
        Assert.Contains("startup path tests", resolved, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(".portable")]
    [InlineData("Update.exe")]
    [InlineData("QuotaBoard.exe")]
    public void Incomplete_portable_layout_keeps_current_executable(string missingFile)
    {
        string executable = CreatePortableLayout();
        File.Delete(Path.Combine(_root, missingFile));

        Assert.Equal(Path.GetFullPath(executable), PortableLauncherPath.Resolve(executable));
    }

    private string CreatePortableLayout()
    {
        string current = Path.Combine(_root, "current");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(_root, ".portable"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "Update.exe"), string.Empty);
        File.WriteAllText(Path.Combine(_root, "QuotaBoard.exe"), string.Empty);
        string executable = Path.Combine(current, "QuotaBoard.exe");
        File.WriteAllText(executable, string.Empty);
        return executable;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
