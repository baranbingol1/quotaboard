// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Domain;

namespace AiLimits.Application.Alerts;

public enum AlertKind
{
    UsageThreshold,
    ResetReminder
}

public sealed record AlertCandidate(
    AlertKind Kind,
    AccountKey Account,
    MeterKey Meter,
    string DeduplicationKey,
    string Title,
    string Message,
    DateTimeOffset? ResetCycle);

public sealed record AlertPolicy(
    IReadOnlyList<double> UsageThresholds,
    TimeSpan? ResetReminder,
    bool Enabled)
{
    public static AlertPolicy Suggested { get; } = new(
        [80d, 95d],
        TimeSpan.FromMinutes(30),
        Enabled: true);
}

public interface IAlertTextProvider
{
    string UsageTitle(string providerId, string meterKey, string meterName, double threshold);
    string UsageMessage(double usedPercent, DateTimeOffset? resetsAt);
    string ResetTitle(string providerId, string meterKey, string meterName);
    string ResetMessage(TimeSpan remaining);
}

/// <summary>
/// Turns a provider snapshot into deterministic alert events. The evaluator is
/// intentionally stateless; <see cref="IAlertStateStore"/> owns durable
/// deduplication across refreshes and process restarts.
/// </summary>
public sealed class AlertEvaluator(IAlertTextProvider? textProvider = null)
{
    private readonly IAlertTextProvider _textProvider = textProvider ?? EnglishAlertTextProvider.Instance;

    public IReadOnlyList<AlertCandidate> Evaluate(
        ProviderSnapshot snapshot,
        AlertPolicy policy,
        DateTimeOffset now)
    {
        if (!policy.Enabled)
        {
            return [];
        }

        var candidates = new List<AlertCandidate>();
        double[] thresholds = policy.UsageThresholds
            .Where(threshold => double.IsFinite(threshold) && threshold is > 0 and <= 100)
            .Distinct()
            .Order()
            .ToArray();

        foreach (UsageMeter meter in snapshot.Meters.Where(meter =>
            meter.Status is not (MeterStatus.Stale or MeterStatus.Unknown or MeterStatus.Unavailable)))
        {
            foreach (double threshold in thresholds)
            {
                // A malformed NaN/Infinity reading satisfies no "< threshold"
                // check and would otherwise fire every threshold at once.
                if (meter.UsedPercent is not double usedPercent
                    || !double.IsFinite(usedPercent)
                    || usedPercent < threshold)
                {
                    continue;
                }

                string thresholdText = threshold.ToString("0.##", CultureInfo.InvariantCulture);
                string resetCycle = FormatResetCycle(meter.ResetsAt);
                candidates.Add(new AlertCandidate(
                    AlertKind.UsageThreshold,
                    snapshot.Account,
                    meter.Key,
                    $"{snapshot.Account}|{meter.Key}|usage-{thresholdText}|{resetCycle}",
                    _textProvider.UsageTitle(snapshot.Account.Provider.Value, meter.Key.Value, meter.DisplayName, threshold),
                    _textProvider.UsageMessage(usedPercent, meter.ResetsAt),
                    meter.ResetsAt));
            }

            if (policy.ResetReminder is not TimeSpan reminder
                || meter.ResetsAt is not DateTimeOffset reset
                || reset <= now
                || reset - now > reminder)
            {
                continue;
            }

            string cycle = FormatResetCycle(reset);
            candidates.Add(new AlertCandidate(
                AlertKind.ResetReminder,
                snapshot.Account,
                meter.Key,
                $"{snapshot.Account}|{meter.Key}|reset-reminder|{cycle}",
                _textProvider.ResetTitle(snapshot.Account.Provider.Value, meter.Key.Value, meter.DisplayName),
                _textProvider.ResetMessage(reset - now),
                reset));
        }

        return candidates;
    }

    private static string FormatResetCycle(DateTimeOffset? reset) =>
        reset?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? "open";

    private sealed class EnglishAlertTextProvider : IAlertTextProvider
    {
        public static EnglishAlertTextProvider Instance { get; } = new();

        public string UsageTitle(string providerId, string meterKey, string meterName, double threshold) =>
            $"{meterName} reached {threshold:0}%";

        public string UsageMessage(double usedPercent, DateTimeOffset? resetsAt) =>
            resetsAt is DateTimeOffset reset
                ? $"{usedPercent:0.#}% used · resets {reset.LocalDateTime:g}"
                : $"{usedPercent:0.#}% used";

        public string ResetTitle(string providerId, string meterKey, string meterName) =>
            $"{meterName} resets soon";

        public string ResetMessage(TimeSpan remaining) => $"Reset in {FormatRemaining(remaining)}.";

        private static string FormatRemaining(TimeSpan remaining) =>
            remaining.TotalHours >= 1
                ? $"{(int)remaining.TotalHours}h {remaining.Minutes}m"
                : $"{Math.Max(1, remaining.Minutes)}m";
    }
}
