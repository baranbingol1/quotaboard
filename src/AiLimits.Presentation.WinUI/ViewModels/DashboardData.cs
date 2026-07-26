// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;
using AiLimits.Application.Usage;

namespace AiLimits.Presentation.WinUI.ViewModels;

public interface IDashboardDataSource
{
    Task<DashboardData> LoadAsync(bool forceRefresh, IProgress<RefreshProgress>? progress, CancellationToken cancellationToken);

    /// <summary>Current model-catalog state for the Settings card; reads the cache only.</summary>
    Task<ModelCatalogStatus> GetModelCatalogStatusAsync(CancellationToken cancellationToken);

    /// <summary>Refetches the model catalog on demand, ignoring the twice-daily schedule.</summary>
    Task<PricingCatalogRefresh> RefreshModelCatalogAsync(CancellationToken cancellationToken);
}

public sealed record DashboardData(
    string TotalTokens,
    string ApiEquivalentCost,
    string Coverage,
    string CatalogCaption,
    string LastUpdated,
    string StatusMessage,
    string PlanValueLabel,
    string PlanValueDetail,
    string CacheSavingsLabel,
    string WeekDeltaLabel,
    IReadOnlyList<ResetHorizonItemViewModel> ResetHorizon,
    IReadOnlyList<ProviderCardViewModel> Providers,
    IReadOnlyList<ProviderUsageViewModel> ProviderUsage,
    IReadOnlyList<ProviderUsageViewModel> HarnessUsage,
    IReadOnlyList<UsageDayViewModel> UsageDays,
    IReadOnlyList<HeatmapCellViewModel> HeatmapCells,
    IReadOnlyList<UsageHistoryDayViewModel> UsageHistory,
    IReadOnlyList<UsageModelSliceViewModel> UsageModelSlices,
    IReadOnlyList<UsageAnalyticsRecord> UsageAnalyticsRecords,
    IReadOnlyList<ProjectUsageSliceViewModel> ProjectUsageSlices,
    IReadOnlyList<ResetCycleViewModel> ResetCycles,
    IReadOnlyList<ModelUsageRowViewModel> ModelRows,
    IReadOnlyList<ProviderConnectionViewModel> Connections,
    string UsageTotal,
    string InputTokens,
    string OutputTokens,
    string CacheReadTokens,
    string CacheWriteTokens,
    string ReasoningTokens,
    string ReportedServiceCost,
    string PricingCatalogStatus,
    string PricingCatalogDetail,
    string ScannerStatus,
    string ScannerDetail,
    string DatabaseStatus,
    string DatabaseDetail,
    IReadOnlyList<FetchAttemptViewModel> RecentAttempts);
