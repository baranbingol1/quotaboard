// SPDX-License-Identifier: Apache-2.0
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.System;

namespace AiLimits.Presentation.WinUI.Pages;

public sealed partial class ProvidersPage : Page
{
    private const double NarrowLayoutWidth = 1030;
    private NotifyCollectionChangedEventHandler? _connectionsChangedHandler;

    public ProvidersPage(LiveDashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        // First child of SummaryPanel: the status line above the page title.
        if (SummaryPanel.Children[0] is TextBlock summary)
        {
            summary.SetBinding(TextBlock.TextProperty, new Binding
            {
                Source = ViewModel,
                Path = new PropertyPath(nameof(LiveDashboardViewModel.StatusMessage)),
                Mode = BindingMode.OneWay
            });
        }
        _connectionsChangedHandler = (_, _) => ApplyFilter(SearchBox.Text);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LiveDashboardViewModel ViewModel { get; }
    public ObservableCollection<ProviderConnectionViewModel> FilteredConnections { get; } = [];

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.Connections.CollectionChanged += _connectionsChangedHandler;
        ApplyFilter(SearchBox.Text);
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.Connections.CollectionChanged -= _connectionsChangedHandler;
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) =>
        ApplyFilter(sender.Text);

    private void OnProvidersContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool narrow = args.NewSize.Width < NarrowLayoutWidth;
        WideColumnHeaders.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        WideConnections.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        NarrowConnections.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
    }

    // Web links only — detection never launches or drives a CLI.
    private async void OnConnectionActionClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { DataContext: ProviderConnectionViewModel connection } ||
            connection.ActionKind != "uri" ||
            string.IsNullOrWhiteSpace(connection.ActionTarget)) return;
        try
        {
            await Launcher.LaunchUriAsync(new Uri(connection.ActionTarget));
        }
        catch { }
    }

    private void ApplyFilter(string? search)
    {
        var query = search?.Trim();
        var matches = string.IsNullOrEmpty(query)
            ? ViewModel.Connections
            : ViewModel.Connections.Where(connection =>
                connection.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                connection.Account.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                connection.AuthSource.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        FilteredConnections.Clear();
        foreach (var connection in matches) FilteredConnections.Add(connection);
        NoMatchesText.Visibility = FilteredConnections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
