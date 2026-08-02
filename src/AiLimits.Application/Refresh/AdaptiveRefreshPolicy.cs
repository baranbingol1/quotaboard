// SPDX-License-Identifier: Apache-2.0
using System;
using AiLimits.Domain;

namespace AiLimits.Application.Refresh;

/// <summary>
/// Canonical adaptive-refresh decision table (ported from CodexBar's
/// AdaptiveRefreshPolicyCore). The cadence follows user attention: fast while
/// the window was recently in front, slow when nobody is looking, slowest on
/// battery saver. Thresholds and delays live here only.
/// </summary>
public sealed class AdaptiveRefreshPolicy
{
    public enum Reason
    {
        RecentInteraction,
        Warm,
        Idle,
        LongIdle,
        Constrained
    }

    public readonly record struct Decision(TimeSpan Delay, Reason Reason);

    private static readonly TimeSpan RecentInteractionThreshold = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan WarmThreshold = TimeSpan.FromHours(1);
    private static readonly TimeSpan IdleThreshold = TimeSpan.FromHours(4);

    private static readonly TimeSpan RecentInteractionDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan WarmDelay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan LongIdleDelay = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan ConstrainedDelay = TimeSpan.FromMinutes(30);

    public Decision NextDelay(DateTimeOffset now, DateTimeOffset? lastInteractionAt, bool energySaverEnabled)
    {
        if (energySaverEnabled)
        {
            return new Decision(ConstrainedDelay, Reason.Constrained);
        }
        if (!lastInteractionAt.HasValue)
        {
            return new Decision(LongIdleDelay, Reason.LongIdle);
        }
        // A future or clock-adjusted timestamp yields a negative age, which
        // reads as recent.
        TimeSpan age = now - lastInteractionAt.Value;
        if (age <= RecentInteractionThreshold)
        {
            return new Decision(RecentInteractionDelay, Reason.RecentInteraction);
        }
        if (age <= WarmThreshold)
        {
            return new Decision(WarmDelay, Reason.Warm);
        }
        if (age < IdleThreshold)
        {
            return new Decision(IdleDelay, Reason.Idle);
        }
        return new Decision(LongIdleDelay, Reason.LongIdle);
    }

    /// <summary>
    /// Escalating retry delays after a refresh that failed for a transient
    /// connectivity reason (network unreachable, timeout). Returns null once
    /// the ladder is exhausted; callers then fall back to the adaptive cadence.
    /// </summary>
    public static TimeSpan? TransientRetryDelay(int attempt)
    {
        return attempt switch
        {
            1 => TimeSpan.FromSeconds(15),
            2 => TimeSpan.FromSeconds(45),
            3 => TimeSpan.FromSeconds(120),
            4 => TimeSpan.FromSeconds(300),
            _ => null
        };
    }

    /// <summary>
    /// Whether a failed publication should use the scheduler's fast retry
    /// ladder. Authentication, authorization, malformed and unsupported
    /// failures remain actionable rather than being retried aggressively.
    /// </summary>
    public static bool IsTransientFailure(RefreshPublication publication) =>
        publication.Status is RefreshPublicationStatus.FailedWithCachedData or RefreshPublicationStatus.FailedWithoutData
        && publication.Attempts.Any(attempt => attempt.FailureKind is
            FetchFailureKind.Network or FetchFailureKind.Timeout or FetchFailureKind.TemporarilyUnavailable);
}
