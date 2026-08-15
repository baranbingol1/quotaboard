// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Presentation.WinUI.ViewModels;

/// <summary>
/// Snapshot of a refresh run: how many provider fetches have completed, how
/// many were scheduled, and which provider is currently in flight. Reported
/// continuously by <see cref="IDashboardDataSource"/> to the UI so the manual
/// refresh button can show real progress instead of graying out for a minute.
/// </summary>
/// <param name="Completed">Providers whose refresh attempt has finished (success or failure).</param>
/// <param name="Total">Providers the data source plans to refresh. Zero means indeterminate.</param>
/// <param name="CurrentProvider">Display name for the provider currently being fetched; empty when idle.</param>
/// <param name="Stage">Which phase of the refresh is running, so the bar can label the local pre-fetch work instead of animating blind.</param>
public readonly record struct RefreshProgress(
    int Completed,
    int Total,
    string CurrentProvider,
    RefreshStage Stage = RefreshStage.Fetching
);

/// <summary>
/// Coarse phase of a refresh run. Only <see cref="Fetching"/> carries a
/// meaningful <see cref="RefreshProgress.Total"/>; the local pre-fetch phases
/// are indeterminate but still worth naming for the user.
/// </summary>
public enum RefreshStage
{
    Fetching,
    DiscoveringAccounts,
    ScanningHistory,
}
