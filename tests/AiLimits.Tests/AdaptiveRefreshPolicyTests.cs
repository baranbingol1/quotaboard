// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Refresh;
using AiLimits.Domain;

namespace AiLimits.Tests;

public sealed class AdaptiveRefreshPolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly AdaptiveRefreshPolicy _policy = new();

    [Fact]
    public void RecentInteractionRefreshesEveryTwoMinutes()
    {
        var decision = _policy.NextDelay(Now, Now.AddMinutes(-1), energySaverEnabled: false);
        Assert.Equal(TimeSpan.FromMinutes(2), decision.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.RecentInteraction, decision.Reason);
    }

    [Theory]
    [InlineData(5, 2)] // exactly at the recent threshold stays recent
    [InlineData(6, 5)] // just past it goes warm
    [InlineData(60, 5)] // exactly one hour stays warm
    [InlineData(61, 15)] // just past one hour goes idle
    [InlineData(239, 15)] // just under four hours stays idle
    [InlineData(240, 30)] // four hours flips to long idle
    [InlineData(600, 30)]
    public void CadenceFollowsInteractionAge(int ageMinutes, int expectedDelayMinutes)
    {
        var decision = _policy.NextDelay(Now, Now.AddMinutes(-ageMinutes), energySaverEnabled: false);
        Assert.Equal(TimeSpan.FromMinutes(expectedDelayMinutes), decision.Delay);
    }

    [Fact]
    public void NoInteractionEverMeansLongIdle()
    {
        var decision = _policy.NextDelay(Now, null, energySaverEnabled: false);
        Assert.Equal(TimeSpan.FromMinutes(30), decision.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.LongIdle, decision.Reason);
    }

    [Fact]
    public void EnergySaverOverridesEverything()
    {
        var decision = _policy.NextDelay(Now, Now.AddMinutes(-1), energySaverEnabled: true);
        Assert.Equal(TimeSpan.FromMinutes(30), decision.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.Constrained, decision.Reason);
    }

    [Fact]
    public void FutureInteractionTimestampReadsAsRecent()
    {
        // A clock adjustment must not park the app on the slowest cadence.
        var decision = _policy.NextDelay(Now, Now.AddMinutes(10), energySaverEnabled: false);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.RecentInteraction, decision.Reason);
    }

    [Theory]
    [InlineData(null, 30)] // never touched: long idle
    [InlineData(240, 30)] // four hours old: long idle
    [InlineData(61, 15)] // one hour old: idle
    public void AgentActivityCapsIdleCadenceAtFiveMinutes(int? ageMinutes, int expectedWithoutActivityMinutes)
    {
        DateTimeOffset? interaction = ageMinutes.HasValue ? Now.AddMinutes(-ageMinutes.Value) : null;

        var withoutActivity = _policy.NextDelay(Now, interaction, energySaverEnabled: false);
        var withActivity = _policy.NextDelay(Now, interaction, energySaverEnabled: false, recentAgentActivity: true);

        Assert.Equal(TimeSpan.FromMinutes(expectedWithoutActivityMinutes), withoutActivity.Delay);
        Assert.Equal(TimeSpan.FromMinutes(5), withActivity.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.AgentActive, withActivity.Reason);
    }

    [Fact]
    public void AgentActivityNeverPullsAFasterDecisionLater()
    {
        var decision = _policy.NextDelay(Now, Now.AddMinutes(-1), energySaverEnabled: false, recentAgentActivity: true);

        Assert.Equal(TimeSpan.FromMinutes(2), decision.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.RecentInteraction, decision.Reason);
    }

    [Fact]
    public void EnergySaverStillBeatsAgentActivity()
    {
        var decision = _policy.NextDelay(
            Now,
            Now.AddMinutes(-240),
            energySaverEnabled: true,
            recentAgentActivity: true
        );

        Assert.Equal(TimeSpan.FromMinutes(30), decision.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.Constrained, decision.Reason);
    }

    [Fact]
    public void AgentActivityCapIsExactlyTheWarmDelay()
    {
        // Warm already refreshes every five minutes, so the cap is a no-op there.
        var decision = _policy.NextDelay(
            Now,
            Now.AddMinutes(-30),
            energySaverEnabled: false,
            recentAgentActivity: true
        );

        Assert.Equal(TimeSpan.FromMinutes(5), decision.Delay);
        Assert.Equal(AdaptiveRefreshPolicy.Reason.Warm, decision.Reason);
    }

    [Theory]
    [InlineData(1, 15)]
    [InlineData(2, 45)]
    [InlineData(3, 120)]
    [InlineData(4, 300)]
    public void TransientRetryLadderEscalates(int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), AdaptiveRefreshPolicy.TransientRetryDelay(attempt));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(-1)]
    public void RetryLadderExhaustsOutsideAttemptRange(int attempt)
    {
        Assert.Null(AdaptiveRefreshPolicy.TransientRetryDelay(attempt));
    }

    [Theory]
    [InlineData(FetchFailureKind.Network, true)]
    [InlineData(FetchFailureKind.Timeout, true)]
    [InlineData(FetchFailureKind.TemporarilyUnavailable, true)]
    [InlineData(FetchFailureKind.Authentication, false)]
    [InlineData(FetchFailureKind.Authorization, false)]
    [InlineData(FetchFailureKind.MalformedResponse, false)]
    [InlineData(FetchFailureKind.Unsupported, false)]
    public void OnlySchedulerTransientFailureKindsEnterTheFastRetryLadder(FetchFailureKind failureKind, bool expected)
    {
        var attempt = new FetchAttempt(
            "attempt",
            new AccountKey(new ProviderId("fake"), "one"),
            "strategy",
            Now,
            TimeSpan.Zero,
            failureKind,
            "message"
        );
        var publication = new RefreshPublication(
            RefreshPublicationStatus.FailedWithoutData,
            null,
            [attempt],
            1,
            "failed"
        );

        Assert.Equal(expected, AdaptiveRefreshPolicy.IsTransientFailure(publication));
    }
}
