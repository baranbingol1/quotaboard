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
