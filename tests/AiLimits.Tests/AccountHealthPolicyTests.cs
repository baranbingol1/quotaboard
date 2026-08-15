// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Presentation;
using AiLimits.Domain;

namespace AiLimits.Tests;

public sealed class AccountHealthPolicyTests
{
    [Fact]
    public void A_fresh_snapshot_with_a_succeeded_latest_attempt_is_live()
    {
        Assert.Equal(
            AccountHealth.Live,
            AccountHealthPolicy.Decide(isConnected: true, snapshotAge: TimeSpan.FromSeconds(30), FetchFailureKind.None)
        );
    }

    [Fact]
    public void A_fresh_snapshot_with_a_failed_latest_attempt_is_not_live()
    {
        // Regression: the 2-minute grace used to keep a card Live right after
        // its refresh failed, because only the snapshot age was consulted.
        Assert.Equal(
            AccountHealth.Offline,
            AccountHealthPolicy.Decide(
                isConnected: true,
                snapshotAge: TimeSpan.FromSeconds(30),
                FetchFailureKind.Network
            )
        );
        Assert.Equal(
            AccountHealth.SignInRequired,
            AccountHealthPolicy.Decide(
                isConnected: true,
                snapshotAge: TimeSpan.FromSeconds(30),
                FetchFailureKind.Authentication
            )
        );
    }

    [Fact]
    public void An_aged_snapshot_with_a_succeeded_latest_attempt_is_cached()
    {
        Assert.Equal(
            AccountHealth.Cached,
            AccountHealthPolicy.Decide(isConnected: true, snapshotAge: TimeSpan.FromMinutes(10), FetchFailureKind.None)
        );
    }

    [Fact]
    public void A_temporarily_unavailable_latest_attempt_is_retrying_never_sign_in()
    {
        Assert.Equal(
            AccountHealth.Retrying,
            AccountHealthPolicy.Decide(isConnected: true, snapshotAge: null, FetchFailureKind.TemporarilyUnavailable)
        );
        Assert.Equal(
            AccountHealth.Retrying,
            AccountHealthPolicy.Decide(
                isConnected: true,
                snapshotAge: TimeSpan.FromMinutes(10),
                FetchFailureKind.TemporarilyUnavailable
            )
        );
        Assert.False(AccountHealthPolicy.RequiresSignIn(FetchFailureKind.TemporarilyUnavailable));
    }

    [Fact]
    public void Auth_failures_require_sign_in_with_or_without_a_snapshot()
    {
        Assert.Equal(
            AccountHealth.SignInRequired,
            AccountHealthPolicy.Decide(isConnected: true, snapshotAge: null, FetchFailureKind.Authorization)
        );
        Assert.Equal(
            AccountHealth.SignInRequired,
            AccountHealthPolicy.Decide(
                isConnected: true,
                snapshotAge: TimeSpan.FromHours(1),
                FetchFailureKind.Authentication
            )
        );
    }

    [Fact]
    public void A_disconnected_account_without_data_is_not_connected()
    {
        Assert.Equal(
            AccountHealth.NotConnected,
            AccountHealthPolicy.Decide(isConnected: false, snapshotAge: null, FetchFailureKind.Network)
        );
    }

    [Fact]
    public void A_clean_account_without_data_reports_no_quota()
    {
        Assert.Equal(
            AccountHealth.NoQuota,
            AccountHealthPolicy.Decide(isConnected: true, snapshotAge: null, FetchFailureKind.None)
        );
    }
}
