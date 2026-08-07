// SPDX-License-Identifier: Apache-2.0

namespace AiLimits.Tests;

public sealed class ReleaseContractTests
{
    [Fact]
    public void ReleaseWorkflowVerifiesUnsignedArtifactsAndTheirProvenance()
    {
        string root = FindRepositoryRoot();
        string workflow = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));
        string releaseVerifier = File.ReadAllText(Path.Combine(root, "scripts", "verify-release.ps1"));

        Assert.Contains("Validate publish output", workflow, StringComparison.Ordinal);
        Assert.Contains("-OutputPath $env:PUBLISH_DIR", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("SignPath", workflow, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sign-release.ps1", workflow, StringComparison.Ordinal);
        Assert.DoesNotContain("verify-signatures.ps1", releaseVerifier, StringComparison.Ordinal);
        Assert.DoesNotContain("release-binaries.ps1", workflow, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, "scripts", "sign-release.ps1")));
        Assert.False(File.Exists(Path.Combine(root, "scripts", "verify-signatures.ps1")));
        Assert.False(File.Exists(Path.Combine(root, "scripts", "release-binaries.ps1")));

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
