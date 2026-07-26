// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Pricing;

namespace AiLimits.Tests;

public sealed class PricingCatalogScheduleTests
{
    /// <summary>
    /// A wall-clock instant in the machine's own zone. The schedule is defined
    /// in local time, so a hardcoded offset here would pass on a UTC+3 desktop
    /// and fail on a UTC build agent.
    /// </summary>
    private static DateTimeOffset Local(int day, int hour, int minute = 0)
    {
        DateTime wall = new(2026, 7, day, hour, minute, 0, DateTimeKind.Unspecified);
        return new DateTimeOffset(wall, TimeZoneInfo.Local.GetUtcOffset(wall));
    }

    [Fact]
    public void A_morning_fetch_is_next_due_that_night()
    {
        Assert.Equal(Local(20, 20, 0), PricingCatalogSchedule.NextDue(Local(20, 8, 5)));
    }

    [Fact]
    public void A_night_fetch_is_next_due_the_following_morning()
    {
        Assert.Equal(Local(21, 8, 0), PricingCatalogSchedule.NextDue(Local(20, 20, 5)));
    }

    [Fact]
    public void A_pre_dawn_fetch_is_due_the_same_morning()
    {
        Assert.Equal(Local(20, 8, 0), PricingCatalogSchedule.NextDue(Local(20, 2, 30)));
    }

    [Theory]
    [InlineData(20, 7, 59, false)]  // just before the morning slot
    [InlineData(20, 8, 0, true)]    // exactly on it
    [InlineData(20, 19, 0, true)]   // still due, the slot was missed while closed
    public void Due_only_once_the_next_slot_is_reached(int day, int hour, int minute, bool expected)
    {
        // Fetched at 02:30, so the 08:00 slot is the next one.
        Assert.Equal(expected, PricingCatalogSchedule.IsDue(Local(20, 2, 30), Local(day, hour, minute)));
    }

    [Fact]
    public void Exactly_two_refreshes_land_in_a_day()
    {
        DateTimeOffset fetched = Local(20, 0, 30);
        int refreshes = 0;
        for (DateTimeOffset now = fetched; now < Local(21, 0, 30); now = now.AddMinutes(10))
        {
            if (PricingCatalogSchedule.IsDue(fetched, now))
            {
                refreshes++;
                fetched = now;
            }
        }

        Assert.Equal(2, refreshes);
    }

    [Fact]
    public void A_long_closure_becomes_due_immediately_rather_than_being_skipped()
    {
        // Closed for a week: the very next check must refetch, not wait for
        // the next wall-clock slot.
        Assert.True(PricingCatalogSchedule.IsDue(Local(13, 8, 0), Local(20, 12, 0)));
    }

    [Fact]
    public void A_cache_stamped_in_the_future_does_not_wedge_the_schedule_shut()
    {
        // Clock moved backwards, or the cache came from another machine. A
        // naive "now >= NextDue(fetchedAt)" would never fire again.
        Assert.True(PricingCatalogSchedule.IsDue(Local(25, 12, 0), Local(20, 12, 0)));
    }

    [Fact]
    public void The_two_slots_are_morning_and_night()
    {
        Assert.Equal(new TimeOnly(8, 0), PricingCatalogSchedule.MorningSlot);
        Assert.Equal(new TimeOnly(20, 0), PricingCatalogSchedule.NightSlot);
        Assert.True(PricingCatalogSchedule.MinimumRetryGap > TimeSpan.Zero);
    }
}
