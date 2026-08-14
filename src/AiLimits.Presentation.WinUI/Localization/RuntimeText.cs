// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Domain;
using AiLimits.Presentation.WinUI.ViewModels;

namespace AiLimits.Presentation.WinUI.Localization;

/// <summary>
/// Localizes display text that originates in provider adapters rather than XAML.
/// Provider/model names remain intact while the surrounding UI vocabulary is translated.
/// </summary>
public static class RuntimeText
{
    public static string AccountFallback(string providerId) =>
        providerId.ToLowerInvariant() switch
        {
            "codex" => L("Runtime_AccountCodex"),
            "claude" => L("Runtime_AccountClaude"),
            "opencode" => L("Runtime_AccountOpenCode"),
            "droid" => L("Runtime_AccountDroid"),
            "copilot" => L("Runtime_AccountCopilot"),
            "amp" => L("Runtime_AccountAmp"),
            "cursor" => L("Runtime_AccountCursor"),
            _ => L("Common_Unknown"),
        };

    public static string AuthSource(string providerId, string? source = null)
    {
        string normalizedSource = source?.Trim().ToLowerInvariant() ?? string.Empty;
        string? sourceKey = normalizedSource switch
        {
            "local history + provider auth metadata" => "Runtime_AuthOpenCodeMetadata",
            "local usage database" => "Runtime_AuthOpenCodeDatabase",
            "factory cli session" => "Runtime_AuthDroidSession",
            "optional api key" => "Runtime_AuthDroidApiKey",
            "cursor app session" => "Runtime_AuthCursor",
            _ => null,
        };
        if (sourceKey is not null)
        {
            return L(sourceKey);
        }

        return providerId.ToLowerInvariant() switch
        {
            "codex" => L("Runtime_AuthCodex"),
            "claude" => L("Runtime_AuthClaude"),
            "opencode" => L("Runtime_AuthOpenCode"),
            "droid" => L("Runtime_AuthDroid"),
            "copilot" => L("Runtime_AuthCopilot"),
            "amp" => L("Runtime_AuthAmp"),
            "cursor" => L("Runtime_AuthCursor"),
            _ => L("Common_Unknown"),
        };
    }

    public static string MeterDisplayName(string meterKey, string fallback)
    {
        string name = fallback.Trim();
        string? exactKey = name.ToLowerInvariant() switch
        {
            "primary limit" => "Runtime_MeterPrimaryLimit",
            "secondary limit" => "Runtime_MeterSecondaryLimit",
            "individual limit" => "Runtime_MeterIndividualLimit",
            "weekly limit" => "Runtime_MeterWeeklyLimit",
            "5-hour limit" => "Runtime_MeterFiveHourLimit",
            "monthly limit" => "Runtime_MeterMonthlyLimit",
            "go session budget" => "Runtime_MeterGoSessionBudget",
            "go weekly budget" => "Runtime_MeterGoWeeklyBudget",
            "go monthly budget" => "Runtime_MeterGoMonthlyBudget",
            "standard tokens" => "Runtime_MeterStandardTokens",
            "premium tokens" => "Runtime_MeterPremiumTokens",
            "extra usage balance" => "Runtime_MeterExtraUsageBalance",
            "credits" => "Runtime_MeterCredits",
            "codex extra limit" => "Runtime_MeterCodexExtraLimit",
            _ => null,
        };
        if (exactKey is not null)
        {
            return L(exactKey);
        }

        const string secondaryPrefix = "Secondary ";
        if (name.StartsWith(secondaryPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return F("Runtime_SecondaryMeter", MeterDisplayName(meterKey, name[secondaryPrefix.Length..]));
        }

        foreach (
            (string suffix, string key) in new[]
            {
                (" weekly limit", "Runtime_WeeklyLimitFor"),
                (" 5-hour limit", "Runtime_FiveHourLimitFor"),
                (" monthly limit", "Runtime_MonthlyLimitFor"),
                (" limit", "Runtime_LimitFor"),
                (" tokens", "Runtime_TokensFor"),
            }
        )
        {
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            {
                return F(key, name[..^suffix.Length]);
            }
        }

        return string.IsNullOrWhiteSpace(name) ? meterKey : name;
    }

    public static string FetchFailure(FetchFailureKind kind) =>
        kind switch
        {
            FetchFailureKind.Authentication => L("Runtime_FailureAuthentication"),
            FetchFailureKind.Authorization => L("Runtime_FailureAuthorization"),
            FetchFailureKind.AccountMismatch => L("Runtime_FailureAccountMismatch"),
            FetchFailureKind.RateLimited => L("Runtime_FailureRateLimited"),
            FetchFailureKind.Network => L("Runtime_FailureNetwork"),
            FetchFailureKind.Timeout => L("Runtime_FailureTimeout"),
            FetchFailureKind.MalformedResponse => L("Runtime_FailureMalformed"),
            FetchFailureKind.ProviderChanged => L("Runtime_FailureProviderChanged"),
            FetchFailureKind.Cancelled => L("Runtime_FailureCancelled"),
            FetchFailureKind.Unsupported => L("Runtime_FailureUnsupported"),
            FetchFailureKind.TemporarilyUnavailable => L("Runtime_FailureTemporarilyUnavailable"),
            FetchFailureKind.None => L("Data_Success"),
            _ => L("Runtime_FailureUnknown"),
        };

    // FetchFailure names the outcome; this says what it means for the person
    // reading it. "Not supported" next to a 0 ms duration told nobody whether
    // something was broken, so every outcome now carries a plain sentence.
    public static string FetchFailureMeaning(FetchFailureKind kind) =>
        kind switch
        {
            FetchFailureKind.Authentication => L("Runtime_MeaningAuthentication"),
            FetchFailureKind.Authorization => L("Runtime_MeaningAuthorization"),
            FetchFailureKind.AccountMismatch => L("Runtime_MeaningAccountMismatch"),
            FetchFailureKind.RateLimited => L("Runtime_MeaningRateLimited"),
            FetchFailureKind.Network => L("Runtime_MeaningNetwork"),
            FetchFailureKind.Timeout => L("Runtime_MeaningTimeout"),
            FetchFailureKind.MalformedResponse => L("Runtime_MeaningMalformed"),
            FetchFailureKind.ProviderChanged => L("Runtime_MeaningProviderChanged"),
            FetchFailureKind.Cancelled => L("Runtime_MeaningCancelled"),
            FetchFailureKind.Unsupported => L("Runtime_MeaningUnsupported"),
            FetchFailureKind.TemporarilyUnavailable => L("Runtime_MeaningTemporarilyUnavailable"),
            FetchFailureKind.None => L("Runtime_MeaningSuccess"),
            _ => L("Runtime_MeaningUnknown"),
        };

    // Reuses the card palette so a red dot means the same thing on every page.
    public static CardStatusKind FetchOutcome(FetchFailureKind kind) =>
        kind switch
        {
            FetchFailureKind.None => CardStatusKind.Live,
            FetchFailureKind.Authentication or FetchFailureKind.Authorization => CardStatusKind.SignInRequired,
            FetchFailureKind.RateLimited => CardStatusKind.RateLimited,
            FetchFailureKind.Network or FetchFailureKind.Timeout => CardStatusKind.Offline,
            // Transient, self-recovering: shown as retrying, never as sign-in.
            FetchFailureKind.TemporarilyUnavailable => CardStatusKind.Stale,
            // Neither broken nor answered: this source simply does not apply here.
            FetchFailureKind.Unsupported or FetchFailureKind.Cancelled => CardStatusKind.NoQuota,
            _ => CardStatusKind.Error,
        };

    public static string AuthorizationProvider(string displayName) =>
        displayName.EndsWith("(API key)", StringComparison.Ordinal)
            ? displayName[..^"(API key)".Length] + L("Runtime_ApiKeySuffix")
            : displayName;

    public static string ProviderStatus(string? indicator, string? fallback) =>
        indicator?.Trim().ToLowerInvariant() switch
        {
            "minor" => L("Runtime_StatusMinor"),
            "major" => L("Runtime_StatusMajor"),
            "critical" => L("Runtime_StatusCritical"),
            "maintenance" => L("Runtime_StatusMaintenance"),
            _ when !string.IsNullOrWhiteSpace(fallback) => fallback,
            _ => L("Runtime_StatusUnknown"),
        };

    private static string L(string key) => LocalizationService.GetString(key);

    private static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key), args);
}
