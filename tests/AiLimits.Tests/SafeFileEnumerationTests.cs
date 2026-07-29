// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Claude;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

/// <summary>
/// Guards against junctions/symlinks inside provider trees. The tests plant a
/// real junction (mklink /J needs no elevation) pointing outside the scanned
/// root; without the guard, recursive enumeration reads the target.
/// </summary>
public sealed class SafeFileEnumerationTests
{
    [Fact]
    public void Recursive_enumeration_does_not_descend_into_a_junction()
    {
        using var temp = new TemporaryDirectory();
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside"));
        File.WriteAllText(Path.Combine(outside.FullName, "leak.jsonl"), "{}");
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root"));
        var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real"));
        File.WriteAllText(Path.Combine(real.FullName, "ok.jsonl"), "{}");
        string link = Path.Combine(root.FullName, "link");
        CreateJunction(link, outside.FullName);

        // Sanity: the junction really resolves, and the legacy SearchOption
        // overload follows it — this is the leak the guard closes.
        Assert.Contains(
            Directory.EnumerateFiles(root.FullName, "*.jsonl", SearchOption.AllDirectories),
            path => path.StartsWith(link, StringComparison.OrdinalIgnoreCase));

        var guarded = Directory.EnumerateFiles(root.FullName, "*.jsonl", SafeFileEnumeration.Recursive).ToArray();

        var file = Assert.Single(guarded);
        Assert.Equal(Path.Combine(real.FullName, "ok.jsonl"), file);
    }

    [Fact]
    public void TopLevel_enumeration_skips_junction_directories()
    {
        using var temp = new TemporaryDirectory();
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside"));
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root"));
        var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real"));
        string link = Path.Combine(root.FullName, "link");
        CreateJunction(link, outside.FullName);

        var directories = Directory.EnumerateDirectories(root.FullName, "*", SafeFileEnumeration.TopLevel).ToArray();

        var directory = Assert.Single(directories);
        Assert.Equal(real.FullName, directory);
        Assert.True(SafeFileEnumeration.IsReparsePoint(link));
        Assert.False(SafeFileEnumeration.IsReparsePoint(real.FullName));
    }

    [Fact]
    public void Recursive_enumeration_includes_hidden_files_and_directories()
    {
        using var temp = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root"));
        string hiddenFile = Path.Combine(root.FullName, "hidden.jsonl");
        File.WriteAllText(hiddenFile, "{}");
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        var hiddenDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "hidden-directory"));
        hiddenDirectory.Attributes |= FileAttributes.Hidden;
        string nestedFile = Path.Combine(hiddenDirectory.FullName, "nested.jsonl");
        File.WriteAllText(nestedFile, "{}");

        string[] files = Directory.EnumerateFiles(
            root.FullName, "*.jsonl", SafeFileEnumeration.Recursive).ToArray();

        Assert.Contains(hiddenFile, files);
        Assert.Contains(nestedFile, files);
    }

    [Fact]
    public void IsSafeDirectory_rejects_a_junction_as_the_scan_root()
    {
        using var temp = new TemporaryDirectory();
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside"));
        File.WriteAllText(Path.Combine(outside.FullName, "leak.jsonl"), "{}");
        var root = Directory.CreateDirectory(Path.Combine(temp.Path, "root"));
        var real = Directory.CreateDirectory(Path.Combine(root.FullName, "real"));
        File.WriteAllText(Path.Combine(real.FullName, "ok.jsonl"), "{}");
        // The scan root itself is a junction: without IsSafeDirectory, the
        // AttributesToSkip guard only filters children, so the root's target
        // would still be walked.
        string link = Path.Combine(temp.Path, "link-root");
        CreateJunction(link, root.FullName);

        Assert.False(SafeFileEnumeration.IsSafeDirectory(link));
        Assert.True(SafeFileEnumeration.IsSafeDirectory(root.FullName));
        // Enumerating through the junction root reads the target's files.
        var leaked = Directory.EnumerateFiles(link, "*.jsonl", SafeFileEnumeration.Recursive).ToArray();
        Assert.NotEmpty(leaked);
        // A caller using IsSafeDirectory as its gate would never reach that
        // enumeration, so no files from the target are seen.
    }

    [Fact]
    public async Task A_scanner_reads_nothing_through_a_junction()
    {
        using var temp = new TemporaryDirectory();
        // A valid Claude history living OUTSIDE the provider tree.
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside", "evil-project"));
        await File.WriteAllLinesAsync(Path.Combine(outside.FullName, "leak.jsonl"),
        [
            "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"message\":{\"id\":\"m1\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":100,\"output_tokens\":20}}}"
        ]);
        var claudeHome = Directory.CreateDirectory(Path.Combine(temp.Path, "claude-home"));
        var projects = Directory.CreateDirectory(Path.Combine(claudeHome.FullName, "projects"));
        var legit = Directory.CreateDirectory(Path.Combine(projects.FullName, "legit"));
        await File.WriteAllLinesAsync(Path.Combine(legit.FullName, "chat.jsonl"),
        [
            "{\"timestamp\":\"2026-07-13T10:00:00Z\",\"message\":{\"id\":\"m2\",\"model\":\"claude-sonnet-4\",\"usage\":{\"input_tokens\":5,\"output_tokens\":3}}}"
        ]);
        CreateJunction(Path.Combine(projects.FullName, "evil-project"), outside.FullName);

        var events = await CollectAsync(
            new ClaudeJsonlTokenSource(claudeHome.FullName).ReadAsync(Account("claude"), null, default));

        var usage = Assert.Single(events);
        Assert.Equal(5, usage.InputTokens);
    }

    [Fact]
    public void CopyMissing_skips_junction_directories()
    {
        using var temp = new TemporaryDirectory();
        var outside = Directory.CreateDirectory(Path.Combine(temp.Path, "outside"));
        File.WriteAllText(Path.Combine(outside.FullName, "secret.txt"), "secret");
        var source = Directory.CreateDirectory(Path.Combine(temp.Path, "source"));
        File.WriteAllText(Path.Combine(source.FullName, "keep.txt"), "keep");
        CreateJunction(Path.Combine(source.FullName, "link"), outside.FullName);
        string destination = Path.Combine(temp.Path, "destination");

        DirectoryCopy.CopyMissing(source.FullName, destination);

        Assert.True(File.Exists(Path.Combine(destination, "keep.txt")));
        Assert.False(Directory.Exists(Path.Combine(destination, "link")));
        // Nothing leaked out of the junction target into the destination tree.
        Assert.False(File.Exists(Path.Combine(destination, "link", "secret.txt")));
        Assert.True(File.Exists(Path.Combine(outside.FullName, "secret.txt")));
    }

    private static void CreateJunction(string link, string target)
    {
        (int exitCode, string error) = RunCmd($"/c mklink /J \"{link}\" \"{target}\"");
        Assert.True(exitCode == 0 && Directory.Exists(link),
            $"mklink failed ({exitCode}): {error}");
    }

    private static (int ExitCode, string Error) RunCmd(string arguments)
    {
        using Process process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = arguments,
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        })!;
        process.WaitForExit();
        return (process.ExitCode, process.StandardError.ReadToEnd());
    }

    // Directory.Delete(recursive) throws UnauthorizedAccessException on a tree
    // containing a junction; cmd's rmdir removes the link itself, never the
    // target. Walk top-down so no reparse point is ever descended into.
    private static void RemoveJunctions(string root)
    {
        foreach (string directory in Directory.EnumerateDirectories(root))
        {
            if (SafeFileEnumeration.IsReparsePoint(directory))
            {
                RunCmd($"/c rmdir \"{directory}\"");
                continue;
            }
            RemoveJunctions(directory);
        }
    }

    private static ProviderAccount Account(string provider) => new(
        new AccountKey(new ProviderId(provider), "one"), "one", null, "fixture", 1, true);

    private static async Task<List<T>> CollectAsync<T>(IAsyncEnumerable<T> source)
    {
        var items = new List<T>();
        await foreach (var item in source) items.Add(item);
        return items;
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
            if (!Directory.Exists(Path)) return;
            RemoveJunctions(Path);
            Directory.Delete(Path, true);
        }
    }
}
