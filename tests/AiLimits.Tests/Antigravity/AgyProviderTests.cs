// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Net;
using System.Globalization;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Antigravity;

namespace AiLimits.Tests;

public sealed class AgyProviderTests
{
    [Fact]
    public void QuotaSummaryMapsGoogleSubscriptionWindowsAndIdentity()
    {
        using JsonDocument quota = JsonDocument.Parse("""
        {
          "response": {
            "groups": [
              {
                "displayName": "Gemini Models",
                "buckets": [
                  {
                    "bucketId": "gemini-5h",
                    "displayName": "Session Limit",
                    "remainingFraction": 0.75,
                    "resetTime": "2026-07-18T12:00:00Z"
                  },
                  {
                    "bucketId": "gemini-weekly",
                    "displayName": "Weekly Limit",
                    "remaining": { "case": "remainingFraction", "value": 0.4 }
                  }
                ]
              },
              {
                "displayName": "Claude and GPT models",
                "buckets": [
                  {
                    "bucketId": "3p-session",
                    "displayName": "Session",
                    "remaining": { "remainingFraction": 0.9 }
                  }
                ]
              }
            ]
          }
        }
        """);
        using JsonDocument identity = JsonDocument.Parse("""
        {
          "userStatus": {
            "email": "person@example.com",
            "userTier": { "name": "Google AI Ultra" }
          }
        }
        """);
        DateTimeOffset now = new(2026, 7, 18, 10, 0, 0, TimeSpan.Zero);
        AgyLimitStrategy strategy = new(new FixedClock(now), new AgyProcessDiscovery());

        ProviderSnapshot snapshot = strategy.BuildSnapshot(
            new AccountKey(new ProviderId("antigravity"), "default"),
            quota.RootElement,
            identity.RootElement,
            now);

        Assert.Equal(
            new[] { "Gemini 5-hour", "Gemini weekly", "Claude/GPT 5-hour" },
            snapshot.Meters.Select(meter => meter.DisplayName));
        Assert.Equal(new double?[] { 25, 60, 10 }, snapshot.Meters.Select(meter => meter.UsedPercent));
        Assert.Equal(TimeSpan.FromHours(5), snapshot.Meters[0].WindowDuration);
        Assert.Equal(TimeSpan.FromDays(7), snapshot.Meters[1].WindowDuration);
        Assert.Equal(DateTimeOffset.Parse("2026-07-18T12:00:00Z"), snapshot.Meters[0].ResetsAt);
        Assert.Equal("person@example.com", snapshot.Extensions["email"].GetString());
        Assert.Equal("Google AI Ultra", snapshot.Extensions["plan_type"].GetString());
        Assert.All(snapshot.Meters, meter => Assert.True(meter.Provenance.IsAuthoritative));
    }

    [Fact]
    public void DisabledAndUnknownQuotaBucketsAreNotReportedAsExhausted()
    {
        using JsonDocument quota = JsonDocument.Parse("""
        {
          "groups": [
            {
              "displayName": "Gemini Models",
              "buckets": [
                { "bucketId": "disabled", "remainingFraction": 0, "disabled": true },
                { "bucketId": "unknown", "displayName": "Unknown" },
                { "bucketId": "weekly", "displayName": "Weekly", "remainingFraction": 0 }
              ]
            }
          ]
        }
        """);
        AgyLimitStrategy strategy = new(new FixedClock(), new AgyProcessDiscovery());

        ProviderSnapshot snapshot = strategy.BuildSnapshot(
            new AccountKey(new ProviderId("antigravity"), "default"),
            quota.RootElement,
            null,
            DateTimeOffset.UtcNow);

        UsageMeter meter = Assert.Single(snapshot.Meters);
        Assert.Equal("Gemini weekly", meter.DisplayName);
        Assert.Equal(MeterStatus.Exhausted, meter.Status);
    }

    [Fact]
    public async Task OptionalIdentityTimeoutKeepsSuccessfulQuota()
    {
        using HttpClient client = new(new IdentityTimeoutHandler());
        AgyLimitStrategy strategy = new(new FixedClock(), () => new[] { 12345 }, client);
        ProviderAccount account = new(
            new AccountKey(new ProviderId("antigravity"), "default"),
            "Google Antigravity",
            null,
            "Existing agy session",
            1,
            true);

        FetchResult result = await strategy.FetchAsync(account, default);

        Assert.True(result.IsSuccess);
        Assert.Single(result.Snapshot!.Meters);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"userStatus\":null}")]
    [InlineData("{\"userStatus\":\"unknown\"}")]
    public void MalformedIdentityDoesNotDiscardValidQuota(string identityJson)
    {
        using JsonDocument quota = ValidQuota();
        using JsonDocument identity = JsonDocument.Parse(identityJson);
        AgyLimitStrategy strategy = new(new FixedClock(), new AgyProcessDiscovery());

        ProviderSnapshot snapshot = strategy.BuildSnapshot(
            new AccountKey(new ProviderId("antigravity"), "default"),
            quota.RootElement,
            identity.RootElement,
            DateTimeOffset.UtcNow);

        Assert.Single(snapshot.Meters);
        Assert.False(snapshot.Extensions.ContainsKey("email"));
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(1.1)]
    public void OutOfRangeRemainingFractionIsRejected(double remainingFraction)
    {
        string fraction = remainingFraction.ToString(CultureInfo.InvariantCulture);
        using JsonDocument quota = JsonDocument.Parse($$"""
        {
          "groups": [{
            "displayName": "Gemini Models",
            "buckets": [{
              "bucketId": "gemini-weekly",
              "remainingFraction": {{fraction}}
            }]
          }]
        }
        """);
        AgyLimitStrategy strategy = new(new FixedClock(), new AgyProcessDiscovery());

        Assert.Throws<JsonException>(() => strategy.BuildSnapshot(
            new AccountKey(new ProviderId("antigravity"), "default"),
            quota.RootElement,
            null,
            DateTimeOffset.UtcNow));
    }

    private static JsonDocument ValidQuota() => JsonDocument.Parse("""
    {
      "groups": [{
        "displayName": "Gemini Models",
        "buckets": [{
          "bucketId": "gemini-weekly",
          "displayName": "Weekly",
          "remainingFraction": 0.5
        }]
      }]
    }
    """);

    private sealed class IdentityTimeoutHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("GetUserStatus", StringComparison.Ordinal))
            {
                throw new TaskCanceledException("identity timeout");
            }

            HttpResponseMessage response = new(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                {
                  "response": {
                    "groups": [{
                      "displayName": "Gemini Models",
                      "buckets": [{
                        "bucketId": "gemini-weekly",
                        "displayName": "Weekly",
                        "remainingFraction": 0.5
                      }]
                    }]
                  }
                }
                """)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class FixedClock : IClock
    {
        public FixedClock(DateTimeOffset? utcNow = null)
        {
            UtcNow = utcNow ?? DateTimeOffset.UtcNow;
        }

        public DateTimeOffset UtcNow { get; }
    }
}
