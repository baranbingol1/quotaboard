// SPDX-License-Identifier: Apache-2.0

namespace AiLimits.Tests;

public sealed class ReleaseSigningContractTests
{
    private static readonly string[] OwnedBinaries =
    [
        "QuotaBoard.exe",
        "QuotaBoard.dll",
        "AiLimits.Application.dll",
        "AiLimits.Domain.dll",
        "AiLimits.Infrastructure.dll",
        "AiLimits.Platform.Windows.dll",
        "AiLimits.Presentation.WinUI.dll"
    ];

    [Fact]
    public void ReleaseScriptsShareOneExplicitOwnedBinaryAllowlist()
    {
        string root = FindRepositoryRoot();
        string allowlist = File.ReadAllText(Path.Combine(root, "scripts", "release-binaries.ps1"));
        string signer = File.ReadAllText(Path.Combine(root, "scripts", "sign-release.ps1"));
        string verifier = File.ReadAllText(Path.Combine(root, "scripts", "verify-signatures.ps1"));
        string buildProperties = File.ReadAllText(Path.Combine(root, "Directory.Build.props"));

        Assert.All(OwnedBinaries, name => Assert.Contains($"'{name}'", allowlist, StringComparison.Ordinal));
        Assert.Equal(OwnedBinaries.Length, allowlist.Split('\n').Count(line => line.TrimStart().StartsWith("'", StringComparison.Ordinal)));
        Assert.Contains("<Product>QuotaBoard</Product>", buildProperties, StringComparison.Ordinal);
        Assert.Contains("$metadata.ProductName -ne 'QuotaBoard'", allowlist, StringComparison.Ordinal);
        Assert.Contains("$versions.Count -ne 1", allowlist, StringComparison.Ordinal);
        Assert.Contains("Get-QuotaBoardOwnedBinaryPaths", signer, StringComparison.Ordinal);
        Assert.Contains("Get-QuotaBoardOwnedBinaryPaths", verifier, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ChildItem -LiteralPath $resolvedPublishDir -Recurse", signer, StringComparison.Ordinal);
    }

    [Fact]
    public void ReleaseWorkflowSubmitsOnlyAllowlistedFilesAndOverlaysSignedOutput()
    {
        string workflow = File.ReadAllText(Path.Combine(FindRepositoryRoot(), ".github", "workflows", "release.yml"));

        Assert.Contains("Stage project-owned binaries for SignPath", workflow, StringComparison.Ordinal);
        Assert.Contains("Get-QuotaBoardOwnedBinaryPaths -RootPath $env:PUBLISH_DIR", workflow, StringComparison.Ordinal);
        Assert.Contains("path: ${{ runner.temp }}\\AiLimits\\release\\win-${{ matrix.arch }}-signing-input", workflow, StringComparison.Ordinal);
        Assert.Contains("Compare-Object $expected $signed", workflow, StringComparison.Ordinal);
        Assert.Contains("Copy-Item -LiteralPath (Join-Path $env:SIGNED_DIR $name) -Destination (Join-Path $env:UNSIGNED_DIR $name) -Force", workflow, StringComparison.Ordinal);
        Assert.Contains("$selected = $env:UNSIGNED_DIR", workflow, StringComparison.Ordinal);

        // Keep the existing hardened SignPath and release-verification gates.
        Assert.Contains("github-artifact-id: ${{ steps.unsigned.outputs.artifact-id }}", workflow, StringComparison.Ordinal);
        Assert.Contains("permissions:\n      actions: read", workflow.Replace("\r\n", "\n"), StringComparison.Ordinal);
        Assert.Contains("-RequireTrustedSignature ($env:SIGNPATH_ENABLED -eq 'true')", workflow, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiLimits.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName
            ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
