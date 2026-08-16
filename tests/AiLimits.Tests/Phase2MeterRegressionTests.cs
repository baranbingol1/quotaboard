// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Presentation;
using AiLimits.Domain;
using AiLimits.Presentation.WinUI;
using AiLimits.Presentation.WinUI.ViewModels;
using Xunit;

namespace AiLimits.Tests;

public sealed class Phase2MeterRegressionTests
{
    [Fact]
    public void Unknown_percentage_never_looks_on_track_or_fully_remaining()
    {
        var meter = new MeterViewModel("test", "Test", null, "-", "-", "No reset", null, MeterStatus.Unknown);

        Assert.DoesNotContain("%", meter.PercentLabel, StringComparison.Ordinal);
        Assert.DoesNotContain("OnTrack", meter.StatusLabel, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("100%", meter.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void Reset_reminder_automation_uses_the_privacy_safe_account()
    {
        var reminder = new ResetHorizonItemViewModel(
            "Provider",
            "alice@example.com",
            "Weekly limit",
            "2d 4h",
            "Monday",
            "#000000",
            DateTimeOffset.UtcNow
        );

        Assert.Equal(EmailPrivacyPreference.Apply(reminder.Account), reminder.DisplayAccount);
        if (EmailPrivacyPreference.Enabled)
        {
            Assert.DoesNotContain(reminder.Account, reminder.DisplayAccount, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Inactive_factory_window_starts_on_next_use()
    {
        var meter = new UsageMeter(
            new MeterKey("droid:five-hour"),
            "5-hour limit",
            MeterScope.Account,
            MeterUnit.Percent,
            null,
            null,
            0,
            null,
            null,
            null,
            MeterStatus.Healthy,
            new MeterProvenance("droid.factory-cli-oauth", "$.limits.standard.fiveHour", DateTimeOffset.UtcNow, true)
        );

        Assert.True(MeterResetPolicy.StartsOnNextUse("droid", meter));
        Assert.False(MeterResetPolicy.StartsOnNextUse("other", meter));
    }
}
