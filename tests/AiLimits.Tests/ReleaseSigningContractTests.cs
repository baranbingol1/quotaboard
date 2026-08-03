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
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        string releaseVerifier = File.ReadAllText(Path.Combine(root, "scripts", "verify-release.ps1"));
        string signatureVerifier = File.ReadAllText(Path.Combine(root, "scripts", "verify-signatures.ps1"));

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

        // Trusted releases identify QuotaBoard's actual signer rather than any
        // certificate that happens to chain to a public root.
        Assert.Contains("ExpectedSignerCertificateSha256", signatureVerifier, StringComparison.Ordinal);
        Assert.Contains("SIGNPATH_EXPECTED_CERTIFICATE_SHA256", workflow, StringComparison.Ordinal);

        // Provenance and inventory must describe this exact release, not any
        // older internally-consistent artifact set from the same repository.
        Assert.Contains("--signer-workflow", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("--source-ref", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("--source-digest", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("ExpectedVersion", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("GITHUB_REF", workflow, StringComparison.Ordinal);
        Assert.Contains("GITHUB_SHA", workflow, StringComparison.Ordinal);
        Assert.Contains(".github/workflows/release.yml", workflow, StringComparison.Ordinal);
        Assert.Contains("gh release download $env:TAG --dir dist", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("--pattern '*.zip'", workflow, StringComparison.Ordinal);
        Assert.Contains("github.event_name == 'push' && startsWith(github.ref, 'refs/tags/v')", workflow, StringComparison.Ordinal);
        Assert.Contains("Compare-Object $expectedAssets $actualAssets -CaseSensitive", releaseVerifier, StringComparison.Ordinal);
        Assert.Contains("but no expected certificate is configured", signatureVerifier, StringComparison.Ordinal);
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
