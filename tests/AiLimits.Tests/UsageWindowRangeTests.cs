// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Usage;

namespace AiLimits.Tests;

public sealed class UsageWindowRangeTests
{
    private static readonly DateOnly Today = new(2026, 7, 15);

    [Fact]
    public void Reversed_range_cannot_restore_a_future_end_date()
    {
        UsageWindowRange range = UsageWindowRange.NormalizeCustom(Today.AddDays(10), Today.AddDays(-10), Today);

        Assert.Equal(Today.AddDays(-10), range.From);
        Assert.Equal(Today, range.Through);
    }

    [Fact]
    public void Entirely_future_range_collapses_to_today()
    {
        UsageWindowRange range = UsageWindowRange.NormalizeCustom(Today.AddDays(5), Today.AddDays(20), Today);

        Assert.Equal(Today, range.From);
        Assert.Equal(Today, range.Through);
    }

    [Fact]
    public void Range_is_limited_to_365_inclusive_days()
    {
        UsageWindowRange range = UsageWindowRange.NormalizeCustom(Today.AddDays(-500), Today, Today);

        Assert.Equal(Today.AddDays(-364), range.From);
        Assert.Equal(Today, range.Through);
    }
}
