// SPDX-License-Identifier: Apache-2.0
using AiLimits.Platform.Windows.Authentication;

namespace AiLimits.Tests;

public sealed class WebViewProfileManagerTests
{
    [Fact]
    public void PathTraversalThrowsAndCreatesNothingOutsideRoot()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "webview-root");
        Directory.CreateDirectory(root);
        var manager = new WebViewProfileManager(root);

        var ex = Assert.Throws<InvalidOperationException>(() => manager.GetProfileDirectory("..", ".."));

        Assert.Contains("escaped", ex.Message, StringComparison.OrdinalIgnoreCase);

        // Nothing should have been created directly under the temp dir (only
        // the webview-root directory we created ourselves).
        var entries = Directory.GetFileSystemEntries(temp.Path);
        Assert.Single(entries);
        Assert.Equal(root, entries[0]);
    }

    [Fact]
    public void ValidProfileCreatesDirectoryUnderRoot()
    {
        using var temp = new TempDir();
        var root = Path.Combine(temp.Path, "webview-root");
        Directory.CreateDirectory(root);
        var manager = new WebViewProfileManager(root);

        var dir = manager.GetProfileDirectory("copilot", "alice");

        Assert.StartsWith(Path.GetFullPath(root), dir, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(dir));
    }

    private sealed class TempDir : IDisposable
    {
        public TempDir()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "AiLimits.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, true);
        }
    }
}
