// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Application.Usage;

/// <summary>
/// Normalizes a user-selected history range to an ordered, non-future window
/// with a bounded inclusive length.
/// </summary>
public readonly record struct UsageWindowRange(DateOnly From, DateOnly Through)
{
    public static UsageWindowRange NormalizeCustom(
        DateOnly first,
        DateOnly second,
        DateOnly today,
        int maximumDays = 365)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumDays, 1);

        if (first > second)
        {
            (first, second) = (second, first);
        }

        DateOnly through = second > today ? today : second;
        DateOnly from = first > through ? through : first;
        if (through.DayNumber - from.DayNumber + 1 > maximumDays)
        {
            from = through.AddDays(1 - maximumDays);
        }

        return new UsageWindowRange(from, through);
    }
}
