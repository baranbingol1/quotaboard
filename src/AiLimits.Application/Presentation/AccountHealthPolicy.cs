// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Presentation;

/// <summary>Health state of an account card, before localization and styling.</summary>
public enum AccountHealth
{
    /// <summary>Last refresh succeeded and the snapshot is fresh.</summary>
    Live,
    /// <summary>Data shown from cache; the last refresh did not fail.</summary>
    Cached,
    SignInRequired,
    RateLimited,
    Offline,
    UnsupportedResponse,
    /// <summary>The source could not run right now but recovers on its own.</summary>
    Retrying,
    FetchFailed,
    NotConnected,
    NoQuota
}

/// <summary>
/// Decides an account card's health from the freshest snapshot's age and the
/// account's latest fetch-attempt outcome. Kept free of localization and
/// view-model types so the whole decision is unit-testable; the app layer
/// only maps the verdict to a label and a status brush.
///
/// The governing rules:
/// a failed latest attempt drops the card out of Live even when the snapshot
/// is still inside the freshness grace window; a transient (temporarily
/// unavailable) outcome reports Retrying, never sign-in.
/// </summary>
public static class AccountHealthPolicy
{
    /// <summary>How long a succeeded snapshot keeps a card Live.</summary>
    public static readonly TimeSpan LiveGrace = TimeSpan.FromMinutes(2);

    public static bool RequiresSignIn(FetchFailureKind kind) =>
        kind is FetchFailureKind.Authentication or FetchFailureKind.Authorization or FetchFailureKind.Unsupported;

    public static AccountHealth Decide(bool isConnected, TimeSpan? snapshotAge, FetchFailureKind latestFailure)
    {
        if (snapshotAge is { } age)
        {
            if (age < LiveGrace && latestFailure == FetchFailureKind.None)
            {
                return AccountHealth.Live;
            }
            if (RequiresSignIn(latestFailure))
            {
                return AccountHealth.SignInRequired;
            }
            // Last-good data stays visible; the state says why it stopped moving.
            return latestFailure switch
            {
                FetchFailureKind.RateLimited => AccountHealth.RateLimited,
                FetchFailureKind.Network or FetchFailureKind.Timeout => AccountHealth.Offline,
                FetchFailureKind.TemporarilyUnavailable => AccountHealth.Retrying,
                FetchFailureKind.MalformedResponse or FetchFailureKind.ProviderChanged or FetchFailureKind.OversizedResponse => AccountHealth.UnsupportedResponse,
                _ => AccountHealth.Cached
            };
        }
        if (!isConnected)
        {
            return AccountHealth.NotConnected;
        }
        if (RequiresSignIn(latestFailure))
        {
            return AccountHealth.SignInRequired;
        }
        // No snapshot yet *and* the last fetch failed is the first-fetch case:
        // there is no cached age to show, but reporting it as "no quota data"
        // made the card indistinguishable from a provider that simply never
        // reports quota — and the Overview drops NoQuota cards, so a provider
        // whose very first response failed to parse disappeared without a word.
        return latestFailure switch
        {
            FetchFailureKind.RateLimited => AccountHealth.RateLimited,
            FetchFailureKind.Network or FetchFailureKind.Timeout => AccountHealth.Offline,
            FetchFailureKind.TemporarilyUnavailable => AccountHealth.Retrying,
            FetchFailureKind.MalformedResponse or FetchFailureKind.ProviderChanged
                or FetchFailureKind.OversizedResponse => AccountHealth.UnsupportedResponse,
            FetchFailureKind.Unknown => AccountHealth.FetchFailed,
            _ => AccountHealth.NoQuota
        };
    }
}
