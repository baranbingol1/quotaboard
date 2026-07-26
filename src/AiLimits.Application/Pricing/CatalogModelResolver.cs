// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;
using System.Runtime.CompilerServices;

namespace AiLimits.Application.Pricing;

public sealed class CatalogModelResolver
{
    // OpenAI bills Codex "fast" variants at 2.5x the base model; models.dev often
    // lacks the -fast id, so pricing derives from the base entry.
    public const decimal FastVariantMultiplier = 2.5m;

    private readonly ExplicitModelResolver _explicitResolver;

    // Host-path ids like "accounts/fireworks/models/glm-5p2" are globally unique,
    // so they can be matched against the whole catalog when the vendor-scoped
    // lookup misses. Built once per catalog snapshot.
    private static readonly ConditionalWeakTable<PricingCatalogSnapshot, Dictionary<string, (string Provider, string Model)>> FullIdIndexes = new();

    public CatalogModelResolver(ExplicitModelResolver explicitResolver)
    {
        _explicitResolver = explicitResolver;
    }

    public ModelResolution? Resolve(ServiceProviderId service, string rawModelId, PricingCatalogSnapshot catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog, "catalog");
        ModelResolution modelResolution = _explicitResolver.Resolve(service, rawModelId);
        if (modelResolution is not null)
        {
            // The catalog index is lowercase on both sides; aliases may not be.
            ModelResolution lowered = modelResolution with
            {
                PricingProviderId = modelResolution.PricingProviderId.ToLowerInvariant(),
                CanonicalModelId = modelResolution.CanonicalModelId.ToLowerInvariant()
            };
            if (catalog.ExactIndex.ContainsKey((lowered.PricingProviderId, lowered.CanonicalModelId)))
            {
                return lowered;
            }
        }
        string pricingProviderId = ModelVendorClassifier.GetPricingProviderId(service.Value, rawModelId);
        if (pricingProviderId != null)
        {
            string text = rawModelId.Trim().ToLowerInvariant();
            int separator = text.LastIndexOf('/');
            if (separator >= 0 && separator < text.Length - 1)
            {
                text = text.Substring(separator + 1);
            }
            if (catalog.ExactIndex.ContainsKey((pricingProviderId, text)))
            {
                return new ModelResolution(pricingProviderId, text, ResolutionConfidence.Exact);
            }
            string text2 = ModelIdNormalizer.Normalize(text);
            if (!string.Equals(text, text2, StringComparison.Ordinal) && catalog.ExactIndex.ContainsKey((pricingProviderId, text2)))
            {
                return new ModelResolution(pricingProviderId, text2, ResolutionConfidence.Exact);
            }
            string text3 = text2.Replace('.', '-');
            if (!string.Equals(text2, text3, StringComparison.Ordinal) && catalog.ExactIndex.ContainsKey((pricingProviderId, text3)))
            {
                return new ModelResolution(pricingProviderId, text3, ResolutionConfidence.Exact);
            }
            if (pricingProviderId == "openai" && text2.EndsWith("-fast", StringComparison.Ordinal))
            {
                string baseId = text2[..^"-fast".Length];
                if (baseId.Length > 0 && catalog.ExactIndex.ContainsKey((pricingProviderId, baseId)))
                {
                    return new ModelResolution(pricingProviderId, baseId, ResolutionConfidence.DerivedMultiplier, FastVariantMultiplier);
                }
            }
        }
        return ResolveFullPathId(rawModelId, catalog);
    }

    // Some tools report the serving host's full model path (Amp via Fireworks:
    // "accounts/fireworks/models/glm-5p2"). models.dev catalogs those hosts
    // under their own provider with the identical path id, so the whole raw id
    // is looked up across the catalog as a last resort.
    private static ModelResolution? ResolveFullPathId(string rawModelId, PricingCatalogSnapshot catalog)
    {
        string full = rawModelId.Trim().ToLowerInvariant();
        if (!full.Contains('/'))
        {
            return null;
        }
        Dictionary<string, (string Provider, string Model)> index = FullIdIndexes.GetValue(catalog, BuildFullIdIndex);
        return index.TryGetValue(full, out (string Provider, string Model) key)
            ? new ModelResolution(key.Provider, key.Model, ResolutionConfidence.Exact)
            : null;
    }

    private static Dictionary<string, (string Provider, string Model)> BuildFullIdIndex(PricingCatalogSnapshot catalog)
    {
        Dictionary<string, (string Provider, string Model)> index = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        foreach ((string Provider, string Model) key in catalog.ExactIndex.Keys)
        {
            if (key.Model.Contains('/'))
            {
                // First provider wins on the (unlikely) duplicate of a full path.
                index.TryAdd(key.Model, key);
            }
        }
        return index;
    }
}
