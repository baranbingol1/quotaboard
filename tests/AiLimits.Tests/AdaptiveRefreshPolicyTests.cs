// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Refresh;

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
    [InlineData(5, 2)]      // exactly at the recent threshold stays recent
    [InlineData(6, 5)]      // just past it goes warm
    [InlineData(60, 5)]     // exactly one hour stays warm
    [InlineData(61, 15)]    // just past one hour goes idle
    [InlineData(239, 15)]   // just under four hours stays idle
    [InlineData(240, 30)]   // four hours flips to long idle
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
}
