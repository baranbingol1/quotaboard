// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Domain;
using AiLimits.Presentation.WinUI.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AiLimits.Presentation.WinUI.ViewModels;

/// <summary>
/// Provider-level health, distinct enough that one broken provider is never
/// presented as a generic dashboard-wide failure.
/// </summary>
public enum CardStatusKind
{
    Live,
    Stale,
    Offline,
    RateLimited,
    SignInRequired,
    NoQuota,
    Error,
}

public sealed record ProviderCardViewModel(
    string Id,
    string Name,
    string Account,
    string Accent,
    string HealthLabel,
    string CoverageLabel,
    IReadOnlyList<MeterViewModel> AllMeters,
    string BalanceLabel = "",
    string IncidentSummary = "",
    string IncidentIndicator = "",
    CardStatusKind StatusKind = CardStatusKind.Live,
    string AuthSourceLabel = ""
)
{
    public string DisplayAccount => EmailPrivacyPreference.Apply(Account);

    public string StatusShortLabel =>
        LocalizationService.GetString(
            StatusKind switch
            {
                CardStatusKind.Live => "Card_StatusLive",
                CardStatusKind.Stale => "Card_StatusStale",
                CardStatusKind.Offline => "Card_StatusOffline",
                CardStatusKind.RateLimited => "Card_StatusRateLimited",
                CardStatusKind.SignInRequired => "Card_StatusSignIn",
                CardStatusKind.NoQuota => "Card_StatusNoQuota",
                _ => "Card_StatusError",
            }
        );

    public string StatusTooltip =>
        string.Join(
            Environment.NewLine,
            new[] { HealthLabel, AuthSourceLabel, CoverageLabel }
                .Where(part => !string.IsNullOrWhiteSpace(part))
                .Distinct()
        );

    public IReadOnlyList<MeterViewModel> DisplayMeters =>
        AllMeters
            .Where(meter =>
                !DashboardDisplayPreference.Current.HideZeroOrNotApplicableMeters || !meter.IsZeroOrNotApplicable
            )
            .ToArray();
    public IReadOnlyList<MeterViewModel> VisibleMeters => DisplayMeters.Take(4).ToArray();
    public int AdditionalCount => Math.Max(0, DisplayMeters.Count - VisibleMeters.Count);
    public string MoreLabel =>
        AdditionalCount > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.GetString("ProviderCard_MoreLimits"),
                AdditionalCount
            )
            : LocalizationService.GetString("ProviderCard_ViewProvider");
    public bool HasIncident => !string.IsNullOrWhiteSpace(IncidentSummary);
    public Visibility IncidentVisibility => HasIncident ? Visibility.Visible : Visibility.Collapsed;
    public string IncidentBadge =>
        HasIncident ? LocalizationService.GetString("ProviderCard_ServiceIncident") : string.Empty;
}

public sealed record MeterViewModel(
    string Key,
    string DisplayName,
    double UsedPercent,
    string UsedLabel,
    string RemainingLabel,
    string ResetLabel,
    DateTimeOffset? ResetsAt,
    MeterStatus Status,
    bool IsNew = false,
    bool IsStale = false,
    string PaceLabel = ""
)
{
    public Visibility PaceVisibility => string.IsNullOrEmpty(PaceLabel) ? Visibility.Collapsed : Visibility.Visible;

    // Producers clamp upstream, but a NaN/Infinity that slips through must
    // degrade to "not applicable" instead of binding NaN to a ProgressBar.
    private double SafePercent => double.IsFinite(UsedPercent) ? Math.Clamp(UsedPercent, 0, 100) : 0;
    public bool IsZeroOrNotApplicable => SafePercent <= 0 || Status is MeterStatus.Unknown or MeterStatus.Unavailable;
    public double DisplayPercent =>
        DashboardDisplayPreference.Current.ShowUsageAsRemaining ? Math.Max(0, 100 - SafePercent) : SafePercent;
    public string PercentLabel => $"{DisplayPercent:0.#}%";
    public string DisplayUsageLabel =>
        DashboardDisplayPreference.Current.ShowUsageAsRemaining ? RemainingLabel : UsedLabel;
    public string DisplayResetLabel =>
        DashboardDisplayPreference.Current.ShowResetTimeAsAbsolute && ResetsAt.HasValue
            ? string.Format(
                CultureInfo.CurrentCulture,
                LocalizationService.GetString("Meter_ResetsAt"),
                ResetsAt.Value.ToLocalTime()
            )
            : ResetLabel;
    public string StatusLabel =>
        IsStale
            ? LocalizationService.GetString("Meter_StatusStale")
            : Status switch
            {
                MeterStatus.Exhausted => LocalizationService.GetString("Meter_StatusExhausted"),
                MeterStatus.Critical => LocalizationService.GetString("Meter_StatusCritical"),
                MeterStatus.Approaching => LocalizationService.GetString("Meter_StatusWatch"),
                _ => LocalizationService.GetString("Meter_StatusOnTrack"),
            };
    public Visibility NewVisibility => IsNew ? Visibility.Visible : Visibility.Collapsed;
    public string AutomationName => $"{DisplayName}, {DisplayUsageLabel}, {DisplayResetLabel}, {StatusLabel}";
}

public sealed record ResetHorizonItemViewModel(
    string Provider,
    string Account,
    string Meter,
    string Countdown,
    string ResetTime,
    string Accent,
    DateTimeOffset ResetsAt
)
{
    public string AutomationName =>
        string.Format(
            CultureInfo.CurrentCulture,
            LocalizationService.GetString("Reset_AutomationName"),
            Provider,
            Account,
            Meter,
            Countdown,
            ResetTime
        );
}

public sealed record UsageDayViewModel(
    string Label,
    double Height,
    string Tokens,
    string Breakdown = "",
    double CostHeight = 4,
    string Cost = "-"
);

public sealed record UsageChartBarViewModel(
    string Label,
    string RangeLabel,
    string Tokens,
    string AutomationName,
    double Height,
    double ItemWidth,
    double BarWidth,
    IReadOnlyList<UsageChartSegmentViewModel> Segments,
    IReadOnlyList<UsageChartHoverDetailViewModel> HoverDetails
);

public sealed record UsageChartSegmentViewModel(
    string Key,
    string Label,
    string Tokens,
    long RawTokens,
    double Height,
    Brush Brush
);

public sealed record UsageChartHoverDetailViewModel(string Label, string ValueLabel, Brush Brush);

public sealed record UsageChartLegendViewModel(string Key, string Label, string Tokens, string ShareLabel, Brush Brush);

public sealed record UsageBreakdownRowViewModel(
    string Key,
    string Label,
    string Tokens,
    string InputTokens,
    string OutputTokens,
    string CacheReadTokens,
    string CacheWriteTokens,
    string ReasoningTokens,
    string ApiEquivalent,
    string ShareLabel,
    double SharePercent,
    long RawTokens
);

/// <summary>
/// One row of the Model mix card: totals plus a sparkline on the same buckets
/// as the main chart, so "which models did this harness run, and when" is
/// answerable without re-pointing the breakdown selector.
/// </summary>
public sealed record UsageModelTrendRowViewModel(
    string Key,
    string Label,
    string Tokens,
    string ShareLabel,
    string Cost,
    string ActivityLabel,
    Brush Accent,
    IReadOnlyList<UsageSparkBarViewModel> Spark
)
{
    public string AutomationName => $"{Label}, {Tokens}, {ActivityLabel}";
}

public sealed record UsageSparkBarViewModel(
    double Width,
    double Height,
    double Opacity,
    string Tooltip,
    string AutomationName,
    Brush Brush
);

public sealed record UsageHistoryDayViewModel(DateOnly Day, long Tokens, decimal? ApiEquivalentCostUsd);

public sealed record UsageModelSliceViewModel(DateOnly Day, string Model, long Tokens);

public sealed record ProjectUsageSliceViewModel(
    DateOnly Day,
    string ProjectKey,
    string ProjectPath,
    string? RepositoryRootPath,
    long Tokens,
    decimal? ApiEquivalentCostUsd
);

public sealed record ProjectUsageViewModel(
    string ProjectKey,
    string Name,
    string Path,
    string Repository,
    string Tokens,
    string Cost,
    long RawTokens,
    decimal? RawCost
);

public sealed record HeatmapCellViewModel(string Tooltip, double Intensity, bool HasData)
{
    public double CellOpacity => HasData ? 0.18 + 0.82 * Intensity : 1.0;
}

public sealed record ResetCycleViewModel(string Provider, string Account, string Title, string Detail, string Accent)
{
    public string DisplayAccount => EmailPrivacyPreference.Apply(Account);
}

public sealed record ProviderUsageViewModel(
    string Provider,
    string Tokens,
    string ApiEquivalent,
    string PricingCoverage,
    string Models,
    string Harnesses,
    string Accent,
    double SharePercent = 0
)
{
    public string ShareLabel => $"{SharePercent:0.#}%";
}

public sealed record ModelUsageRowViewModel(
    string Source,
    string Model,
    string AuthProvider,
    string Tokens,
    string Cost,
    string PricingStatus,
    // Machine-readable identity for Settings (never parse the localized
    // PricingStatus): which service the row belongs to and whether any price
    // (catalog or manual) applied.
    string ServiceId = "",
    bool IsPriced = false
);

public sealed class ProviderConnectionViewModel
{
    public ProviderConnectionViewModel() { }

    public ProviderConnectionViewModel(
        string name,
        string account,
        string authSource,
        string health,
        string coverage,
        string accent,
        string actionLabel,
        string actionKind = "",
        string actionTarget = "",
        string id = "",
        bool isConnected = false
    )
    {
        Id = id;
        IsConnected = isConnected;
        Name = name;
        Account = account;
        AuthSource = authSource;
        Health = health;
        Coverage = coverage;
        Accent = accent;
        ActionLabel = actionLabel;
        ActionKind = actionKind;
        ActionTarget = actionTarget;
    }

    public string Id { get; set; } = string.Empty;
    public bool IsConnected { get; set; }
    public CardStatusKind StatusKind { get; set; } = CardStatusKind.Offline;

    /// <summary>Subscription plan reported by the provider ("Pro", "Max"…), empty when unknown.</summary>
    public string DetectedPlan { get; set; } = string.Empty;

    /// <summary>Standard monthly price of the detected plan in USD; NaN when unknown or ambiguous.</summary>
    public double SuggestedMonthlyCost { get; set; } = double.NaN;
    public string Name { get; set; } = string.Empty;
    public string Account { get; set; } = string.Empty;
    public string DisplayAccount => EmailPrivacyPreference.Apply(Account);
    public string AuthSource { get; set; } = string.Empty;
    public string Health { get; set; } = string.Empty;
    public string Coverage { get; set; } = string.Empty;
    public string Accent { get; set; } = string.Empty;
    public string ActionLabel { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string ActionTarget { get; set; } = string.Empty;
}

public sealed record FetchAttemptViewModel(
    string Provider,
    string Strategy,
    string Duration,
    string Status,
    string Meaning,
    CardStatusKind StatusKind
);
