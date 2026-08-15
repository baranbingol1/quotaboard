// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Application.Presentation;
using AiLimits.Application.Usage;
using AiLimits.Presentation.WinUI.Localization;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AiLimits.Presentation.WinUI.Pages;

public sealed partial class UsagePage : Page
{
    private const int BreakdownPageSize = 5;
    private const int ModelMixPageSize = 6;
    private const double StackedLayoutWidth = 900;
    // Plot geometry, shared with the matching row heights in UsagePage.xaml.
    private const double PlotHeight = 210;
    private const double SparkHeight = 30;
    private readonly HashSet<string> _providers = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _harnesses = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _projects = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _models = new(StringComparer.OrdinalIgnoreCase);
    private UsageAnalyticsResult? _lastResult;
    private bool _pageReady;
    private bool _updatingFacets;
    private DateOnly _from;
    private DateOnly _through;
    private UsageBreakdownRowViewModel[] _allBreakdownRows = [];
    private int _visibleBreakdownCount = BreakdownPageSize;
    private UsageModelTrendRowViewModel[] _allModelMixRows = [];
    private int _visibleModelMixCount = ModelMixPageSize;
    private double _chartViewportWidth;
    private UsageChartSeriesDimension _chartSeriesDimension = UsageChartSeriesDimension.Provider;
    private bool _isStackedLayout;

    public UsagePage(LiveDashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
        ToolTipService.SetToolTip(ProjectEyebrowLabel, L("Usage_ProjectEyebrowTip"));
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        _through = today;
        _from = today.AddDays(-29);
        FromDatePicker.Date = new DateTimeOffset(_from.ToDateTime(TimeOnly.MinValue));
        ThroughDatePicker.Date = new DateTimeOffset(_through.ToDateTime(TimeOnly.MinValue));
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        _pageReady = true;
    }

    public LiveDashboardViewModel ViewModel { get; }

    private static string L(string key) => LocalizationService.GetString(key);

    private static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key), args);

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.UsageAnalyticsDataChanged += OnUsageAnalyticsDataChanged;
        ThemeService.Applied += OnThemeApplied;
        RefreshQuery();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        ViewModel.UsageAnalyticsDataChanged -= OnUsageAnalyticsDataChanged;
        ThemeService.Applied -= OnThemeApplied;
    }

    private void OnUsageAnalyticsDataChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshQuery);

    private void OnUsageContentSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool stacked = args.NewSize.Width < StackedLayoutWidth;
        if (stacked == _isStackedLayout)
        {
            return;
        }
        _isStackedLayout = stacked;

        QueryColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(260);
        ResultsColumn.Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ResultsColumn.MinWidth = stacked ? 0 : 300;
        SummaryColumn.Width = stacked ? new GridLength(0) : new GridLength(270);

        Grid.SetColumn(QueryPanel, 0);
        Grid.SetColumn(ResultsPanel, stacked ? 0 : 1);
        Grid.SetColumn(SummaryPanel, stacked ? 0 : 2);
        Grid.SetRow(QueryPanel, 0);
        Grid.SetRow(ResultsPanel, stacked ? 1 : 0);
        Grid.SetRow(SummaryPanel, stacked ? 2 : 0);
    }

    private void OnThemeApplied(ElementTheme theme) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_lastResult is not null)
            {
                RenderChart(_lastResult);
                RenderModelMix(_lastResult);
            }
        });

    private void OnDateRangeChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_pageReady || DateRangePicker.SelectedItem is not ComboBoxItem item)
        {
            return;
        }
        string tag = item.Tag as string ?? "30";
        bool custom = string.Equals(tag, "custom", StringComparison.OrdinalIgnoreCase);
        CustomRangePanel.Visibility = custom ? Visibility.Visible : Visibility.Collapsed;
        if (!custom && int.TryParse(tag, out int days))
        {
            _through = DateOnly.FromDateTime(DateTime.Now);
            _from = _through.AddDays(1 - days);
            RefreshQuery();
        }
    }

    private void OnApplyCustomRange(object sender, RoutedEventArgs args)
    {
        if (!FromDatePicker.SelectedDate.HasValue || !ThroughDatePicker.SelectedDate.HasValue)
        {
            return;
        }
        UsageWindowRange range = UsageWindowRange.NormalizeCustom(
            DateOnly.FromDateTime(FromDatePicker.SelectedDate.Value.LocalDateTime),
            DateOnly.FromDateTime(ThroughDatePicker.SelectedDate.Value.LocalDateTime),
            DateOnly.FromDateTime(DateTime.Now));
        _from = range.From;
        _through = range.Through;
        RefreshQuery();
    }

    private void OnQueryControlChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_pageReady) return;
        RefreshQuery();
    }

    private void OnBreakdownChanged(object sender, SelectionChangedEventArgs args)
    {
        if (!_pageReady) return;
        RefreshQuery();
    }

    private void OnProviderSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        ApplyFacetSelection(ProviderFilter, _providers);

    private void OnHarnessSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        ApplyFacetSelection(HarnessFilter, _harnesses);

    private void OnProjectSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        ApplyFacetSelection(ProjectFilter, _projects);

    private void OnModelSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        ApplyFacetSelection(ModelFilter, _models);

    private void ApplyFacetSelection(ListView list, HashSet<string> selected)
    {
        if (!_pageReady || _updatingFacets) return;
        UsageFacetOption[] visible = (list.ItemsSource as IEnumerable<UsageFacetOption> ?? []).ToArray();
        selected.ExceptWith(visible.Select(option => option.Key));
        selected.UnionWith(list.SelectedItems.Cast<UsageFacetOption>().Select(option => option.Key));
        RefreshQuery();
    }

    private void OnProjectSearchChanged(object sender, TextChangedEventArgs args)
    {
        if (_pageReady && _lastResult is not null) RenderFacets(_lastResult.Facets);
    }

    private void OnModelSearchChanged(object sender, TextChangedEventArgs args)
    {
        if (_pageReady && _lastResult is not null) RenderFacets(_lastResult.Facets);
    }

    private void OnChartViewportSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (!_pageReady || _lastResult is null || args.NewSize.Width <= 0
            || Math.Abs(args.NewSize.Width - _chartViewportWidth) < 0.5)
        {
            return;
        }

        _chartViewportWidth = args.NewSize.Width;
        RenderChart(_lastResult);
        // The model-mix sparklines share the chart's time axis, so they have
        // to be re-laid out on the same width change.
        RenderModelMix(_lastResult);
    }

    private void OnChartBarPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: UsageChartBarViewModel bar })
        {
            return;
        }

        ShowChartHoverCard(bar);

        Windows.Foundation.Point point = args.GetCurrentPoint(ChartPlotHost).Position;
        double cardWidth = Math.Max(170, ChartHoverCard.ActualWidth);
        double cardHeight = Math.Max(46, ChartHoverCard.ActualHeight);
        double left = Math.Clamp(point.X + 14, 0, Math.Max(0, ChartPlotHost.ActualWidth - cardWidth));
        double preferredTop = point.Y >= cardHeight + 12 ? point.Y - cardHeight - 10 : point.Y + 14;
        PositionChartHoverCard(Math.Clamp(preferredTop, 0, Math.Max(0, ChartPlotHost.ActualHeight - cardHeight)), left);
    }

    private void OnChartBarGotFocus(object sender, RoutedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: UsageChartBarViewModel bar } host)
        {
            return;
        }

        // Keyboard focus gets the same card a hover would, anchored to the bar
        // instead of the pointer.
        ShowChartHoverCard(bar);
        double cardWidth = Math.Max(170, ChartHoverCard.ActualWidth);
        double cardHeight = Math.Max(46, ChartHoverCard.ActualHeight);
        var bounds = host.TransformToVisual(ChartPlotHost).TransformBounds(new Windows.Foundation.Rect(0, 0, host.ActualWidth, host.ActualHeight));
        double left = Math.Clamp(bounds.X + bounds.Width + 14, 0, Math.Max(0, ChartPlotHost.ActualWidth - cardWidth));
        double top = Math.Clamp(bounds.Y, 0, Math.Max(0, ChartPlotHost.ActualHeight - cardHeight));
        PositionChartHoverCard(top, left);
    }

    private void OnChartBarLostFocus(object sender, RoutedEventArgs args) =>
        ChartHoverCard.Visibility = Visibility.Collapsed;

    private void OnChartBarPointerExited(object sender, PointerRoutedEventArgs args) =>
        ChartHoverCard.Visibility = Visibility.Collapsed;

    private void ShowChartHoverCard(UsageChartBarViewModel bar)
    {
        ChartHoverRangeText.Text = bar.RangeLabel;
        ChartHoverTokensText.Text = F("Usage_HoverTokens", bar.Tokens);
        ChartHoverDetails.ItemsSource = bar.HoverDetails;
        ChartHoverCard.Visibility = Visibility.Visible;
        ChartHoverCard.Measure(new Windows.Foundation.Size(340, double.PositiveInfinity));
    }

    private void PositionChartHoverCard(double top, double left)
    {
        Canvas.SetLeft(ChartHoverCard, left);
        Canvas.SetTop(ChartHoverCard, top);
    }

    private void OnBreakdownShowMore(object sender, RoutedEventArgs args)
    {
        _visibleBreakdownCount = Math.Min(_allBreakdownRows.Length, _visibleBreakdownCount + BreakdownPageSize);
        RenderBreakdownPage();
    }

    private void OnBreakdownShowLess(object sender, RoutedEventArgs args)
    {
        _visibleBreakdownCount = BreakdownPageSize;
        RenderBreakdownPage();
    }

    private void OnChartSeriesProviderClicked(object sender, RoutedEventArgs args) =>
        SetChartSeries(UsageChartSeriesDimension.Provider);

    private void OnChartSeriesHarnessClicked(object sender, RoutedEventArgs args) =>
        SetChartSeries(UsageChartSeriesDimension.Harness);

    private void OnChartSeriesModelClicked(object sender, RoutedEventArgs args) =>
        SetChartSeries(UsageChartSeriesDimension.Model);

    private void SetChartSeries(UsageChartSeriesDimension dimension)
    {
        _chartSeriesDimension = dimension;
        ChartSeriesProviderButton.IsChecked = dimension == UsageChartSeriesDimension.Provider;
        ChartSeriesHarnessButton.IsChecked = dimension == UsageChartSeriesDimension.Harness;
        ChartSeriesModelButton.IsChecked = dimension == UsageChartSeriesDimension.Model;
        RefreshQuery();
    }

    private void OnModelMixShowMore(object sender, RoutedEventArgs args)
    {
        _visibleModelMixCount = Math.Min(_allModelMixRows.Length, _visibleModelMixCount + ModelMixPageSize);
        RenderModelMixPage();
    }

    private void OnModelMixShowLess(object sender, RoutedEventArgs args)
    {
        _visibleModelMixCount = ModelMixPageSize;
        RenderModelMixPage();
    }

    private void OnResetQuery(object sender, RoutedEventArgs args)
    {
        _pageReady = false;
        _providers.Clear();
        _harnesses.Clear();
        _projects.Clear();
        _models.Clear();
        ProjectSearchBox.Text = string.Empty;
        ModelSearchBox.Text = string.Empty;
        DateRangePicker.SelectedIndex = 1;
        TimeGrainPicker.SelectedIndex = 0;
        ComparePicker.SelectedIndex = 0;
        BreakdownPicker.SelectedIndex = 0;
        _chartSeriesDimension = UsageChartSeriesDimension.Provider;
        ChartSeriesProviderButton.IsChecked = true;
        ChartSeriesHarnessButton.IsChecked = false;
        ChartSeriesModelButton.IsChecked = false;
        _visibleModelMixCount = ModelMixPageSize;
        _visibleBreakdownCount = BreakdownPageSize;
        CustomRangePanel.Visibility = Visibility.Collapsed;
        _through = DateOnly.FromDateTime(DateTime.Now);
        _from = _through.AddDays(-29);
        FromDatePicker.Date = new DateTimeOffset(_from.ToDateTime(TimeOnly.MinValue));
        ThroughDatePicker.Date = new DateTimeOffset(_through.ToDateTime(TimeOnly.MinValue));
        _pageReady = true;
        RefreshQuery();
    }

    private void RefreshQuery()
    {
        if (!_pageReady) return;
        UsageAnalyticsQuery query = new(
            _from,
            _through,
            ResolveTimeGrain(),
            ResolveBreakdown(),
            _providers.ToArray(),
            _harnesses.ToArray(),
            _projects.ToArray(),
            _models.ToArray(),
            ComparePicker.SelectedItem is ComboBoxItem { Tag: "previous" },
            _chartSeriesDimension);
        _lastResult = ViewModel.QueryUsage(query);
        RenderResult(_lastResult);
    }

    private void RenderResult(UsageAnalyticsResult result)
    {
        RenderFacets(result.Facets);
        TotalTokensText.Text = FormatTokens(result.TotalTokens);
        ApiEquivalentText.Text = FormatUsd(result.ApiEquivalentCostUsd);
        RenderMatchingData(result);
        WindowLabelText.Text = RangeLabel(result.From, result.Through).ToUpper(CultureInfo.CurrentCulture);
        ComparisonText.Text = ComparisonLabel(result.Comparison);
        EmptyStateText.Visibility = result.MatchingRecordCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateText.Text = EmptyText(result.EmptyReason, "Usage_ChartNoHistory", "Usage_ChartEmpty.Text");
        ChartPlotHost.Visibility = result.MatchingRecordCount == 0 ? Visibility.Collapsed : Visibility.Visible;

        double coverage = result.TotalTokens == 0 ? 0 : 100.0 * result.PricedTokens / result.TotalTokens;
        PricingCoverageText.Text = result.ApiEquivalentCostUsd.HasValue
            ? F("Usage_PricingCoverageDetail", coverage)
            : L("Usage_PricingCoverageNone");

        RenderChart(result);
        RenderBreakdown(result);
        RenderModelMix(result);

        int activeGroups = ActiveFilterGroupCount();
        ActiveFilterCountText.Text = activeGroups == 0
            ? L("Usage_FilterAllData")
            : F("Usage_FilterActiveCount", activeGroups);
        string filterSummary = activeGroups == 0
            ? L("Usage_QueryAllActivity")
            : F(activeGroups == 1 ? "Usage_QueryFiltersOne" : "Usage_QueryFiltersMany", activeGroups);
        QuerySummaryText.Text = $"{RangeLabel(result.From, result.Through)} · {filterSummary} · {BreakdownLabel()}";
    }

    /// <summary>
    /// Says what the matching-record count is actually made of. A bare "159"
    /// is not a quantity anyone can reason about: it is neither requests nor
    /// sessions, it is one row per day, model and project.
    /// </summary>
    private void RenderMatchingData(UsageAnalyticsResult result)
    {
        UsageComposition composition = result.Composition;

        MatchingRecordsText.Text = result.MatchingRecordCount == 0
            ? EmptyText(result.EmptyReason, "Usage_MatchingNoHistory", "Usage_MatchingNone")
            : F("Usage_MatchingRows", result.MatchingRecordCount);
        MatchingRecordsDetailText.Text = result.MatchingRecordCount == 0
            ? EmptyText(result.EmptyReason, "Usage_MatchingNoHistoryDetail", "Usage_MatchingNoneDetail")
            : string.Join(" · ", new[]
            {
                Plural(composition.Providers, "Usage_PluralProvider", "Usage_PluralProviders"),
                Plural(composition.Harnesses, "Usage_PluralHarness", "Usage_PluralHarnesses"),
                Plural(composition.Models, "Usage_PluralModel", "Usage_PluralModels"),
                Plural(composition.Projects, "Usage_PluralProject", "Usage_PluralProjects"),
                F("Usage_ActiveBuckets", composition.ActiveBuckets, GrainNoun(composition.ActiveBuckets))
            });
    }

    private static string Plural(int count, string singularKey, string pluralKey) =>
        F(count == 1 ? singularKey : pluralKey, count);

    /// <summary>Names the current bucket size, so counts read as "12 active days" rather than a bare number.</summary>
    private string GrainNoun(int count) => ResolveTimeGrain() switch
    {
        UsageTimeGrain.Month => L(count == 1 ? "Usage_GrainMonthNoun" : "Usage_GrainMonthsNoun"),
        UsageTimeGrain.Week => L(count == 1 ? "Usage_GrainWeekNoun" : "Usage_GrainWeeksNoun"),
        _ => L(count == 1 ? "Usage_GrainDayNoun" : "Usage_GrainDaysNoun")
    };

    /// <summary>
    /// Per-model totals plus a sparkline on the chart's own buckets. Combined
    /// with the harness facet this answers questions like "what has Droid been
    /// running lately, and how much of it was which model".
    /// </summary>
    private void RenderModelMix(UsageAnalyticsResult result)
    {
        int bucketCount = Math.Max(1, result.Chart.Count);
        // Track the chart's own bar pitch so the sparkline reads as the same
        // time axis, but keep it legible when there are very many buckets.
        double available = _chartViewportWidth > 0 ? _chartViewportWidth : 520;
        double barWidth = Math.Clamp((available - 2.0 * bucketCount) / bucketCount, 2, 14);

        // Same ramp, same order as the chart's Series-by-model legend: both rank
        // by tokens descending and tie-break on label, so row N and legend entry
        // N are the same model and now agree on colour. Brand hues cannot do
        // this job — every claude-* model normalises to the one Claude orange,
        // which is exactly why the mix showed four indistinguishable rows while
        // the chart drew those models in four different ramp slots. (The
        // correspondence is with the Series-by-model legend; grouping the chart
        // by provider colours it by brand, and makes no per-model claim.)
        _allModelMixRows = result.ModelTrends.Select((trend, rank) =>
        {
            Brush accent = ChartSeriesBrush(rank, isOthers: false);
            long peak = Math.Max(1, trend.PeakBucketTokens);
            UsageSparkBarViewModel[] spark = trend.BucketTokens.Select((tokens, index) =>
            {
                UsageChartBucket bucket = result.Chart[index];
                string range = RangeLabel(bucket.From, bucket.Through);
                return new UsageSparkBarViewModel(
                    barWidth,
                    tokens == 0 ? 1 : Math.Max(2, SparkHeight * tokens / peak),
                    tokens == 0 ? 0.16 : 1.0,
                    F("Usage_SparkTooltip", range, FormatTokens(tokens)),
                    F("Usage_SparkAutomation", trend.Label, range, FormatTokens(tokens)),
                    accent);
            }).ToArray();

            string grain = GrainNoun(result.Chart.Count);
            string activity = trend.FirstUsed.HasValue && trend.LastUsed.HasValue
                ? F("Usage_ModelActivityWindow", trend.ActiveBuckets, result.Chart.Count, grain, RangeLabel(trend.FirstUsed.Value, trend.LastUsed.Value))
                : F("Usage_ModelActivity", trend.ActiveBuckets, result.Chart.Count, grain);

            return new UsageModelTrendRowViewModel(
                trend.Key,
                trend.Label,
                FormatTokens(trend.Tokens),
                $"{trend.SharePercent:0.#}%",
                FormatUsd(trend.ApiEquivalentCostUsd),
                activity,
                accent,
                spark);
        }).ToArray();

        ModelMixSubtitleText.Text = _allModelMixRows.Length == 0
            ? EmptyText(result.EmptyReason, "Usage_ModelMixNoHistory", "Usage_ModelMixEmpty.Text")
            : F(_allModelMixRows.Length == 1 ? "Usage_ModelMixSubtitleOne" : "Usage_ModelMixSubtitleMany", _allModelMixRows.Length);
        ModelMixWindowText.Text = RangeLabel(result.From, result.Through).ToUpper(CultureInfo.CurrentCulture);
        ModelMixEmptyText.Visibility = _allModelMixRows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderModelMixPage();
    }

    private static string EmptyText(UsageEmptyReason reason, string noHistoryKey, string noMatchesKey) =>
        L(reason == UsageEmptyReason.NoHistory ? noHistoryKey : noMatchesKey);

    private void RenderModelMixPage()
    {
        int visible = Math.Min(_visibleModelMixCount, _allModelMixRows.Length);
        ModelMixItems.ItemsSource = _allModelMixRows.Take(visible).ToArray();
        int remaining = _allModelMixRows.Length - visible;
        ModelMixShowMoreButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        ModelMixShowLessButton.Visibility = visible > ModelMixPageSize ? Visibility.Visible : Visibility.Collapsed;
        ModelMixShowMoreButton.Content = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.GetString("Usage_ShowMore"),
            remaining);
        ModelMixShowLessButton.Content = LocalizationService.GetString("Usage_ShowLess");
    }

    private void RenderChart(UsageAnalyticsResult result)
    {
        long maximum = Math.Max(1, result.Chart.Select(bucket => bucket.Tokens).DefaultIfEmpty().Max());
        YAxisMaxText.Text = FormatTokens(maximum);
        YAxisMidText.Text = FormatTokens(maximum / 2);

        Dictionary<string, Brush> seriesBrushes = result.ChartLegend
            .Select((item, index) => new
            {
                item.Key,
                Brush = _chartSeriesDimension == UsageChartSeriesDimension.Provider && !item.IsOthers
                    ? new SolidColorBrush(Theming.ThemeDictionaryBuilder.Parse(ProviderColors.Resolve(item.Key, item.Label)))
                    : ChartSeriesBrush(index, item.IsOthers),
            })
            .ToDictionary(item => item.Key, item => item.Brush, StringComparer.OrdinalIgnoreCase);

        long legendTotal = Math.Max(1, result.ChartLegend.Sum(item => item.Tokens));
        ChartLegendItems.ItemsSource = result.ChartLegend.Select(item =>
            new UsageChartLegendViewModel(
                item.Key,
                item.IsOthers && item.PooledSeriesCount > 0
                    ? F("Usage_OthersSelected", item.PooledSeriesCount)
                    : item.Label,
                FormatTokens(item.Tokens),
                $"{100.0 * item.Tokens / legendTotal:0.#}%",
                seriesBrushes[item.Key]))
            .ToArray();

        int bucketCount = result.Chart.Count;
        double availableWidth = _chartViewportWidth > 0
            ? _chartViewportWidth
            : Math.Max(320, ChartScrollViewer.ActualWidth);
        UsageChartGeometry geometry = UsageChartLayout.Calculate(availableWidth, bucketCount);
        bool[] labelled = UsageChartLayout.LabelledBuckets(bucketCount, geometry.ItemWidth);
        RenderChartAxis(result, geometry, labelled);
        ChartItems.ItemsSource = result.Chart.Select((bucket, index) =>
        {
            string range = RangeLabel(bucket.From, bucket.Through);
            string label = labelled[index]
                ? ChartLabel(bucket, ResolveTimeGrain(), geometry.ItemWidth)
                : string.Empty;
            UsageChartSegmentViewModel[] segments = bucket.Segments.Select(segment =>
                new UsageChartSegmentViewModel(
                    segment.Key,
                    segment.Label,
                    FormatTokens(segment.Tokens),
                    segment.Tokens,
                    PlotHeight * segment.Tokens / maximum,
                    seriesBrushes[segment.Key]))
                .ToArray();

            UsageChartHoverDetailViewModel[] hoverDetails = segments
                .Where(segment => segment.RawTokens > 0)
                .Select(segment => new UsageChartHoverDetailViewModel(
                    segment.Label,
                    $"{segment.Tokens} · " +
                    $"{(bucket.Tokens == 0 ? 0 : 100.0 * segment.RawTokens / bucket.Tokens):0.#}%",
                    segment.Brush))
                .ToArray();

            string tokens = FormatTokens(bucket.Tokens);
            return new UsageChartBarViewModel(
                label,
                range,
                tokens,
                F("Usage_BarAutomation", range, tokens),
                bucket.Tokens == 0 ? 0 : Math.Max(2, PlotHeight * bucket.Tokens / maximum),
                geometry.ItemWidth,
                geometry.BarWidth,
                segments,
                hoverDetails);
        }).ToArray();
    }

    /// <summary>
    /// Lays the date axis out on a Canvas, centring each label on its bar.
    /// The labels cannot live inside the bar template: a bucket cell is about
    /// ten pixels wide on a 30-day window and clips anything drawn in it, so
    /// every two-digit day number lost its second digit.
    /// </summary>
    private void RenderChartAxis(
        UsageAnalyticsResult result,
        UsageChartGeometry geometry,
        bool[] labelled)
    {
        ChartAxisCanvas.Children.Clear();
        if (result.MatchingRecordCount == 0)
        {
            return;
        }

        const double gap = 3;
        UsageTimeGrain grain = ResolveTimeGrain();
        for (int index = 0; index < result.Chart.Count && index < labelled.Length; index++)
        {
            if (!labelled[index])
            {
                continue;
            }

            var text = new TextBlock
            {
                Text = ChartLabel(result.Chart[index], grain, geometry.ItemWidth),
                FontSize = 11,
                FontFamily = ThemeService.ResolveThemeResource("MetricFont") as FontFamily ?? FontFamily.XamlAutoFontFamily,
                Foreground = ThemeService.ResolveThemeResource("TextSecondaryBrush") as Brush,
            };
            text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double centre = index * (geometry.ItemWidth + gap) + geometry.ItemWidth / 2;
            // Clamp inside the plot: the first and last labels are centred on
            // bars flush with the edges, so half of each would fall outside.
            double width = text.DesiredSize.Width;
            double limit = Math.Max(0, geometry.ConsumedWidth - width);
            Canvas.SetLeft(text, Math.Clamp(centre - width / 2, 0, limit));
            Canvas.SetTop(text, 9);
            ChartAxisCanvas.Children.Add(text);
        }
    }

    private Brush ChartSeriesBrush(int legendIndex, bool isOthers)
    {
        string resourceKey = ChartSeriesBrushResolver.ResolveResourceKey(legendIndex, isOthers);
        return ThemeService.ResolveThemeResource(resourceKey) as Brush
            ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }

    private void RenderBreakdown(UsageAnalyticsResult result)
    {
        _allBreakdownRows = result.Breakdown.Select(item => new UsageBreakdownRowViewModel(
            item.Key,
            item.Label,
            FormatTokens(item.Tokens),
            FormatTokens(item.InputTokens),
            FormatTokens(item.OutputTokens),
            FormatTokens(item.CacheReadTokens),
            FormatTokens(item.CacheWriteTokens),
            FormatTokens(item.ReasoningTokens),
            FormatUsd(item.ApiEquivalentCostUsd),
            $"{item.SharePercent:0.#}%",
            item.SharePercent,
            item.Tokens)).ToArray();
        RenderBreakdownPage();
    }

    private void RenderBreakdownPage()
    {
        int visible = Math.Min(_visibleBreakdownCount, _allBreakdownRows.Length);
        BreakdownItems.ItemsSource = _allBreakdownRows.Take(visible).ToArray();
        TokenSplitTable.ItemsSource = _allBreakdownRows.Take(visible).ToArray();
        int remaining = _allBreakdownRows.Length - visible;
        BreakdownShowMoreButton.Visibility = remaining > 0 ? Visibility.Visible : Visibility.Collapsed;
        BreakdownShowLessButton.Visibility = visible > BreakdownPageSize ? Visibility.Visible : Visibility.Collapsed;
        BreakdownShowMoreButton.Content = string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.GetString("Usage_ShowMore"),
            remaining);
        BreakdownShowLessButton.Content = LocalizationService.GetString("Usage_ShowLess");
    }

    private void RenderFacets(UsageFacetSet facets)
    {
        _updatingFacets = true;
        try
        {
            SetFacetItems(ProviderFilter, facets.Providers, _providers, null);
            SetFacetItems(HarnessFilter, facets.Harnesses, _harnesses, null);
            SetFacetItems(ProjectFilter, facets.Projects, _projects, ProjectSearchBox.Text);
            SetFacetItems(ModelFilter, facets.Models, _models, ModelSearchBox.Text);
            ProviderCountText.Text = FacetCount(facets.Providers.Count, _providers.Count);
            HarnessCountText.Text = FacetCount(facets.Harnesses.Count, _harnesses.Count);
            ProjectCountText.Text = FacetCount(facets.Projects.Count, _projects.Count);
            ModelCountText.Text = FacetCount(facets.Models.Count, _models.Count);
            RenderProjectSourceNote(facets.Harnesses);
        }
        finally
        {
            _updatingFacets = false;
        }
    }

    // Harnesses whose token sources hand up ProjectIdentity.Unknown for every
    // event, so their tokens can never carry a project: Amp does not record a
    // working directory (AmpThreadTokenSource) and Cursor's usage arrives from
    // the dashboard API, whose payload has no folder at all.
    private static readonly string[] ProjectlessHarnessKeys = ["amp", "cursor"];

    private void RenderProjectSourceNote(IReadOnlyList<UsageFacetOption> harnesses)
    {
        string[] names = harnesses
            .Where(option => ProjectlessHarnessKeys.Contains(option.Key, StringComparer.OrdinalIgnoreCase))
            .Select(option => option.Label)
            .OrderBy(label => label, StringComparer.CurrentCulture)
            .ToArray();
        if (names.Length == 0)
        {
            ProjectSourceNote.Visibility = Visibility.Collapsed;
            return;
        }
        // Phrased so one name and two read the same way.
        ProjectSourceNoteText.Text = F(
            "Usage_ProjectSourceNote",
            string.Join(L("Usage_JoinAnd"), names),
            L("Common_Unknown"));
        ProjectSourceNote.Visibility = Visibility.Visible;
    }

    private static void SetFacetItems(
        ListView list,
        IReadOnlyList<UsageFacetOption> options,
        IReadOnlySet<string> selected,
        string? search)
    {
        UsageFacetOption[] visible = string.IsNullOrWhiteSpace(search)
            ? options.ToArray()
            : options.Where(option => option.Label.Contains(search.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToArray();
        list.ItemsSource = visible;
        list.SelectedItems.Clear();
        foreach (UsageFacetOption option in visible.Where(option => selected.Contains(option.Key)))
        {
            list.SelectedItems.Add(option);
        }
    }

    private UsageTimeGrain ResolveTimeGrain()
    {
        string tag = (TimeGrainPicker.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        if (tag == "day") return UsageTimeGrain.Day;
        if (tag == "week") return UsageTimeGrain.Week;
        if (tag == "month") return UsageTimeGrain.Month;
        int days = _through.DayNumber - _from.DayNumber + 1;
        return days <= 45 ? UsageTimeGrain.Day : days <= 180 ? UsageTimeGrain.Week : UsageTimeGrain.Month;
    }

    private UsageBreakdownDimension ResolveBreakdown() =>
        ((BreakdownPicker.SelectedItem as ComboBoxItem)?.Tag as string) switch
        {
            "tool" => UsageBreakdownDimension.Harness,
            "project" => UsageBreakdownDimension.Project,
            "model" => UsageBreakdownDimension.Model,
            _ => UsageBreakdownDimension.Provider
        };

    private string BreakdownLabel() => ResolveBreakdown() switch
    {
        UsageBreakdownDimension.Harness => L("Usage_BreakdownByHarness"),
        UsageBreakdownDimension.Project => L("Usage_BreakdownByProject"),
        UsageBreakdownDimension.Model => L("Usage_BreakdownByModel"),
        _ => L("Usage_BreakdownByProvider")
    };

    private int ActiveFilterGroupCount() =>
        (_providers.Count > 0 ? 1 : 0)
        + (_harnesses.Count > 0 ? 1 : 0)
        + (_projects.Count > 0 ? 1 : 0)
        + (_models.Count > 0 ? 1 : 0);

    private static string FacetCount(int available, int selected) =>
        F(selected > 0 ? "Usage_FacetSelected" : "Usage_FacetAvailable", selected > 0 ? selected : available);

    private static string ComparisonLabel(UsageComparison? comparison)
    {
        if (comparison is null) return L("Usage_ComparisonOff");
        if (comparison.IsNew) return L("Usage_ComparisonNew");
        if (!comparison.PercentChange.HasValue) return L("Usage_ComparisonNoBaseline");
        return F("Usage_ComparisonChange", comparison.PercentChange.Value);
    }


    private static string ChartLabel(UsageChartBucket bucket, UsageTimeGrain grain, double itemWidth) => grain switch
    {
        UsageTimeGrain.Month => bucket.From.ToString("MMM yy", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture),
        _ when itemWidth >= UsageChartLayout.WideLabelWidth => bucket.From.ToString("d MMM", CultureInfo.CurrentCulture),
        _ => bucket.From.Day.ToString(CultureInfo.CurrentCulture)
    };

    private static string RangeLabel(DateOnly from, DateOnly through) =>
        from == through
            ? from.ToString("d MMM yyyy", CultureInfo.CurrentCulture)
            : $"{from.ToString("d MMM", CultureInfo.CurrentCulture)} – {through.ToString("d MMM yyyy", CultureInfo.CurrentCulture)}";

    private static string FormatTokens(long value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };

    private static string FormatUsd(decimal? value) =>
        value.HasValue ? value.Value.ToString("$0.00", CultureInfo.InvariantCulture) : "—";
}
