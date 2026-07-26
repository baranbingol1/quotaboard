// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Tray;
using AiLimits.Domain;

namespace AiLimits.Tests;

public sealed class TrayMeterStatusPolicyTests
{
    [Fact]
    public void Worst_uses_the_highest_current_meter_state()
    {
        MeterStatus result = TrayMeterStatusPolicy.Worst(
            [MeterStatus.Healthy, MeterStatus.Approaching, MeterStatus.Critical]);

        Assert.Equal(MeterStatus.Critical, result);
    }

    [Fact]
    public void Stale_and_unavailable_readings_do_not_override_current_status()
    {
        MeterStatus result = TrayMeterStatusPolicy.Worst(
            [MeterStatus.Stale, MeterStatus.Unavailable, MeterStatus.Healthy]);

        Assert.Equal(MeterStatus.Healthy, result);
    }

    [Fact]
    public void No_current_meter_falls_back_to_healthy()
    {
        MeterStatus result = TrayMeterStatusPolicy.Worst(
            [MeterStatus.Unknown, MeterStatus.Stale, MeterStatus.Unavailable]);

        Assert.Equal(MeterStatus.Healthy, result);
    }
}
