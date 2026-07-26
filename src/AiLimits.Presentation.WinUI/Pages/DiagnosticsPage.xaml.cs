// SPDX-License-Identifier: Apache-2.0
using System.Collections.Specialized;
using AiLimits.Presentation.WinUI.Converters;
using AiLimits.Presentation.WinUI.Localization;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace AiLimits.Presentation.WinUI.Pages;

public sealed partial class DiagnosticsPage : Page
{
    // Shared with the provider cards and the Connections rows: one status
    // colour scale everywhere, so a red dot always means the same thing.
    private static readonly CardStatusBrushConverter StatusBrush = new();

    private NotifyCollectionChangedEventHandler? _attemptsChangedHandler;

    // Rows are built in code, so theme resources have to be looked up by hand.
    // ThemeService knows which theme dictionary is live; the flat lookup covers
    // resources declared outside one.
    private static T? Resource<T>(string key) where T : class =>
        ThemeService.ResolveThemeResource(key) as T
            ?? (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue(key, out object? value) ? value as T : null);

    public DiagnosticsPage(LiveDashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        BindStatusCard(0, nameof(ViewModel.PricingCatalogStatus), nameof(ViewModel.PricingCatalogDetail));
        BindStatusCard(1, nameof(ViewModel.ScannerStatus), nameof(ViewModel.ScannerDetail));
        BindStatusCard(2, nameof(ViewModel.DatabaseStatus), nameof(ViewModel.DatabaseDetail));
        _attemptsChangedHandler = (_, _) => RenderAttempts();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public LiveDashboardViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.RecentAttempts.CollectionChanged += _attemptsChangedHandler;
        RenderAttempts();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.RecentAttempts.CollectionChanged -= _attemptsChangedHandler;
    }

    private void BindStatusCard(int index, string statusPath, string detailPath)
    {
        if (StatusGrid.Children[index] is not Border { Child: StackPanel panel }) return;
        if (panel.Children[1] is TextBlock status)
            status.SetBinding(TextBlock.TextProperty, new Binding
            {
                Source = ViewModel, Path = new PropertyPath(statusPath), Mode = BindingMode.OneWay
            });
        if (panel.Children[2] is TextBlock detail)
            detail.SetBinding(TextBlock.TextProperty, new Binding
            {
                Source = ViewModel, Path = new PropertyPath(detailPath), Mode = BindingMode.OneWay
            });
    }

    private void RenderAttempts()
    {
        var stack = new StackPanel();
        stack.Children.Add(CreateHeaderRow());
        foreach (var attempt in ViewModel.RecentAttempts)
        {
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = Resource<Brush>("SurfaceStrokeBrush")
            });
            stack.Children.Add(CreateAttemptRow(attempt));
        }
        if (ViewModel.RecentAttempts.Count == 0)
            stack.Children.Add(new TextBlock
            {
                Text = LocalizationService.GetString("Diagnostics_NoAttempts"),
                Padding = new Thickness(18, 16, 18, 16),
                Foreground = Resource<Brush>("TextSecondaryBrush"),
            });
        AttemptsHost.Child = stack;
    }

    // A leading status dot, then the provider (with the source that answered
    // for it underneath), the outcome, what that outcome means, and how long
    // it took. Header and body share this column set so they stay aligned.
    private static Grid CreateRowGrid() => new()
    {
        Padding = new Thickness(18, 13, 18, 13),
        // Without a gutter the wrapped provider/outcome/meaning text runs
        // edge to edge and the columns read as one paragraph.
        ColumnSpacing = 14,
        ColumnDefinitions =
        {
            new() { Width = new GridLength(10) },
            new() { Width = new GridLength(1.2, GridUnitType.Star), MinWidth = 140 },
            new() { Width = new GridLength(0.9, GridUnitType.Star), MinWidth = 110 },
            new() { Width = new GridLength(2, GridUnitType.Star), MinWidth = 160 },
            new() { Width = new GridLength(64) },
        }
    };

    private static Grid CreateHeaderRow()
    {
        var grid = CreateRowGrid();
        AddHeader(grid, "Diagnostics_AttemptsProvider", 1);
        AddHeader(grid, "Diagnostics_AttemptsResult", 2);
        AddHeader(grid, "Diagnostics_AttemptsMeaning", 3);
        AddHeader(grid, "Diagnostics_AttemptsDuration", 4);
        return grid;
    }

    private static Grid CreateAttemptRow(FetchAttemptViewModel attempt)
    {
        var grid = CreateRowGrid();
        var dot = new Ellipse
        {
            Width = 8, Height = 8,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 6, 0, 0),
            Fill = (Brush)StatusBrush.Convert(attempt.StatusKind, typeof(Brush), null!, string.Empty),
        };
        grid.Children.Add(dot);

        var identity = new StackPanel { Spacing = 2 };
        identity.Children.Add(new TextBlock
        {
            Text = attempt.Provider, FontSize = 14, TextWrapping = TextWrapping.Wrap,
        });
        // The strategy id is the one thing worth quoting verbatim in a bug
        // report, so it stays — as a monospace subtitle rather than a column
        // of jargon competing with the provider name.
        var source = new TextBlock
        {
            Text = attempt.Strategy, FontSize = 11,
            Foreground = Resource<Brush>("TextSecondaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        // A null FontFamily is not a legal local value, so only override the
        // inherited font when the theme actually supplies a monospace one.
        if (Resource<FontFamily>("MetricFont") is { } metricFont) source.FontFamily = metricFont;
        identity.Children.Add(source);
        Grid.SetColumn(identity, 1);
        grid.Children.Add(identity);

        AddText(grid, attempt.Status, 2, secondary: false);
        AddText(grid, attempt.Meaning, 3, secondary: true);
        AddText(grid, attempt.Duration, 4, secondary: true);
        return grid;
    }

    private static void AddHeader(Grid grid, string resourceKey, int column)
    {
        var block = new TextBlock
        {
            Text = LocalizationService.GetString(resourceKey),
            TextWrapping = TextWrapping.Wrap, FontSize = 11, CharacterSpacing = 100,
        };
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }

    private static void AddText(Grid grid, string text, int column, bool secondary)
    {
        var block = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap,
            FontSize = secondary ? 12 : 14,
        };
        if (secondary)
            block.Foreground = Resource<Brush>("TextSecondaryBrush");
        Grid.SetColumn(block, column);
        grid.Children.Add(block);
    }
}
