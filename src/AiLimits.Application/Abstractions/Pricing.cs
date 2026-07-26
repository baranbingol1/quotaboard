// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;
using AiLimits.Domain;

namespace AiLimits.Application.Abstractions;

public interface IModelResolver
{
    ModelResolution? Resolve(ServiceProviderId service, string rawModelId);
}

public interface IPricingCatalog
{
    Task<PricingCatalogSnapshot?> GetCurrentAsync(CancellationToken cancellationToken);

    Task<PricingCatalogSnapshot?> RefreshIfStaleAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Refreshes the catalog and reports what actually happened.
    /// <paramref name="force"/> bypasses the twice-daily schedule and the
    /// retry gap so the Settings button can refetch on demand.
    /// </summary>
    Task<PricingCatalogRefresh> RefreshAsync(bool force, CancellationToken cancellationToken);

    /// <summary>
    /// Why the last attempt failed, or null when it succeeded or none has run.
    /// Held in memory so a failure is visible immediately rather than only
    /// after the next successful write to disk.
    /// </summary>
    string? LastError { get; }

    /// <summary>When a network attempt was last made, successful or not.</summary>
    DateTimeOffset? LastAttemptAt { get; }
}
