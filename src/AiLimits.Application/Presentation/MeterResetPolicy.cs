// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Presentation;

public static class MeterResetPolicy
{
    public static bool StartsOnNextUse(string providerId, UsageMeter meter) =>
        string.Equals(providerId, "droid", StringComparison.Ordinal)
        && meter.UsedPercent is 0.0
        && meter.Status == MeterStatus.Healthy
        && meter.Provenance.SourcePath.StartsWith("$.limits.", StringComparison.Ordinal);
}
