// SPDX-License-Identifier: Apache-2.0
using System.Collections.ObjectModel;
using System.Globalization;
using AiLimits.Application.Pricing;
using AiLimits.Application.Usage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AiLimits.Presentation.WinUI.Localization;

namespace AiLimits.Presentation.WinUI.ViewModels;

public sealed partial class LiveDashboardViewModel : ObservableObject, IDisposable
{
    private readonly IDashboardDataSource _dataSource;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _initialized;
    private IReadOnlyList<UsageHistoryDayViewModel> _usageHistory = [];
    private IReadOnlyList<UsageModelSliceViewModel> _usageModelSlices = [];
    private IReadOnlyList<UsageAnalyticsRecord> _usageAnalyticsRecords = [];
    private IReadOnlyList<ProjectUsageSliceViewModel> _projectUsageSlices = [];
    private IReadOnlyList<ModelUsageRowViewModel> _allModelRows = [];
    private IReadOnlyList<ProjectUsageViewModel> _allProjectRows = [];
    private const int PageSize = 10;
    private int _visibleModelCount = PageSize;
    private int _visibleProjectCount = PageSize;
    private string _projectSort = "tokens";
    private DateOnly _usageWindowFrom;
    private DateOnly _usageWindowThrough;

    public LiveDashboardViewModel(IDashboardDataSource dataSource)
    {
        _dataSource = dataSource;
        DashboardDisplayPreference.Changed += OnDisplayPreferenceChanged;
        EmailPrivacyPreference.Changed += OnEmailPrivacyChanged;
    }

    public event EventHandler? UsageAnalyticsDataChanged;

    public ObservableCollection<ResetHorizonItemViewModel> ResetHorizon { get; } = [];
    public ObservableCollection<ProviderCardViewModel> Providers { get; } = [];
    public ObservableCollection<ProviderUsageViewModel> ProviderUsage { get; } = [];
    public ObservableCollection<ProviderUsageViewModel> HarnessUsage { get; } = [];
    public ObservableCollection<UsageDayViewModel> UsageDays { get; } = [];
    public ObservableCollection<HeatmapCellViewModel> HeatmapCells { get; } = [];
    public ObservableCollection<ResetCycleViewModel> ResetCycles { get; } = [];
    public ObservableCollection<ModelUsageRowViewModel> ModelRows { get; } = [];
    /// <summary>Every observed model row (unpaged); used by the manual-pricing settings.</summary>
    public IReadOnlyList<ModelUsageRowViewModel> AllModelRows => _allModelRows;
    public ObservableCollection<ProviderConnectionViewModel> Connections { get; } = [];
    public ObservableCollection<FetchAttemptViewModel> RecentAttempts { get; } = [];
    public ObservableCollection<ProjectUsageViewModel> ProjectUsage { get; } = [];

    [ObservableProperty] public partial string TotalTokens { get; set; } = "-";
    [ObservableProperty] public partial string ApiEquivalentCost { get; set; } = "-";
    [ObservableProperty] public partial string Coverage { get; set; } = L("Dashboard_Discovering");
    [ObservableProperty] public partial string PlanValueLabel { get; set; } = "-";
    [ObservableProperty] public partial string PlanValueDetail { get; set; } = L("Dashboard_SetPlanSpend");
    [ObservableProperty] public partial string CacheSavingsLabel { get; set; } = "-";
    [ObservableProperty] public partial string WeekDeltaLabel { get; set; } = "";
    [ObservableProperty] public partial string CatalogCaption { get; set; } = L("Dashboard_CatalogNotLoaded");
    [ObservableProperty] public partial string UsageTotal { get; set; } = "-";
    [ObservableProperty] public partial string InputTokens { get; set; } = "-";
    [ObservableProperty] public partial string OutputTokens { get; set; } = "-";
    [ObservableProperty] public partial string CacheReadTokens { get; set; } = "-";
    [ObservableProperty] public partial string CacheWriteTokens { get; set; } = "-";
    [ObservableProperty] public partial string ReasoningTokens { get; set; } = "-";
    [ObservableProperty] public partial string ReportedServiceCost { get; set; } = "-";
    [ObservableProperty] public partial string PricingCatalogStatus { get; set; } = L("Dashboard_LoadingUpper");
    [ObservableProperty] public partial string PricingCatalogDetail { get; set; } = L("Dashboard_CheckingCache");
    [ObservableProperty] public partial string ScannerStatus { get; set; } = "0 / 0";
    [ObservableProperty] public partial string ScannerDetail { get; set; } = L("Dashboard_DiscoveringTelemetry");
    [ObservableProperty] public partial string DatabaseStatus { get; set; } = L("Dashboard_OpeningUpper");
    [ObservableProperty] public partial string DatabaseDetail { get; set; } = L("Dashboard_InitializingDatabase");
    [ObservableProperty] public partial string StatusMessage { get; set; } = L("Dashboard_LoadingProviderData");
    [ObservableProperty] public partial string LastUpdated { get; set; } = L("Dashboard_NotLoaded");
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveUsageGrouping))]
    public partial bool UsageByProvider { get; set; } = true;

    public ObservableCollection<ProviderUsageViewModel> ActiveUsageGrouping => UsageByProvider ? ProviderUsage : HarnessUsage;
    [ObservableProperty] public partial string UsageWindowTitle { get; set; } = F("UsageWindow_LastDays", 30);
    [ObservableProperty] public partial string UsageWindowSummary { get; set; } = "-";
    [ObservableProperty] public partial string PeriodDeltaLabel { get; set; } = "";
    [ObservableProperty] public partial string ThirtyDayDeltaLabel { get; set; } = "";
    [ObservableProperty] public partial string NinetyDayDeltaLabel { get; set; } = "";
    [ObservableProperty] public partial string ProjectReconciliationLabel { get; set; } = "";
    [ObservableProperty] public partial bool HasMoreProjects { get; set; }
    [ObservableProperty] public partial bool HasMoreModels { get; set; }
    [ObservableProperty] public partial bool CanShowLessProjects { get; set; }
    [ObservableProperty] public partial bool CanShowLessModels { get; set; }
    [ObservableProperty] public partial string ShowMoreProjectsLabel { get; set; } = "";
    [ObservableProperty] public partial string ShowMoreModelsLabel { get; set; } = "";

    public string ShowLessLabel => L("Usage_ShowLess");

    public DateOnly UsageWindowFrom => _usageWindowFrom;
    public DateOnly UsageWindowThrough => _usageWindowThrough;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshCommand))]
    public partial bool IsRefreshing { get; set; }

    [ObservableProperty] public partial double RefreshProgressFraction { get; set; }
    [ObservableProperty] public partial string RefreshProgressLabel { get; set; } = string.Empty;
    [ObservableProperty] public partial string RefreshProgressDetail { get; set; } = string.Empty;

    // True while the bar has no meaningful fraction to fill: the local pre-fetch
    // phases (account discovery, telemetry scan) animate indeterminately with a
    // stage label; the per-provider network fetch flips this to determinate.
    [ObservableProperty] public partial bool RefreshIsIndeterminate { get; set; }

    public async Task InitializeAsync()
    {
        if (_initialized) return;
        _initialized = true;
        await LoadAndApplyAsync(false).ConfigureAwait(true);
        await LoadAndApplyAsync(true).ConfigureAwait(true);
    }

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private Task RefreshAsync() => LoadAndApplyAsync(true);
    private bool CanRefresh() => !IsRefreshing;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RefreshModelCatalogCommand))]
    public partial bool IsRefreshingModelCatalog { get; set; }

    [ObservableProperty] public partial string ModelCatalogStatusLabel { get; set; } = L("Catalog_Checking");
    [ObservableProperty] public partial string ModelCatalogDetail { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModelCatalogSchedule { get; set; } = string.Empty;
    [ObservableProperty] public partial string ModelCatalogError { get; set; } = string.Empty;
    [ObservableProperty] public partial bool ModelCatalogHasError { get; set; }

    /// <summary>
    /// Reads catalog state without touching the network, so opening Settings
    /// never triggers a fetch.
    /// </summary>
    public async Task LoadModelCatalogStatusAsync()
    {
        try
        {
            ApplyCatalogStatus(await _dataSource.GetModelCatalogStatusAsync(_lifetime.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException)
        {
        }
    }

    [RelayCommand(CanExecute = nameof(CanRefreshModelCatalog))]
    private async Task RefreshModelCatalogAsync()
    {
        IsRefreshingModelCatalog = true;
        try
        {
            PricingCatalogRefresh result = await _dataSource
                .RefreshModelCatalogAsync(_lifetime.Token).ConfigureAwait(true);
            ApplyCatalogStatus(await _dataSource.GetModelCatalogStatusAsync(_lifetime.Token).ConfigureAwait(true));
            if (result.IsFailure)
            {
                ModelCatalogHasError = true;
                ModelCatalogError = result.Error ?? L("Catalog_RefreshFailed");
            }
            else
            {
                // A successful refetch may have changed prices, so re-project
                // the cached data rather than leaving stale cost columns.
                await LoadAndApplyAsync(false).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsRefreshingModelCatalog = false;
        }
    }

    private bool CanRefreshModelCatalog() => !IsRefreshingModelCatalog;

    private void ApplyCatalogStatus(ModelCatalogStatus status)
    {
        ModelCatalogStatusLabel = status.IsAvailable ? L("Catalog_Loaded") : L("Catalog_Unavailable");
        ModelCatalogDetail = status.IsAvailable
            ? F("Catalog_Detail",
                status.ModelCount.ToString("N0", CultureInfo.CurrentCulture),
                status.FetchedAt?.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture) ?? "—",
                status.ShortHash)
            : L("Catalog_NoCache");
        ModelCatalogSchedule = status.NextDue is { } due
            ? F("Catalog_NextDue", due.ToLocalTime().ToString("d MMM HH:mm", CultureInfo.CurrentCulture))
            : L("Catalog_NextDueUnknown");
        ModelCatalogHasError = status.HasError;
        ModelCatalogError = status.LastError ?? string.Empty;
    }

    /// <summary>
    /// Re-projects cached data without touching the network so presentation-only
    /// preferences (like provider visibility) apply immediately. UI thread only.
    /// </summary>
    public Task ReapplyFromCacheAsync() => LoadAndApplyAsync(false);

    /// <summary>
    /// Scheduler entry point; must be invoked on the UI thread. Returns false
    /// when a refresh was already in flight and nothing was done.
    /// </summary>
    public async Task<bool> RefreshFromSchedulerAsync()
    {
        if (IsRefreshing) return false;
        await LoadAndApplyAsync(true).ConfigureAwait(true);
        return true;
    }

    private async Task LoadAndApplyAsync(bool forceRefresh)
    {
        IsRefreshing = true;
        StatusMessage = forceRefresh ? L("Dashboard_RefreshingProviders") : L("Dashboard_ReadingProviderData");
        RefreshProgressFraction = 0;
        // Show an immediate, labelled indeterminate bar; the first stage report
        // (discovery) arrives almost at once and refines the label.
        RefreshIsIndeterminate = true;
        RefreshProgressLabel = forceRefresh ? L("Data_RefreshStageDiscovering") : string.Empty;
        RefreshProgressDetail = string.Empty;
        try
        {
            var progress = new Progress<RefreshProgress>(OnRefreshProgressReported);
            // Progress<T> posts to the captured UI context, so an early
            // projection lands on the same thread Apply requires. The data
            // source reports at most one, minutes before it returns.
            var interim = new Progress<DashboardData>(Apply);
            Apply(await _dataSource.LoadAsync(forceRefresh, progress, interim, _lifetime.Token).ConfigureAwait(true));
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception) { StatusMessage = L("Dashboard_RefreshFailed"); }
        finally
        {
            IsRefreshing = false;
            RefreshIsIndeterminate = false;
            RefreshProgressFraction = 0;
            RefreshProgressLabel = string.Empty;
            RefreshProgressDetail = string.Empty;
        }
    }

    // The provider parallel-batches its fetches, so progress arrives on the
    // captured UI SyncContext as each account finishes. The fraction lags the
    // label by one tick on purpose (we report "starting provider X" before
    // its result is in), which keeps the bar honest about how much is left.
    private void OnRefreshProgressReported(RefreshProgress update)
    {
        bool determinate = update.Total > 0;
        RefreshIsIndeterminate = !determinate;
        RefreshProgressFraction = determinate ? (double)update.Completed / update.Total : 0;
        RefreshProgressLabel = determinate
            ? F("Data_RefreshProgressLabel", update.Completed, update.Total)
            : StageLabel(update.Stage);
        RefreshProgressDetail = string.IsNullOrEmpty(update.CurrentProvider)
            ? string.Empty
            : F("Data_RefreshProgressDetail", update.CurrentProvider);
    }

    private static string StageLabel(RefreshStage stage) => stage switch
    {
        RefreshStage.ScanningHistory => L("Data_RefreshStageScanning"),
        _ => L("Data_RefreshStageDiscovering"),
    };

    private void Apply(DashboardData data)
    {
        TotalTokens = data.TotalTokens;
        ApiEquivalentCost = data.ApiEquivalentCost;
        Coverage = data.Coverage;
        PlanValueLabel = data.PlanValueLabel;
        PlanValueDetail = data.PlanValueDetail;
        CacheSavingsLabel = data.CacheSavingsLabel;
        WeekDeltaLabel = data.WeekDeltaLabel;
        CatalogCaption = data.CatalogCaption;
        LastUpdated = data.LastUpdated;
        StatusMessage = data.StatusMessage;
        UsageTotal = data.UsageTotal;
        InputTokens = data.InputTokens;
        OutputTokens = data.OutputTokens;
        CacheReadTokens = data.CacheReadTokens;
        CacheWriteTokens = data.CacheWriteTokens;
        ReasoningTokens = data.ReasoningTokens;
        ReportedServiceCost = data.ReportedServiceCost;
        PricingCatalogStatus = data.PricingCatalogStatus;
        PricingCatalogDetail = data.PricingCatalogDetail;
        ScannerStatus = data.ScannerStatus;
        ScannerDetail = data.ScannerDetail;
        DatabaseStatus = data.DatabaseStatus;
        DatabaseDetail = data.DatabaseDetail;
        Replace(ResetHorizon, data.ResetHorizon);
        Replace(Providers, data.Providers);
        Replace(ProviderUsage, data.ProviderUsage);
        Replace(HarnessUsage, data.HarnessUsage);
        Replace(HeatmapCells, data.HeatmapCells);
        Replace(ResetCycles, data.ResetCycles);
        Replace(Connections, data.Connections);
        Replace(RecentAttempts, data.RecentAttempts);
        _usageHistory = data.UsageHistory;
        _usageModelSlices = data.UsageModelSlices;
        _usageAnalyticsRecords = data.UsageAnalyticsRecords;
        _projectUsageSlices = data.ProjectUsageSlices;
        _allModelRows = data.ModelRows;
        RebuildModelRows();
        if (_usageWindowThrough == default)
        {
            SetUsageWindow(30);
        }
        else
        {
            RebuildUsageWindow();
        }
        UsageAnalyticsDataChanged?.Invoke(this, EventArgs.Empty);
    }

    public UsageAnalyticsResult QueryUsage(UsageAnalyticsQuery query) =>
        UsageAnalyticsQueryEngine.Run(_usageAnalyticsRecords, query);

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values) target.Add(value);
    }

    private void OnDisplayPreferenceChanged(DashboardDisplayOptions options)
    {
        ProviderCardViewModel[] refreshed = Providers
            .Select(provider => provider with
            {
                AllMeters = provider.AllMeters.Select(meter => meter with { }).ToArray()
            })
            .ToArray();
        Replace(Providers, refreshed);
    }

    private void OnEmailPrivacyChanged(bool _)
    {
        // Re-materializing the items regenerates their containers, so the
        // DisplayAccount bindings re-read the preference.
        Replace(Providers, Providers.ToArray());
        Replace(ResetCycles, ResetCycles.ToArray());
        Replace(Connections, Connections.ToArray());
    }

    public void SetUsageWindow(int days)
    {
        days = Math.Clamp(days, 1, 365);
        _visibleProjectCount = PageSize;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        _usageWindowThrough = today;
        _usageWindowFrom = today.AddDays(1 - days);
        UsageWindowTitle = F("UsageWindow_LastDays", days);
        RebuildUsageWindow();
    }

    public void SetCustomUsageWindow(DateOnly from, DateOnly through)
    {
        _visibleProjectCount = PageSize;
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        UsageWindowRange range = UsageWindowRange.NormalizeCustom(from, through, today);
        from = range.From;
        through = range.Through;
        _usageWindowFrom = from;
        _usageWindowThrough = through;
        UsageWindowTitle = $"{from.ToString("d", CultureInfo.CurrentCulture)} – {through.ToString("d", CultureInfo.CurrentCulture)}";
        RebuildUsageWindow();
    }

    public void SetProjectSort(string? sort)
    {
        _projectSort = sort is "cost" or "project" ? sort : "tokens";
        _visibleProjectCount = PageSize;
        RebuildProjects();
    }

    private void RebuildUsageWindow()
    {
        if (_usageWindowThrough == default)
        {
            return;
        }

        int days = _usageWindowThrough.DayNumber - _usageWindowFrom.DayNumber + 1;
        UsageHistoryDayViewModel[] current = _usageHistory
            .Where(day => day.Day >= _usageWindowFrom && day.Day <= _usageWindowThrough)
            .ToArray();
        long tokens = current.Sum(day => day.Tokens);
        decimal? cost = SumCost(current);
        UsageWindowSummary = F("UsageWindow_Summary", FormatTokens(tokens), FormatUsd(cost));
        PeriodDeltaLabel = BuildComparison(_usageWindowThrough, days, L("UsageWindow_SelectedPeriod"));
        WeekDeltaLabel = BuildComparison(_usageWindowThrough, 7, F("UsageWindow_Days", 7));
        ThirtyDayDeltaLabel = BuildComparison(_usageWindowThrough, 30, F("UsageWindow_Days", 30));
        NinetyDayDeltaLabel = BuildComparison(_usageWindowThrough, 90, F("UsageWindow_Days", 90));
        Replace(UsageDays, BuildTrendPoints(_usageWindowFrom, _usageWindowThrough));
        RebuildProjects();
    }

    private IReadOnlyList<UsageDayViewModel> BuildTrendPoints(DateOnly from, DateOnly through)
    {
        int days = through.DayNumber - from.DayNumber + 1;
        int bucketDays = days switch
        {
            <= 14 => 1,
            <= 45 => 3,
            <= 120 => 7,
            _ => 14
        };
        var history = _usageHistory.ToDictionary(day => day.Day);
        var buckets = new List<(DateOnly From, DateOnly Through, long Tokens, decimal? Cost)>();
        for (DateOnly start = from; start <= through; start = start.AddDays(bucketDays))
        {
            DateOnly end = start.AddDays(bucketDays - 1);
            if (end > through) end = through;
            long tokens = 0;
            decimal cost = 0m;
            bool hasCost = false;
            for (DateOnly day = start; day <= end; day = day.AddDays(1))
            {
                if (!history.TryGetValue(day, out UsageHistoryDayViewModel? item)) continue;
                tokens += item.Tokens;
                if (item.ApiEquivalentCostUsd.HasValue)
                {
                    cost += item.ApiEquivalentCostUsd.Value;
                    hasCost = true;
                }
            }
            buckets.Add((start, end, tokens, hasCost ? cost : null));
        }

        long maxTokens = Math.Max(1, buckets.Max(bucket => bucket.Tokens));
        decimal maxCost = Math.Max(0.000001m, buckets.Where(bucket => bucket.Cost.HasValue).Select(bucket => bucket.Cost!.Value).DefaultIfEmpty().Max());
        return buckets.Select(bucket =>
        {
            string label = bucketDays == 1
                ? bucket.From.ToString("ddd", CultureInfo.CurrentCulture).ToUpper(CultureInfo.CurrentCulture)
                : bucket.From.ToString("dd MMM", CultureInfo.CurrentCulture);
            string range = bucket.From == bucket.Through
                ? bucket.From.ToString("D", CultureInfo.CurrentCulture)
                : $"{bucket.From.ToString("d", CultureInfo.CurrentCulture)} – {bucket.Through.ToString("d", CultureInfo.CurrentCulture)}";
            return new UsageDayViewModel(
                label,
                bucket.Tokens == 0 ? 4 : 24 + 116.0 * bucket.Tokens / maxTokens,
                FormatTokens(bucket.Tokens),
                BuildTrendBreakdown(bucket.From, bucket.Through, range, bucket.Tokens, bucket.Cost),
                bucket.Cost is null or 0m ? 4 : 24 + 116.0 * (double)(bucket.Cost.Value / maxCost),
                FormatUsd(bucket.Cost));
        }).ToArray();
    }

    private string BuildTrendBreakdown(DateOnly from, DateOnly through, string range, long tokens, decimal? cost)
    {
        string[] models = _usageModelSlices
            .Where(slice => slice.Day >= from && slice.Day <= through)
            .GroupBy(slice => slice.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => new { Model = group.Key, Tokens = group.Sum(slice => slice.Tokens) })
            .OrderByDescending(group => group.Tokens)
            .ThenBy(group => group.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => F("UsageWindow_ModelLine", group.Model, FormatTokens(group.Tokens)))
            .ToArray();
        List<string> lines = models.Take(3).ToList();
        long others = _usageModelSlices
            .Where(slice => slice.Day >= from && slice.Day <= through)
            .GroupBy(slice => slice.Model, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.Sum(slice => slice.Tokens))
            .OrderByDescending(value => value)
            .Skip(3)
            .Sum();
        if (others > 0)
        {
            lines.Add(F("Data_Others", FormatTokens(others)));
        }
        string summary = F("UsageWindow_Breakdown", range, FormatTokens(tokens), FormatUsd(cost));
        return lines.Count == 0 ? summary : summary + Environment.NewLine + string.Join(Environment.NewLine, lines);
    }

    private void RebuildProjects()
    {
        ProjectUsageViewModel[] projects = _projectUsageSlices
            .Where(slice => slice.Day >= _usageWindowFrom && slice.Day <= _usageWindowThrough)
            .GroupBy(slice => slice.ProjectKey, StringComparer.Ordinal)
            .Select(group =>
            {
                ProjectUsageSliceViewModel first = group.First();
                long tokens = group.Sum(slice => slice.Tokens);
                decimal? cost = group.Any(slice => slice.ApiEquivalentCostUsd.HasValue)
                    ? group.Where(slice => slice.ApiEquivalentCostUsd.HasValue).Sum(slice => slice.ApiEquivalentCostUsd.GetValueOrDefault())
                    : null;
                string name = ProjectName(first.ProjectPath);
                bool worktree = !string.IsNullOrWhiteSpace(first.RepositoryRootPath)
                    && !string.Equals(first.RepositoryRootPath, first.ProjectPath, StringComparison.OrdinalIgnoreCase);
                if (worktree) name += L("UsageWindow_WorktreeSuffix");
                return new ProjectUsageViewModel(
                    first.ProjectKey,
                    name,
                    first.ProjectPath,
                    first.RepositoryRootPath ?? "-",
                    FormatTokens(tokens),
                    FormatUsd(cost),
                    tokens,
                    cost);
            })
            .Where(project => project.RawTokens > 0)
            .ToArray();

        IEnumerable<ProjectUsageViewModel> sorted = _projectSort switch
        {
            "cost" => projects.OrderByDescending(project => project.RawCost ?? -1m).ThenByDescending(project => project.RawTokens),
            "project" => projects.OrderBy(project => project.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => projects.OrderByDescending(project => project.RawTokens).ThenByDescending(project => project.RawCost ?? -1m)
        };
        _allProjectRows = sorted.ToArray();
        Replace(ProjectUsage, _allProjectRows.Take(_visibleProjectCount));
        UpdateProjectPaging();

        long historyTokens = _usageHistory
            .Where(day => day.Day >= _usageWindowFrom && day.Day <= _usageWindowThrough)
            .Sum(day => day.Tokens);
        long projectTokens = projects.Sum(project => project.RawTokens);
        ProjectReconciliationLabel = historyTokens == projectTokens
            ? F("UsageWindow_ProjectReconciled", FormatTokens(historyTokens))
            : F("UsageWindow_ProjectMismatch", FormatTokens(projectTokens), FormatTokens(historyTokens));
    }

    public void ShowMoreProjects()
    {
        _visibleProjectCount = Math.Min(_allProjectRows.Count, _visibleProjectCount + PageSize);
        Replace(ProjectUsage, _allProjectRows.Take(_visibleProjectCount));
        UpdateProjectPaging();
    }

    public void ShowLessProjects()
    {
        _visibleProjectCount = PageSize;
        Replace(ProjectUsage, _allProjectRows.Take(_visibleProjectCount));
        UpdateProjectPaging();
    }

    public void ShowMoreModels()
    {
        _visibleModelCount = Math.Min(_allModelRows.Count, _visibleModelCount + PageSize);
        RebuildModelRows();
    }

    public void ShowLessModels()
    {
        _visibleModelCount = PageSize;
        RebuildModelRows();
    }

    private void RebuildModelRows()
    {
        Replace(ModelRows, _allModelRows.Take(_visibleModelCount));
        int remaining = Math.Max(0, _allModelRows.Count - ModelRows.Count);
        HasMoreModels = remaining > 0;
        CanShowLessModels = ModelRows.Count > PageSize;
        ShowMoreModelsLabel = F("Usage_ShowMore", remaining);
    }

    private void UpdateProjectPaging()
    {
        int remaining = Math.Max(0, _allProjectRows.Count - ProjectUsage.Count);
        HasMoreProjects = remaining > 0;
        CanShowLessProjects = ProjectUsage.Count > PageSize;
        ShowMoreProjectsLabel = F("Usage_ShowMore", remaining);
    }

    private string BuildComparison(DateOnly through, int days, string label)
    {
        DateOnly currentFrom = through.AddDays(1 - days);
        DateOnly previousThrough = currentFrom.AddDays(-1);
        DateOnly previousFrom = previousThrough.AddDays(1 - days);
        UsageHistoryDayViewModel[] current = _usageHistory.Where(day => day.Day >= currentFrom && day.Day <= through).ToArray();
        UsageHistoryDayViewModel[] previous = _usageHistory.Where(day => day.Day >= previousFrom && day.Day <= previousThrough).ToArray();
        long currentTokens = current.Sum(day => day.Tokens);
        long previousTokens = previous.Sum(day => day.Tokens);
        return F("UsageWindow_Comparison", label, Delta(currentTokens, previousTokens), Delta(SumCost(current), SumCost(previous)));
    }

    private static string Delta(long current, long previous) => previous == 0
        ? current == 0 ? L("Delta_NoChange") : L("Delta_New")
        : $"{(100.0 * (current - previous) / previous):+0.#;-0.#;0}%";

    private static string Delta(decimal? current, decimal? previous)
    {
        if (!current.HasValue || !previous.HasValue) return L("Delta_Unavailable");
        if (previous.Value == 0m) return current.Value == 0m ? L("Delta_NoChange") : L("Delta_New");
        return $"{(100m * (current.Value - previous.Value) / previous.Value):+0.#;-0.#;0}%";
    }

    private static decimal? SumCost(IEnumerable<UsageHistoryDayViewModel> days)
    {
        UsageHistoryDayViewModel[] values = days.ToArray();
        return values.Any(day => day.ApiEquivalentCostUsd.HasValue)
            ? values.Where(day => day.ApiEquivalentCostUsd.HasValue).Sum(day => day.ApiEquivalentCostUsd.GetValueOrDefault())
            : null;
    }

    private static string ProjectName(string path)
    {
        if (string.Equals(path, "Unknown", StringComparison.OrdinalIgnoreCase)) return L("Common_Unknown");
        string trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string FormatTokens(long value) => Math.Abs(value) switch
    {
        >= 1_000_000_000 => $"{value / 1_000_000_000d:0.##}B",
        >= 1_000_000 => $"{value / 1_000_000d:0.##}M",
        >= 1_000 => $"{value / 1_000d:0.#}K",
        _ => value.ToString("N0", CultureInfo.CurrentCulture)
    };

    private static string FormatUsd(decimal? value) => value.HasValue
        ? value.Value.ToString("$0.00", CultureInfo.InvariantCulture)
        : "-";

    private static string L(string key) => LocalizationService.GetString(key);
    private static string F(string key, params object[] values) =>
        string.Format(CultureInfo.CurrentCulture, L(key), values);

    public void Dispose()
    {
        DashboardDisplayPreference.Changed -= OnDisplayPreferenceChanged;
        EmailPrivacyPreference.Changed -= OnEmailPrivacyChanged;
        _lifetime.Cancel();
        _lifetime.Dispose();
    }
}
