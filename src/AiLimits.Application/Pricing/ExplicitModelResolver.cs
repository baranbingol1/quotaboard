// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;

namespace AiLimits.Application.Pricing;

public sealed class ExplicitModelResolver : IModelResolver
{
    private readonly IReadOnlyDictionary<(string Service, string RawModel), ModelResolution> _aliases;

    public ExplicitModelResolver(IEnumerable<ModelAlias> aliases)
    {
        _aliases = aliases.ToDictionary(
            (ModelAlias alias) => (Value: alias.Service.Value, ModelIdNormalizer.Normalize(alias.RawModelId)),
            (ModelAlias alias) =>
                new ModelResolution(
                    alias.PricingProviderId,
                    alias.CanonicalModelId,
                    alias.RawModelId.Equals(alias.CanonicalModelId, StringComparison.OrdinalIgnoreCase)
                        ? ResolutionConfidence.Exact
                        : ResolutionConfidence.ExplicitAlias
                )
        );
    }

    public ModelResolution? Resolve(ServiceProviderId service, string rawModelId)
    {
        ModelResolution value;
        return _aliases.TryGetValue((service.Value, ModelIdNormalizer.Normalize(rawModelId)), out value) ? value : null;
    }
}
