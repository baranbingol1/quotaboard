// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Presentation;

public enum PaceProjectionKind
{
    /// <summary>No projection: the meter lacks window data or is not in a live state.</summary>
    None,

    /// <summary>The current burn rate stays under the limit through the whole window.</summary>
    OnTrackByReset,

    /// <summary>The current burn rate reaches the limit before the window resets.</summary>
    ExhaustsBeforeReset,

    /// <summary>The burn rate already reached the limit inside the elapsed window.</summary>
    ExhaustedNow,
}

/// <summary>
/// A linear projection of meter usage over its reset window. Values are
/// provider-independent; formatting and localization live in the caller.
/// </summary>
public sealed record PaceProjection(
    PaceProjectionKind Kind,
    double ProjectedPercentByReset = 0.0,
    DateTimeOffset? ExhaustsAt = null
)
{
    public static readonly PaceProjection Unavailable = new(PaceProjectionKind.None);
}

/// <summary>
/// Projects whether a meter's current burn rate will exhaust its quota before
/// the window resets. The projection is deliberately linear and uses only the
/// meter's own window geometry — never other providers' usage or history.
/// </summary>
public static class MeterPacePolicy
{
    // Below 5% elapsed the projection is noise: one early burst reads as a
    // guaranteed exhaustion and one quiet hour reads as unlimited headroom.
    private const double MinimumElapsedFraction = 0.05;

    public static PaceProjection Project(UsageMeter meter, DateTimeOffset now)
    {
        if (
            meter.ResetsAt is not DateTimeOffset resetsAt
            || meter.WindowDuration is not { } window
            || meter.Status is MeterStatus.Stale or MeterStatus.Exhausted
            || window <= TimeSpan.Zero
            || resetsAt <= now
        )
        {
            return PaceProjection.Unavailable;
        }

        double used = meter.UsedPercent ?? 0.0;
        if (!double.IsFinite(used) || used <= 0.0 || used > 100.0)
        {
            return PaceProjection.Unavailable;
        }

        DateTimeOffset windowStart = resetsAt - window;
        TimeSpan elapsed = now - windowStart;
        if (elapsed <= TimeSpan.Zero)
        {
            return PaceProjection.Unavailable;
        }

        double elapsedFraction = elapsed.TotalSeconds / window.TotalSeconds;
        if (elapsedFraction < MinimumElapsedFraction)
        {
            return PaceProjection.Unavailable;
        }

        elapsedFraction = Math.Min(1.0, elapsedFraction);
        double projected = used / elapsedFraction;
        if (projected < 100.0)
        {
            return new PaceProjection(PaceProjectionKind.OnTrackByReset, projected);
        }

        // The rate reaches 100% after elapsed * (100 / used), because the same
        // linear rate is assumed to continue to the exhaustion point.
        DateTimeOffset exhaustsAt = windowStart + TimeSpan.FromSeconds(elapsed.TotalSeconds * (100.0 / used));
        return exhaustsAt <= now
            ? new PaceProjection(PaceProjectionKind.ExhaustedNow, 100.0, exhaustsAt)
            : new PaceProjection(PaceProjectionKind.ExhaustsBeforeReset, 100.0, exhaustsAt);
    }
}
