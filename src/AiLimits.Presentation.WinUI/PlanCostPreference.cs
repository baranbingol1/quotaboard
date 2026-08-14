// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;

namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Per-provider monthly subscription spend in USD, keyed by provider id
/// (claude, codex, droid, ...). Used to relate the API-equivalent estimate to
/// what the plans actually cost. Stored locally next to the theme preference.
/// </summary>
public static class PlanCostPreference
{
    private static readonly string PreferencePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard",
        "plan-costs.json"
    );

    private static readonly string LegacySingleValuePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "QuotaBoard",
        "plan-cost.preference"
    );

    public static IReadOnlyDictionary<string, decimal> LoadAll()
    {
        try
        {
            // The pre-per-provider single total can't be attributed to a provider;
            // drop it so Settings starts clean.
            if (File.Exists(LegacySingleValuePath))
            {
                File.Delete(LegacySingleValuePath);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try
        {
            if (!File.Exists(PreferencePath))
            {
                return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
            }
            var parsed = JsonSerializer.Deserialize<Dictionary<string, decimal>>(File.ReadAllText(PreferencePath));
            return new Dictionary<string, decimal>(
                (parsed ?? []).Where(pair => pair.Value > 0m),
                StringComparer.OrdinalIgnoreCase
            );
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static void SaveFor(string providerId, decimal? value)
    {
        if (string.IsNullOrWhiteSpace(providerId))
        {
            return;
        }
        try
        {
            var costs = new Dictionary<string, decimal>(LoadAll(), StringComparer.OrdinalIgnoreCase);
            if (value is > 0m)
            {
                costs[providerId] = value.Value;
            }
            else
            {
                costs.Remove(providerId);
            }
            PreferenceFile.WriteAtomic(PreferencePath, JsonSerializer.Serialize(costs));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
