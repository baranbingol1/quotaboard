// SPDX-License-Identifier: Apache-2.0

namespace AiLimits.Tests;

/// <summary>
/// The quality and test gates only work if CI actually invokes them. These
/// checks keep the workflow, hook, and scripts from drifting apart.
/// </summary>
public sealed class QualityGatesContractTests
{
    [Fact]
    public void CiWorkflowRunsFormatterComplexityCoverageAndIntegrationTests()
    {
        string root = FindRepositoryRoot();
        string ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        string release = File.ReadAllText(Path.Combine(root, ".github", "workflows", "release.yml"));

        Assert.Contains("dotnet csharpier check src tests", ci, StringComparison.Ordinal);
        Assert.Contains("invoke-quality-gates.ps1", ci, StringComparison.Ordinal);
        Assert.Contains("Assert-Coverage.ps1", ci, StringComparison.Ordinal);
        Assert.Contains("Assert-TestDurations.ps1", ci, StringComparison.Ordinal);
        Assert.Contains("AiLimits.IntegrationTests", ci, StringComparison.Ordinal);
        Assert.Contains("coverlet.runsettings", ci, StringComparison.Ordinal);
        Assert.Contains("AiLimits.IntegrationTests", release, StringComparison.Ordinal);
        Assert.Contains("dotnet csharpier check src tests", release, StringComparison.Ordinal);
        Assert.Contains("invoke-quality-gates.ps1 -SkipFormat", release, StringComparison.Ordinal);
        Assert.Contains("Assert-Coverage.ps1", release, StringComparison.Ordinal);
        Assert.Contains("Assert-TestDurations.ps1", release, StringComparison.Ordinal);
        Assert.Contains("needs: [quality, tests, audit]", ci, StringComparison.Ordinal);
        Assert.Contains("needs: [quality, tests]", release, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(root, "AGENTS.md")));
        Assert.True(File.Exists(Path.Combine(root, ".editorconfig")));
        Assert.True(File.Exists(Path.Combine(root, ".csharpierrc.json")));
        Assert.True(File.Exists(Path.Combine(root, ".pre-commit-config.yaml")));
        Assert.True(File.Exists(Path.Combine(root, ".githooks", "pre-commit")));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "quality", "Measure-Complexity.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "quality", "Find-LargeFiles.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "quality", "Find-DuplicateCode.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "quality", "Find-TechDebt.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "scripts", "quality", "Find-UnusedPackages.ps1")));
        Assert.True(File.Exists(Path.Combine(root, "tests", "coverlet.runsettings")));
    }

    [Fact]
    public void ComplexityConfigurationUsesTheFortyPointPolicy()
    {
        string root = FindRepositoryRoot();
        string metrics = File.ReadAllText(Path.Combine(root, "CodeMetricsConfig.txt"));
        string editorConfig = File.ReadAllText(Path.Combine(root, ".editorconfig"));
        string script = File.ReadAllText(Path.Combine(root, "scripts", "quality", "Measure-Complexity.ps1"));

        Assert.Contains("CA1502: 40", metrics, StringComparison.Ordinal);
        Assert.Contains("dotnet_diagnostic.CA1502.severity = error", editorConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet_code_quality.CA1502.threshold", editorConfig, StringComparison.Ordinal);
        Assert.Contains("[int]$Threshold = 40", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentsMdDocumentsRestoreTestPublishAndLaunch()
    {
        string root = FindRepositoryRoot();
        string agents = File.ReadAllText(Path.Combine(root, "AGENTS.md"));

        Assert.Contains("dotnet restore", agents, StringComparison.Ordinal);
        Assert.Contains("tests/AiLimits.Tests/AiLimits.Tests.csproj", agents, StringComparison.Ordinal);
        Assert.Contains(
            "tests/AiLimits.IntegrationTests/AiLimits.IntegrationTests.csproj",
            agents,
            StringComparison.Ordinal
        );
        Assert.Contains("dotnet csharpier check src tests", agents, StringComparison.Ordinal);
        Assert.Contains("scripts/publish-ai-limits.ps1", agents, StringComparison.Ordinal);
        Assert.Contains("app/win-x64/QuotaBoard.exe", agents, StringComparison.Ordinal);
        Assert.True(agents.Length > 100);
    }

    [Fact]
    public void IssueAndPullRequestTemplatesExist()
    {
        string root = FindRepositoryRoot();
        string issueTemplateDirectory = Path.Combine(root, ".github", "ISSUE_TEMPLATE");

        Assert.True(File.Exists(Path.Combine(issueTemplateDirectory, "config.yml")));
        Assert.True(File.Exists(Path.Combine(issueTemplateDirectory, "bug.yml")));
        Assert.True(File.Exists(Path.Combine(issueTemplateDirectory, "feature.yml")));
        Assert.True(File.Exists(Path.Combine(issueTemplateDirectory, "chore.yml")));
        Assert.True(File.Exists(Path.Combine(root, ".github", "pull_request_template.md")));
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AiLimits.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root was not found.");
    }
}
