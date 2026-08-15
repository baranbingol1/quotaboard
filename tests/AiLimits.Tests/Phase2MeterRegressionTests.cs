// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Presentation;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Copilot;
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

    [Theory]
    [InlineData(-20, 100)]
    [InlineData(120, 0)]
    [InlineData(50, 50)]
    public void Copilot_percent_remaining_is_clamped(double remainingPercent, double expectedUsed)
    {
        string json =
            "{\"quota_snapshots\":{\"chat\":{\"entitlement\":100,\"remaining\":50,\"percent_remaining\":"
            + remainingPercent.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + "}}}";
        using JsonDocument document = JsonDocument.Parse(json);
        var strategy = new CopilotQuotaStrategy(new HttpClient(), new TestClock());

        UsageMeter meter = Assert.Single(
            strategy.ParseMeters(new ProviderId("copilot"), document.RootElement, DateTimeOffset.UtcNow)
        );

        Assert.Equal(expectedUsed, meter.UsedPercent);
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

    private sealed class TestClock : AiLimits.Application.Abstractions.IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
