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

    public UsagePage(LiveDashboardViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();
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

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ViewModel.UsageAnalyticsDataChanged += OnUsageAnalyticsDataChanged;
        RefreshQuery();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args) =>
        ViewModel.UsageAnalyticsDataChanged -= OnUsageAnalyticsDataChanged;

    private void OnUsageAnalyticsDataChanged(object? sender, EventArgs args) =>
        DispatcherQueue.TryEnqueue(RefreshQuery);

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

        ChartHoverRangeText.Text = bar.RangeLabel;
        ChartHoverTokensText.Text = $"{bar.Tokens} TOKENS";
        ChartHoverDetailsText.Text = bar.HoverDetails;
        ChartHoverCard.Visibility = Visibility.Visible;

        Windows.Foundation.Point point = args.GetCurrentPoint(ChartPlotHost).Position;
        double cardWidth = Math.Max(170, ChartHoverCard.ActualWidth);
        double cardHeight = Math.Max(46, ChartHoverCard.ActualHeight);
        double left = Math.Clamp(point.X + 14, 0, Math.Max(0, ChartPlotHost.ActualWidth - cardWidth));
        double preferredTop = point.Y >= cardHeight + 12 ? point.Y - cardHeight - 10 : point.Y + 14;
        double top = Math.Clamp(preferredTop, 0, Math.Max(0, ChartPlotHost.ActualHeight - cardHeight));
        Canvas.SetLeft(ChartHoverCard, left);
        Canvas.SetTop(ChartHoverCard, top);
    }

    private void OnChartBarPointerExited(object sender, PointerRoutedEventArgs args) =>
        ChartHoverCard.Visibility = Visibility.Collapsed;

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
        ChartScrollViewer.Visibility = result.MatchingRecordCount == 0 ? Visibility.Collapsed : Visibility.Visible;

        double coverage = result.TotalTokens == 0 ? 0 : 100.0 * result.PricedTokens / result.TotalTokens;
        PricingCoverageText.Text = result.ApiEquivalentCostUsd.HasValue
            ? $"Public API-price estimate · {coverage:0.#}% of matching tokens priced"
            : "No exact public API price is available for this query";

        RenderChart(result);
        RenderBreakdown(result);
        RenderModelMix(result);

        int activeGroups = ActiveFilterGroupCount();
        ActiveFilterCountText.Text = activeGroups == 0 ? "ALL DATA" : $"{activeGroups} ACTIVE";
        QuerySummaryText.Text = $"{RangeLabel(result.From, result.Through)} · {(activeGroups == 0 ? "all activity" : $"{activeGroups} filter{(activeGroups == 1 ? "" : "s")}")} · {BreakdownLabel()}";
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
            ? "Nothing matches this query"
            : $"{result.MatchingRecordCount:N0} rows · {FormatTokens(result.TotalTokens)} tokens";
        MatchingRecordsDetailText.Text = result.MatchingRecordCount == 0
            ? "Clear a filter or widen the date range."
            : string.Join(" · ", new[]
            {
                Plural(composition.Providers, "provider", "providers"),
                Plural(composition.Harnesses, "harness", "harnesses"),
                Plural(composition.Models, "model", "models"),
                Plural(composition.Projects, "project", "projects"),
                $"{composition.ActiveBuckets} active {GrainNoun(composition.ActiveBuckets)}"
            });
    }

    private static string Plural(int count, string singular, string plural) =>
        $"{count} {(count == 1 ? singular : plural)}";

    /// <summary>Names the current bucket size, so counts read as "12 active days" rather than a bare number.</summary>
    private string GrainNoun(int count) => ResolveTimeGrain() switch
    {
        UsageTimeGrain.Month => count == 1 ? "month" : "months",
        UsageTimeGrain.Week => count == 1 ? "week" : "weeks",
        _ => count == 1 ? "day" : "days"
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

        _allModelMixRows = result.ModelTrends.Select(trend =>
        {
            Brush accent = new SolidColorBrush(
                Theming.ThemeDictionaryBuilder.Parse(ProviderColors.Resolve(trend.Key, trend.Label)));
            long peak = Math.Max(1, trend.PeakBucketTokens);
            UsageSparkBarViewModel[] spark = trend.BucketTokens.Select((tokens, index) =>
            {
                UsageChartBucket bucket = result.Chart[index];
                return new UsageSparkBarViewModel(
                    barWidth,
                    tokens == 0 ? 1 : Math.Max(2, SparkHeight * tokens / peak),
                    tokens == 0 ? 0.16 : 1.0,
                    $"{RangeLabel(bucket.From, bucket.Through)} · {FormatTokens(tokens)} tokens",
                    accent);
            }).ToArray();

            string activity = trend.FirstUsed.HasValue && trend.LastUsed.HasValue
                ? $"used on {trend.ActiveBuckets} of {result.Chart.Count} {GrainNoun(result.Chart.Count)} · {RangeLabel(trend.FirstUsed.Value, trend.LastUsed.Value)}"
                : $"used on {trend.ActiveBuckets} of {result.Chart.Count} {GrainNoun(result.Chart.Count)}";

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
            ? "No models match this query."
            : $"{_allModelMixRows.Length} model{(_allModelMixRows.Length == 1 ? "" : "s")} in this query, newest activity on the right.";
        ModelMixWindowText.Text = RangeLabel(result.From, result.Through).ToUpper(CultureInfo.CurrentCulture);
        ModelMixEmptyText.Visibility = _allModelMixRows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        RenderModelMixPage();
    }

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
            .Select(item => new
            {
                item.Key,
                Brush = ChartSeriesBrush(item.Key, item.Label, item.IsOthers),
            })
            .ToDictionary(item => item.Key, item => item.Brush, StringComparer.OrdinalIgnoreCase);

        long legendTotal = Math.Max(1, result.ChartLegend.Sum(item => item.Tokens));
        ChartLegendItems.ItemsSource = result.ChartLegend.Select(item =>
            new UsageChartLegendViewModel(
                item.Key,
                item.Label,
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

            string hoverDetails = string.Join(
                "\n",
                bucket.Segments
                    .Where(segment => segment.Tokens > 0)
                    .Select(segment =>
                        $"{segment.Label} · {FormatTokens(segment.Tokens)} · " +
                        $"{(bucket.Tokens == 0 ? 0 : 100.0 * segment.Tokens / bucket.Tokens):0.#}%"));

            return new UsageChartBarViewModel(
                label,
                range,
                FormatTokens(bucket.Tokens),
                $"{range}\n{FormatTokens(bucket.Tokens)} tokens",
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

    private Brush ChartSeriesBrush(string key, string label, bool isOthers)
    {
        if (isOthers)
        {
            return ThemeService.ResolveThemeResource("ChartSeriesOthersBrush") as Brush
                ?? new SolidColorBrush(Microsoft.UI.Colors.Gray);
        }
        // Chart series are providers/tools, so they carry the same accent as
        // everywhere else in the app instead of a positional theme color.
        return new SolidColorBrush(Theming.ThemeDictionaryBuilder.Parse(ProviderColors.Resolve(key, label)));
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
        ProjectSourceNoteText.Text =
            $"No working directory is reported for {string.Join(" and ", names)}, so that usage lands under Unknown.";
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
        UsageBreakdownDimension.Harness => "by harness",
        UsageBreakdownDimension.Project => "by project",
        UsageBreakdownDimension.Model => "by model",
        _ => "by provider"
    };

    private int ActiveFilterGroupCount() =>
        (_providers.Count > 0 ? 1 : 0)
        + (_harnesses.Count > 0 ? 1 : 0)
        + (_projects.Count > 0 ? 1 : 0)
        + (_models.Count > 0 ? 1 : 0);

    private static string FacetCount(int available, int selected) =>
        selected > 0 ? $"{selected} SELECTED" : $"{available} AVAILABLE";

    private static string ComparisonLabel(UsageComparison? comparison)
    {
        if (comparison is null) return "Comparison off";
        if (comparison.IsNew) return "New vs previous period";
        if (!comparison.PercentChange.HasValue) return "No previous-period baseline";
        return $"{comparison.PercentChange.Value:+0.#;-0.#;0}% vs previous period";
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
