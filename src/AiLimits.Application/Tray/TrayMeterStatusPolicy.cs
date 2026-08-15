// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Tray;

/// <summary>
/// Defines the four current quota states represented by the tray icon.
/// Unknown, unavailable, and stale readings never override a current meter.
/// </summary>
public static class TrayMeterStatusPolicy
{
    public static bool IsCurrent(MeterStatus status) =>
        status is MeterStatus.Healthy or MeterStatus.Approaching or MeterStatus.Critical or MeterStatus.Exhausted;

    public static int Severity(MeterStatus status) =>
        status switch
        {
            MeterStatus.Exhausted => 4,
            MeterStatus.Critical => 3,
            MeterStatus.Approaching => 2,
            MeterStatus.Healthy => 1,
            _ => 0,
        };

    public static MeterStatus Worst(IEnumerable<MeterStatus> statuses) =>
        statuses.Where(IsCurrent).OrderByDescending(Severity).FirstOrDefault(MeterStatus.Healthy);
}
