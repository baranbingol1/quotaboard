// SPDX-License-Identifier: Apache-2.0
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AiLimits.Presentation.WinUI.Pages;

public sealed partial class OverviewPage : Page
{
    public OverviewPage(LiveDashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
    }
    public LiveDashboardViewModel ViewModel { get; }

    public Visibility CountToVisibility(int count) => count > 0 ? Visibility.Visible : Visibility.Collapsed;

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
