// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Pricing;

public sealed record ModelAlias(
    ServiceProviderId Service,
    string RawModelId,
    string PricingProviderId,
    string CanonicalModelId
);
