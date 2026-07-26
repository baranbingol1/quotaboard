// SPDX-License-Identifier: Apache-2.0
using AiLimits.Presentation.WinUI.Pages;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace AiLimits.Presentation.WinUI.Shell;

public sealed partial class ShellPage : Page
{
    private readonly LiveDashboardViewModel _viewModel;
    private readonly IStartupRegistrationService? _startupRegistration;
    private readonly Dictionary<Type, Page> _pages = [];

    public ShellPage(LiveDashboardViewModel viewModel, IStartupRegistrationService? startupRegistration = null)
    {
        _viewModel = viewModel;
        _startupRegistration = startupRegistration;
        InitializeComponent();
        Navigation.SelectedItem = Navigation.MenuItems[0];
        Navigate(typeof(OverviewPage));
    }

    private void OnSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected) { Navigate(typeof(SettingsPage)); return; }
        if (args.SelectedItemContainer?.Tag is not string tag) return;
        Navigate(tag switch
        {
            "overview" => typeof(OverviewPage),
            "usage" => typeof(UsagePage),
            "providers" => typeof(ProvidersPage),
            "diagnostics" => typeof(DiagnosticsPage),
            _ => typeof(OverviewPage)
        });
    }

    private void Navigate(Type pageType)
    {
        if (!_pages.TryGetValue(pageType, out var page))
        {
            page = pageType == typeof(OverviewPage) ? new OverviewPage(_viewModel)
                : pageType == typeof(UsagePage) ? new UsagePage(_viewModel)
                : pageType == typeof(ProvidersPage) ? new ProvidersPage(_viewModel)
                : pageType == typeof(DiagnosticsPage) ? new DiagnosticsPage(_viewModel)
                : new SettingsPage(_viewModel, _startupRegistration);
            _pages[pageType] = page;
        }
        if (ContentFrame.Content != page) ContentFrame.Content = page;
    }
}
