// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Presentation;
using AiLimits.Domain;
using Xunit;

namespace AiLimits.Tests;

public sealed class MeterPacePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 12, 0, 0, TimeSpan.Zero);

    private static UsageMeter Meter(
        double? usedPercent,
        TimeSpan? window,
        DateTimeOffset? resetsAt,
        MeterStatus status = MeterStatus.Healthy
    ) =>
        new(
            new MeterKey("codex:weekly"),
            "Weekly limit",
            MeterScope.Account,
            MeterUnit.Percent,
            null,
            null,
            usedPercent,
            window,
            resetsAt,
            null,
            status,
            new MeterProvenance("test", "$.weekly", Now, true)
        );

    [Fact]
    public void Steady_rate_that_fits_projects_on_track()
    {
        // Half the five-hour window elapsed at 40%: linear pace reaches 80%.
        UsageMeter meter = Meter(40, TimeSpan.FromHours(5), Now + TimeSpan.FromHours(2.5));

        PaceProjection pace = MeterPacePolicy.Project(meter, Now);

        Assert.Equal(PaceProjectionKind.OnTrackByReset, pace.Kind);
        Assert.Equal(80.0, pace.ProjectedPercentByReset, precision: 5);
        Assert.Null(pace.ExhaustsAt);
    }

    [Fact]
    public void Burning_faster_than_the_window_exhausts_before_reset()
    {
        // 90% consumed with 80% of the window elapsed: the linear rate reaches
        // 100% at ~4.44h into a window started 4h ago — roughly 27 minutes
        // from now and well before the reset.
        UsageMeter meter = Meter(90, TimeSpan.FromHours(5), Now + TimeSpan.FromHours(1));

        PaceProjection pace = MeterPacePolicy.Project(meter, Now);

        Assert.Equal(PaceProjectionKind.ExhaustsBeforeReset, pace.Kind);
        Assert.NotNull(pace.ExhaustsAt);
        Assert.True(pace.ExhaustsAt > Now);
        Assert.True(pace.ExhaustsAt < meter.ResetsAt!.Value);
    }

    [Fact]
    public void Projection_that_already_reached_the_limit_reports_exhausted_now()
    {
        // Exactly 100% consumed while the window still runs: the rate reached
        // the limit at this instant, so the projection reports "exhausted now".
        UsageMeter meter = Meter(100, TimeSpan.FromHours(5), Now + TimeSpan.FromHours(2));

        PaceProjection pace = MeterPacePolicy.Project(meter, Now);

        Assert.Equal(PaceProjectionKind.ExhaustedNow, pace.Kind);
    }

    [Theory]
    [InlineData(0.0, 5)] // zero usage projects nothing
    [InlineData(150.0, 5)] // an impossible over-limit reading is not a projection
    [InlineData(50.0, 0)] // no window duration
    public void Degenerate_meters_have_no_projection(double usedPercent, int windowHours)
    {
        UsageMeter meter = Meter(
            usedPercent,
            windowHours > 0 ? TimeSpan.FromHours(windowHours) : null,
            Now + TimeSpan.FromHours(1)
        );

        Assert.Equal(PaceProjectionKind.None, MeterPacePolicy.Project(meter, Now).Kind);
    }

    [Fact]
    public void Too_little_elapsed_window_yields_no_projection()
    {
        // Only 4% of the window has elapsed: below the 5% noise floor.
        UsageMeter meter = Meter(80, TimeSpan.FromHours(5), Now + TimeSpan.FromMinutes(288));

        Assert.Equal(PaceProjectionKind.None, MeterPacePolicy.Project(meter, Now).Kind);
    }

    [Fact]
    public void Stale_or_exhausted_meters_have_no_projection()
    {
        UsageMeter stale = Meter(50, TimeSpan.FromHours(5), Now + TimeSpan.FromHours(2), MeterStatus.Stale);
        UsageMeter exhausted = Meter(50, TimeSpan.FromHours(5), Now + TimeSpan.FromHours(2), MeterStatus.Exhausted);

        Assert.Equal(PaceProjectionKind.None, MeterPacePolicy.Project(stale, Now).Kind);
        Assert.Equal(PaceProjectionKind.None, MeterPacePolicy.Project(exhausted, Now).Kind);
    }

    [Fact]
    public void Past_reset_or_missing_percent_have_no_projection()
    {
        UsageMeter pastReset = Meter(50, TimeSpan.FromHours(5), Now - TimeSpan.FromMinutes(1));
        UsageMeter noPercent = Meter(null, TimeSpan.FromHours(5), Now + TimeSpan.FromHours(2));

        Assert.Equal(PaceProjectionKind.None, MeterPacePolicy.Project(pastReset, Now).Kind);
        Assert.Equal(PaceProjectionKind.None, MeterPacePolicy.Project(noPercent, Now).Kind);
    }

    [Fact]
    public void Late_window_projection_extrapolates_the_remaining_fraction()
    {
        // 39% used with 97.5% of the window elapsed projects to exactly 40%.
        UsageMeter meter = Meter(39, TimeSpan.FromHours(4), Now + TimeSpan.FromMinutes(6));

        PaceProjection pace = MeterPacePolicy.Project(meter, Now);

        Assert.Equal(PaceProjectionKind.OnTrackByReset, pace.Kind);
        Assert.Equal(40.0, pace.ProjectedPercentByReset, precision: 3);
    }
}
