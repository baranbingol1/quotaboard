// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;

namespace AiLimits.Presentation.WinUI;

/// <summary>
/// Persists user-entered per-1M-token prices for models the catalog cannot
/// price. Parse/serialize and quoting semantics live in
/// <see cref="ManualPriceOverrideSet"/> so they stay unit-testable.
/// </summary>
public static class ModelPricingOverridePreference
{
    private static readonly string PreferencePath = AppDataDirectory.File("model-pricing-overrides.json");

    public static ManualPriceOverrideSet LoadAll()
    {
        try
        {
            return File.Exists(PreferencePath)
                ? ManualPriceOverrideSet.Parse(File.ReadAllText(PreferencePath))
                : ManualPriceOverrideSet.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ManualPriceOverrideSet.Empty;
        }
    }

    public static void SaveFor(string serviceId, string rawModelId, ManualModelPrice? price)
    {
        try
        {
            PreferenceFile.WriteAtomic(PreferencePath, LoadAll().With(serviceId, rawModelId, price).Serialize());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
