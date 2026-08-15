// SPDX-License-Identifier: Apache-2.0
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AiLimits.Presentation.WinUI.Pages;

public sealed partial class OverviewPage : Page
{
    private readonly Action _openConnections;

    public OverviewPage(LiveDashboardViewModel viewModel, Action openConnections)
    {
        ViewModel = viewModel;
        _openConnections = openConnections;
        InitializeComponent();
    }
    public LiveDashboardViewModel ViewModel { get; }

    public Visibility CountToVisibility(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

    public Visibility When(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void OnOpenConnectionsClicked(object sender, RoutedEventArgs args) => _openConnections();

    private void OnGroupByProviderClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.UsageByProvider = true;
        GroupByProviderButton.IsChecked = true;
        GroupByHarnessButton.IsChecked = false;
    }

    private void OnGroupByHarnessClicked(object sender, RoutedEventArgs e)
    {
        ViewModel.UsageByProvider = false;
        GroupByProviderButton.IsChecked = false;
        GroupByHarnessButton.IsChecked = true;
    }
}
