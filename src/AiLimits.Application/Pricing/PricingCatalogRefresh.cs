// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Pricing;

public enum PricingCatalogOutcome
{
    /// <summary>Still inside the current schedule slot; no network call was made.</summary>
    NotDue,

    /// <summary>The server confirmed the cached copy is current (HTTP 304).</summary>
    Unchanged,

    /// <summary>A newer catalog was downloaded and cached.</summary>
    Updated,

    /// <summary>The attempt failed; <see cref="PricingCatalogRefresh.Error"/> says why and the cache is unchanged.</summary>
    Failed,
}

/// <summary>
/// Outcome of one catalog refresh attempt.
/// <para>
/// The catalog used to report nothing at all — every failure was swallowed by
/// a bare catch that returned the cached snapshot, so a fetch that had been
/// broken for a day looked identical to one that had just succeeded. Returning
/// the outcome is what lets Settings tell "verified minutes ago" apart from
/// "has not reached the server since Tuesday".
/// </para>
/// </summary>
/// <param name="Snapshot">The catalog now in effect; null only when nothing is cached and the fetch failed.</param>
/// <param name="Error">Human-readable failure reason, null unless <paramref name="Outcome"/> is Failed.</param>
public sealed record PricingCatalogRefresh(
    PricingCatalogOutcome Outcome,
    PricingCatalogSnapshot? Snapshot,
    string? Error = null
)
{
    public bool IsFailure => Outcome == PricingCatalogOutcome.Failed;
}
