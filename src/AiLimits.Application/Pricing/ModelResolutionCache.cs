// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Pricing;

/// <summary>
/// Memoises model resolution for one projection. The catalog is fixed for the
/// duration, so the same (service, model) pair always resolves the same way —
/// and the history aggregations ask about the same few dozen pairs hundreds of
/// times each.
/// </summary>
public sealed class ModelResolutionCache(ExplicitModelResolver explicitResolver, CatalogModelResolver catalogResolver)
{
    private readonly Dictionary<(string Service, string Model), ModelResolution?> _entries = [];

    public ModelResolution? Resolve(ServiceProviderId service, string rawModelId, PricingCatalogSnapshot? catalog)
    {
        (string, string) key = (service.Value, rawModelId);
        if (_entries.TryGetValue(key, out ModelResolution? cached))
        {
            return cached;
        }
        ModelResolution? resolved = catalog is null
            ? explicitResolver.Resolve(service, rawModelId)
            : catalogResolver.Resolve(service, rawModelId, catalog);
        _entries[key] = resolved;
        return resolved;
    }
}
