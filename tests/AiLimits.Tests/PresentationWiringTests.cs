// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Tests;

public sealed class PresentationWiringTests
{
    [Fact]
    public void Missing_account_projection_never_marks_discovery_failure_connected()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "AiLimits.App", "LiveDashboardDataSource.cs")
        );

        Assert.Contains("isConnected: false", source, StringComparison.Ordinal);
        Assert.DoesNotContain("isConnected: retrying", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Overview_connections_action_updates_navigation_selection()
    {
        string source = File.ReadAllText(
            Path.Combine(FindRepositoryRoot(), "src", "AiLimits.Presentation.WinUI", "Shell", "ShellPage.xaml.cs")
        );

        Assert.Contains("new OverviewPage(_viewModel, OpenConnections)", source, StringComparison.Ordinal);
        Assert.Contains("Navigation.SelectedItem = ConnectionsNavigationItem", source, StringComparison.Ordinal);
        Assert.DoesNotContain("() => Navigate(typeof(ProvidersPage))", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Clean_state_e2e_mode_isolated_from_user_data_and_provider_adapters()
    {
        string root = FindRepositoryRoot();
        string program = File.ReadAllText(Path.Combine(root, "src", "AiLimits.App", "Program.cs"));
        string dataSource = File.ReadAllText(Path.Combine(root, "src", "AiLimits.App", "LiveDashboardDataSource.cs"));
        string driver = File.ReadAllText(Path.Combine(root, "scripts", "drive-app.ps1"));

        Assert.Contains("AppDataDirectory.UseIsolatedRoot(isolatedRoot)", program, StringComparison.Ordinal);
        Assert.Contains("IsolatedEmptyState ? InstanceKey + \".IsolatedE2E\"", program, StringComparison.Ordinal);
        Assert.Contains("if (isolatedEmptyState)", dataSource, StringComparison.Ordinal);
        Assert.Contains("_adapters = [];", dataSource, StringComparison.Ordinal);
        Assert.Contains("forceRefresh && _isolatedEmptyState", dataSource, StringComparison.Ordinal);
        Assert.Contains("[switch]$CleanState", driver, StringComparison.Ordinal);
        Assert.Contains("--e2e-clean-state", driver, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_reminder_automation_uses_the_masked_account_label()
    {
        string source = File.ReadAllText(
            Path.Combine(
                FindRepositoryRoot(),
                "src",
                "AiLimits.Presentation.WinUI",
                "ViewModels",
                "DashboardViewModels.cs"
            )
        );

        Assert.Contains("public string DisplayAccount => EmailPrivacyPreference.Apply(Account);", source);
        Assert.Contains("LocalizationService.GetString(\"Reset_AutomationName\")", source);
        Assert.Matches("Provider,\\s+DisplayAccount,", source);
    }

    [Fact]
    public void Usage_connections_and_diagnostics_have_narrow_window_layouts()
    {
        string pages = Path.Combine(FindRepositoryRoot(), "src", "AiLimits.Presentation.WinUI", "Pages");
        string usage = File.ReadAllText(Path.Combine(pages, "UsagePage.xaml.cs"));
        string providersXaml = File.ReadAllText(Path.Combine(pages, "ProvidersPage.xaml"));
        string providersCode = File.ReadAllText(Path.Combine(pages, "ProvidersPage.xaml.cs"));
        string diagnostics = File.ReadAllText(Path.Combine(pages, "DiagnosticsPage.xaml.cs"));

        Assert.Contains("StackedLayoutWidth", usage, StringComparison.Ordinal);
        Assert.Contains("NarrowConnections", providersXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("MinWidth=\"1000\"", providersXaml, StringComparison.Ordinal);
        Assert.Contains("private const double NarrowLayoutWidth = 1030;", providersCode, StringComparison.Ordinal);
        Assert.Contains(
            "ChartPlotHost.Visibility = result.MatchingRecordCount == 0 ? Visibility.Collapsed : Visibility.Visible;",
            usage,
            StringComparison.Ordinal
        );
        Assert.DoesNotContain(
            "ChartScrollViewer.Visibility = result.MatchingRecordCount == 0",
            usage,
            StringComparison.Ordinal
        );
        Assert.Contains("StackedStatusWidth", diagnostics, StringComparison.Ordinal);
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
