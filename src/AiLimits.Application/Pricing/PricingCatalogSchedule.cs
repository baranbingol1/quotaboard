// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Pricing;

/// <summary>
/// When the models.dev catalog is due for a refresh: twice a day, once in the
/// morning and once at night, anchored to wall-clock slots rather than to a
/// rolling interval.
/// <para>
/// A plain "every N hours" TTL drifts — each refresh pushes the next one later
/// by however long the app happened to be closed, so a machine that is only
/// used in the evening eventually never refreshes in the morning. Anchoring to
/// fixed local times keeps the two daily fetches predictable and makes a
/// missed slot catch up on the next launch instead of being skipped.
/// </para>
/// </summary>
public static class PricingCatalogSchedule
{
    /// <summary>Local time of the morning refresh slot.</summary>
    public static readonly TimeOnly MorningSlot = new(8, 0);

    /// <summary>Local time of the night refresh slot.</summary>
    public static readonly TimeOnly NightSlot = new(20, 0);

    /// <summary>
    /// Shortest gap between two network attempts. Only bounds the failure
    /// path: a fetch that fails does not advance <c>FetchedAt</c>, so without
    /// this the catalog would be retried on every background refresh tick.
    /// </summary>
    public static readonly TimeSpan MinimumRetryGap = TimeSpan.FromMinutes(10);

    /// <summary>
    /// The first scheduled slot strictly after <paramref name="fetchedAt"/>,
    /// in that value's own offset.
    /// </summary>
    public static DateTimeOffset NextDue(DateTimeOffset fetchedAt)
    {
        DateTimeOffset morning = SlotOn(fetchedAt, MorningSlot);
        if (morning > fetchedAt)
        {
            return morning;
        }
        DateTimeOffset night = SlotOn(fetchedAt, NightSlot);
        if (night > fetchedAt)
        {
            return night;
        }
        return SlotOn(fetchedAt.AddDays(1), MorningSlot);
    }

    /// <summary>
    /// Whether a catalog last fetched at <paramref name="fetchedAt"/> should be
    /// refetched at <paramref name="now"/>. Both are compared in local time so
    /// "morning" and "night" mean what the user sees on the clock.
    /// </summary>
    public static bool IsDue(DateTimeOffset fetchedAt, DateTimeOffset now)
    {
        DateTimeOffset localFetched = fetchedAt.ToLocalTime();
        DateTimeOffset localNow = now.ToLocalTime();
        // A cache stamped in the future (clock moved backwards, or a file
        // copied from another machine) must not wedge the schedule shut.
        return localNow < localFetched || localNow >= NextDue(localFetched);
    }

    private static DateTimeOffset SlotOn(DateTimeOffset day, TimeOnly slot) =>
        new(day.Year, day.Month, day.Day, slot.Hour, slot.Minute, 0, day.Offset);
}
