// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Claude;
using AiLimits.Infrastructure.Providers.Codex;
using AiLimits.Infrastructure.Providers.Droid;
using AiLimits.Infrastructure.Providers.Shared;

namespace AiLimits.Tests;

public sealed class ProviderLimitParsingTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 14, 0, 0, 0, TimeSpan.Zero);

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private static ProviderAccount Account(string provider) =>
        new(new AccountKey(new ProviderId(provider), "acct"), provider, null, "test", 1L, IsConnected: true);

    private static CodexOAuthLimitStrategy CodexStrategy() =>
        new(new HttpClient(), new FixedClock(), Account("codex"), "unused.json");

    [Fact]
    public void CodexAdditionalRateLimitDoesNotDuplicateWeekly()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rate_limit": {
                "primary_window": {"used_percent": 20, "reset_at": 1784499168, "limit_window_seconds": 604800}
              },
              "additional_rate_limits": [
                {
                  "limit_name": "GPT-5.3-Codex-Spark",
                  "metered_feature": "codex_spark",
                  "rate_limit": {
                    "primary_window": {"used_percent": 0, "reset_at": 1784574830, "limit_window_seconds": 604800}
                  }
                }
              ]
            }
            """
        );
        var meters = CodexStrategy().ExtractMeters(document.RootElement, new ProviderId("codex"), Now);
        Assert.Equal(2, meters.Count);
        var weekly = Assert.Single(meters, meter => meter.DisplayName == "Weekly limit");
        Assert.Equal(20.0, weekly.UsedPercent);
        var spark = Assert.Single(meters, meter => meter.DisplayName == "GPT-5.3-Codex-Spark weekly limit");
        Assert.Equal(0.0, spark.UsedPercent);
        Assert.Equal(MeterScope.Model, spark.Scope);
        Assert.Equal(TimeSpan.FromDays(7), spark.WindowDuration);
    }

    [Fact]
    public void CodexPrimaryAndSecondaryWindowsSlotByRole()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rate_limit": {
                "primary_window": {"used_percent": 41, "reset_at": 1784499168, "limit_window_seconds": 18000},
                "secondary_window": {"used_percent": 12, "reset_at": 1784574830, "limit_window_seconds": 604800}
              }
            }
            """
        );
        var meters = CodexStrategy().ExtractMeters(document.RootElement, new ProviderId("codex"), Now);
        Assert.Equal(2, meters.Count);
        Assert.Single(meters, meter => meter.DisplayName == "5-hour limit");
        Assert.Single(meters, meter => meter.DisplayName == "Weekly limit");
    }

    [Fact]
    public void CodexTwoWeeklyMainLanesGetDistinctNames()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "rate_limit": {
                "primary_window": {"used_percent": 20, "reset_at": 1784499168, "limit_window_seconds": 604800},
                "secondary_window": {"used_percent": 0, "reset_at": 1784574830, "limit_window_seconds": 604800}
              }
            }
            """
        );
        var meters = CodexStrategy().ExtractMeters(document.RootElement, new ProviderId("codex"), Now);
        Assert.Equal(2, meters.Count);
        Assert.Equal(2, meters.Select(meter => meter.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Single(meters, meter => meter.DisplayName == "Weekly limit");
    }

    [Fact]
    public void FactoryCorePoolMetersAreLabeledDroidCore()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "limits": {
                "standard": {
                  "fiveHour": {"usedPercent": 9},
                  "weekly": {"usedPercent": 56, "windowEnd": "2026-07-21T00:00:00Z"},
                  "monthly": {"usedPercent": 29}
                },
                "core": {
                  "fiveHour": {"usedPercent": 5},
                  "weekly": {"usedPercent": 33},
                  "monthly": {"usedPercent": 19}
                }
              }
            }
            """
        );
        var strategy = new DroidApiLimitStrategy(new HttpClient(), new FixedClock());
        var result = strategy.BuildSnapshot(
            new AccountKey(new ProviderId("droid"), "default"),
            document.RootElement,
            billingLimits: true,
            started: 0L,
            planName: "Pro",
            email: "factory.user@example.com"
        );
        Assert.True(result.IsSuccess);
        var meters = result.Snapshot!.Meters;
        Assert.Equal(6, meters.Count);
        Assert.Equal(3, meters.Count(meter => meter.DisplayName.StartsWith("Droid Core ", StringComparison.Ordinal)));
        Assert.Single(meters, meter => meter.DisplayName == "Droid Core weekly limit");
        var weekly = Assert.Single(meters, meter => meter.DisplayName == "Weekly limit");
        Assert.Equal(new DateTimeOffset(2026, 7, 21, 0, 0, 0, TimeSpan.Zero), weekly.ResetsAt);
        Assert.Equal(6, meters.Select(meter => meter.DisplayName).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("factory.user@example.com", result.Snapshot!.Extensions["email"].GetString());
    }

    [Fact]
    public void FactoryExpiredWindowsAreZeroedInsteadOfShowingTheLastWindow()
    {
        // Live /billing/limits shape (2026-07-22): Factory's rolling windows
        // start on first use, and once one lapses the API keeps returning the
        // COMPLETED window — windowEnd in the past, secondsRemaining null —
        // with its old usedPercent. Factory's dashboard shows those as 0%.
        using var document = JsonDocument.Parse(
            """
            {
              "usesTokenRateLimitsBilling": true,
              "limits": {
                "standard": {
                  "fiveHour": {"usedPercent": 22, "windowEnd": "2026-07-10T19:05:25.607Z", "secondsRemaining": null},
                  "weekly": {"usedPercent": 92, "windowEnd": "2026-07-12T10:14:42.814Z", "secondsRemaining": null},
                  "monthly": {"usedPercent": 38, "windowEnd": "2026-08-08T10:58:07.351Z", "secondsRemaining": 1493810}
                }
              },
              "extraUsageBalanceCents": 0
            }
            """
        );
        var strategy = new DroidApiLimitStrategy(new HttpClient(), new FixedClock());
        var result = strategy.BuildSnapshot(
            new AccountKey(new ProviderId("droid"), "default"),
            document.RootElement,
            billingLimits: true,
            started: 0L
        );
        Assert.True(result.IsSuccess);
        var meters = result.Snapshot!.Meters;

        var fiveHour = Assert.Single(meters, meter => meter.DisplayName == "5-hour limit");
        Assert.Equal(0.0, fiveHour.UsedPercent);
        Assert.Null(fiveHour.ResetsAt);

        var weekly = Assert.Single(meters, meter => meter.DisplayName == "Weekly limit");
        Assert.Equal(0.0, weekly.UsedPercent);
        Assert.Null(weekly.ResetsAt);

        var monthly = Assert.Single(meters, meter => meter.DisplayName == "Monthly limit");
        Assert.Equal(38.0, monthly.UsedPercent);
        Assert.Equal(new DateTimeOffset(2026, 8, 8, 10, 58, 7, 351, TimeSpan.Zero), monthly.ResetsAt);
    }

    [Fact]
    public void ClaudeScopedFableLimitIsParsed()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "five_hour": {"utilization": 23.0, "resets_at": "2026-07-13T21:50:00+00:00"},
              "seven_day": {"utilization": 23.0, "resets_at": "2026-07-14T23:00:00+00:00"},
              "limits": [
                {"kind": "session", "group": "session", "percent": 23, "resets_at": "2026-07-13T21:50:00+00:00", "scope": null, "is_active": false},
                {"kind": "weekly_all", "group": "weekly", "percent": 23, "resets_at": "2026-07-14T23:00:00+00:00", "scope": null, "is_active": false},
                {"kind": "weekly_scoped", "group": "weekly", "percent": 31, "resets_at": "2026-07-14T23:00:00+00:00",
                 "scope": {"model": {"id": null, "display_name": "Fable"}, "surface": null}, "is_active": true}
              ]
            }
            """
        );
        var strategy = new ClaudeOAuthLimitStrategy(
            new HttpClient(),
            new FixedClock(),
            Account("claude"),
            "unused.json"
        );
        var meters = new List<UsageMeter>();
        strategy.AddScopedLimitMeters(document.RootElement, Now, meters);
        var fable = Assert.Single(meters);
        Assert.Equal("Fable weekly limit", fable.DisplayName);
        Assert.Equal(31.0, fable.UsedPercent);
        Assert.Equal(TimeSpan.FromDays(7), fable.WindowDuration);
        Assert.Equal(new DateTimeOffset(2026, 7, 14, 23, 0, 0, TimeSpan.Zero), fable.ResetsAt);
        Assert.Equal(MeterScope.Model, fable.Scope);
    }

    // Claude's oauth/usage utilization is a 0..100 percent. A real 1% reading
    // right after a window reset must stay 1% — the generic "utilization in
    // [0,1] is a fraction" heuristic inflated it to 100% (the startup
    // "5-hour limit exhausted" false alarm).
    [Fact]
    public void ClaudeLowUtilizationIsNotRescaledToOneHundredPercent()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "five_hour": {"utilization": 1.0, "resets_at": "2026-07-13T21:50:00+00:00"},
              "seven_day": {"utilization": 0.5, "resets_at": "2026-07-14T23:00:00+00:00"}
            }
            """
        );
        var aliases = new Dictionary<string, MeterAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["five_hour"] = new MeterAlias("five_hour", "5-hour limit", PercentIsAbsolute: true),
            ["seven_day"] = new MeterAlias("seven_day", "Weekly limit", PercentIsAbsolute: true),
        };
        var meters = new DynamicMeterExtractor().Extract(
            new ProviderId("claude"),
            document.RootElement,
            "claude.oauth-usage",
            Now,
            authoritative: true,
            aliases
        );

        var fiveHour = Assert.Single(meters, meter => meter.DisplayName == "5-hour limit");
        Assert.Equal(1.0, fiveHour.UsedPercent);
        Assert.Equal(MeterStatus.Healthy, fiveHour.Status);

        var weekly = Assert.Single(meters, meter => meter.DisplayName == "Weekly limit");
        Assert.Equal(0.5, weekly.UsedPercent);
        Assert.Equal(MeterStatus.Healthy, weekly.Status);
    }

    // Without an alias pinning the scale, the fraction heuristic keeps working
    // for providers that really do send 0..1 utilization.
    [Fact]
    public void UnaliasedFractionalUtilizationStillRescales()
    {
        using var document = JsonDocument.Parse(
            """
            {"some_window": {"utilization": 0.5, "resets_at": "2026-07-13T21:50:00+00:00"}}
            """
        );
        var meters = new DynamicMeterExtractor().Extract(
            new ProviderId("fixture"),
            document.RootElement,
            "fixture",
            Now,
            authoritative: true
        );
        var meter = Assert.Single(meters);
        Assert.Equal(50.0, meter.UsedPercent);
    }
}
