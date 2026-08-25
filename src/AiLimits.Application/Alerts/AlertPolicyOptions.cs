// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;

namespace AiLimits.Application.Alerts;

/// <summary>
/// User-tunable subset of <see cref="AlertPolicy"/>: two usage thresholds and
/// a reset-reminder lead time. Parsing is total — any unreadable or invalid
/// value falls back to the suggested default, so a torn preference file can
/// never silently disable alerts or produce a degenerate threshold pair.
/// </summary>
public sealed record AlertPolicyOptions(
    bool Enabled,
    int WarnThresholdPercent,
    int CriticalThresholdPercent,
    int ResetReminderMinutes
)
{
    public const int DefaultWarnThreshold = 80;
    public const int DefaultCriticalThreshold = 95;
    public const int DefaultResetReminderMinutes = 30;
    public const int MaxResetReminderMinutes = 24 * 60;

    public static AlertPolicyOptions Suggested { get; } =
        new(true, DefaultWarnThreshold, DefaultCriticalThreshold, DefaultResetReminderMinutes);

    /// <summary>
    /// Canonical form: warn in [1,99], critical in [2,100] and strictly above
    /// warn, reminder in [0, MaxResetReminderMinutes] where 0 disables the
    /// reset reminder.
    /// </summary>
    public AlertPolicyOptions Clamped()
    {
        int warn = Math.Clamp(WarnThresholdPercent, 1, 99);
        int critical = Math.Clamp(Math.Max(CriticalThresholdPercent, warn + 1), 2, 100);
        int reminder = Math.Clamp(ResetReminderMinutes, 0, MaxResetReminderMinutes);
        return new AlertPolicyOptions(Enabled, warn, critical, reminder);
    }

    public AlertPolicy ToPolicy()
    {
        AlertPolicyOptions clamped = Clamped();
        return new AlertPolicy(
            [clamped.WarnThresholdPercent, clamped.CriticalThresholdPercent],
            clamped.ResetReminderMinutes > 0 ? TimeSpan.FromMinutes(clamped.ResetReminderMinutes) : null,
            clamped.Enabled
        );
    }

    public string Serialize() => JsonSerializer.Serialize(this, JsonSerializerOptions.Web);

    /// <summary>
    /// Accepts the JSON form written by <see cref="Serialize"/> and the legacy
    /// bare "True"/"False" preference file content. Anything else — including
    /// null, truncated, or wrongly typed input — yields the suggested policy.
    /// </summary>
    public static AlertPolicyOptions Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Suggested;
        }

        string trimmed = text.Trim();
        if (bool.TryParse(trimmed, out bool legacyEnabled))
        {
            return Suggested with { Enabled = legacyEnabled };
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Suggested;
            }

            return new AlertPolicyOptions(
                Bool(document.RootElement, "enabled") ?? Suggested.Enabled,
                Int(document.RootElement, "warnThresholdPercent") ?? DefaultWarnThreshold,
                Int(document.RootElement, "criticalThresholdPercent") ?? DefaultCriticalThreshold,
                Int(document.RootElement, "resetReminderMinutes") ?? DefaultResetReminderMinutes
            ).Clamped();
        }
        catch (JsonException)
        {
            return Suggested;
        }
    }

    private static bool? Bool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out JsonElement value))
        {
            return null;
        }
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static int? Int(JsonElement element, string property) =>
        element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out int parsed)
            ? parsed
            : null;
}
