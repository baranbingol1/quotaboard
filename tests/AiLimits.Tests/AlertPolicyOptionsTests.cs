// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Alerts;
using Xunit;

namespace AiLimits.Tests;

public sealed class AlertPolicyOptionsTests
{
    [Fact]
    public void Missing_or_blank_file_yields_the_suggested_policy()
    {
        Assert.Equal(AlertPolicyOptions.Suggested, AlertPolicyOptions.Parse(null));
        Assert.Equal(AlertPolicyOptions.Suggested, AlertPolicyOptions.Parse("   "));
    }

    [Theory]
    [InlineData("True", true)]
    [InlineData("false", false)]
    [InlineData(" False ", false)]
    public void Legacy_bare_bool_files_only_change_the_enabled_flag(string text, bool enabled)
    {
        AlertPolicyOptions parsed = AlertPolicyOptions.Parse(text);

        Assert.Equal(enabled, parsed.Enabled);
        Assert.Equal(AlertPolicyOptions.DefaultWarnThreshold, parsed.WarnThresholdPercent);
        Assert.Equal(AlertPolicyOptions.DefaultCriticalThreshold, parsed.CriticalThresholdPercent);
        Assert.Equal(AlertPolicyOptions.DefaultResetReminderMinutes, parsed.ResetReminderMinutes);
    }

    [Fact]
    public void Round_trips_through_json()
    {
        AlertPolicyOptions saved = new(false, 70, 90, 15);

        AlertPolicyOptions loaded = AlertPolicyOptions.Parse(saved.Serialize());

        Assert.Equal(saved, loaded);
    }

    [Fact]
    public void Torn_or_wrongly_typed_files_fall_back_to_suggested()
    {
        Assert.Equal(AlertPolicyOptions.Suggested, AlertPolicyOptions.Parse("{\"enabled\":tru"));
        Assert.Equal(AlertPolicyOptions.Suggested, AlertPolicyOptions.Parse("[1,2,3]"));
        Assert.Equal(AlertPolicyOptions.Suggested, AlertPolicyOptions.Parse("{\"warnThresholdPercent\":\"eighty\"}"));
    }

    [Fact]
    public void Clamping_keeps_warn_strictly_below_critical()
    {
        AlertPolicyOptions clamped = new AlertPolicyOptions(true, 95, 50, 30).Clamped();

        Assert.Equal(95, clamped.WarnThresholdPercent);
        Assert.Equal(96, clamped.CriticalThresholdPercent);
    }

    [Fact]
    public void Clamping_bounds_all_values()
    {
        AlertPolicyOptions clamped = new AlertPolicyOptions(true, 0, 500, 99999).Clamped();

        Assert.Equal(1, clamped.WarnThresholdPercent);
        Assert.Equal(100, clamped.CriticalThresholdPercent);
        Assert.Equal(AlertPolicyOptions.MaxResetReminderMinutes, clamped.ResetReminderMinutes);
    }

    [Fact]
    public void Policy_exposes_the_threshold_pair_and_reminder()
    {
        AlertPolicy policy = new AlertPolicyOptions(true, 75, 98, 45).ToPolicy();

        Assert.Equal([75d, 98d], policy.UsageThresholds);
        Assert.Equal(TimeSpan.FromMinutes(45), policy.ResetReminder);
        Assert.True(policy.Enabled);
    }

    [Fact]
    public void Zero_reminder_minutes_disables_the_reset_reminder()
    {
        AlertPolicy policy = new AlertPolicyOptions(true, 80, 95, 0).ToPolicy();

        Assert.Null(policy.ResetReminder);
    }
}
