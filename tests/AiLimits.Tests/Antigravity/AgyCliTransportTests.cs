// SPDX-License-Identifier: Apache-2.0
using System.Net;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Antigravity;

namespace AiLimits.Tests;

public sealed class AgyCliLocatorTests
{
    [Fact]
    public void ExplicitOverrideWinsOverEverythingElse()
    {
        AgyCliLocator locator = new(
            name => name == AgyCliLocator.PathOverrideVariable ? @"D:\custom\agy.exe" : null,
            path => path == @"D:\custom\agy.exe",
            _ => @"C:\on-path\agy.exe"
        );

        Assert.Equal(@"D:\custom\agy.exe", locator.Resolve());
    }

    [Fact]
    public void OverridePointingAtNothingIsAConfigurationErrorNotAFallback()
    {
        // Silently searching on would hide the typo and read a different install
        // than the one the user asked for.
        AgyCliLocator locator = new(
            name => name == AgyCliLocator.PathOverrideVariable ? @"D:\typo\agy.exe" : null,
            _ => false,
            _ => @"C:\on-path\agy.exe"
        );

        Assert.Null(locator.Resolve());
    }

    [Fact]
    public void PathIsPreferredOverTheInstallerLocation()
    {
        AgyCliLocator locator = new(
            name => name == "LOCALAPPDATA" ? @"C:\Users\dev\AppData\Local" : null,
            _ => true,
            _ => @"C:\on-path\agy.exe"
        );

        Assert.Equal(@"C:\on-path\agy.exe", locator.Resolve());
    }

    [Fact]
    public void InstallerLocationIsFoundWhenTheCliIsNotOnPath()
    {
        // agy only joins PATH after `agy install`, so a perfectly usable install
        // is invisible to a PATH-only lookup.
        string installed = Path.Combine(@"C:\Users\dev\AppData\Local", "agy", "bin", "agy.exe");
        AgyCliLocator locator = new(
            name => name == "LOCALAPPDATA" ? @"C:\Users\dev\AppData\Local" : null,
            path => path == installed,
            _ => null
        );

        Assert.Equal(installed, locator.Resolve());
    }

    [Fact]
    public void NoInstallAnywhereResolvesToNothing()
    {
        AgyCliLocator locator = new(_ => null, _ => false, _ => null);

        Assert.Null(locator.Resolve());
    }
}

public sealed class AgyCliTransportTests
{
    private static readonly ProviderAccount Account = new(
        new AccountKey(new ProviderId("antigravity"), "default"),
        "Google Antigravity",
        null,
        "Antigravity CLI",
        1,
        true
    );

    [Fact]
    public async Task PlaintextPortIsReadWhenTlsIsNotWhatItServes()
    {
        // agy binds two ports and serves TLS on one and plaintext on the other,
        // with nothing to say which is which before connecting.
        using HttpClient client = new(new SchemeSensitiveHandler(servedScheme: "http"));
        AgyLimitStrategy strategy = new(new StubClock(), AgyTestSession.Listening(4800), client);

        FetchResult result = await strategy.FetchAsync(Account, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { "Gemini weekly", "Gemini 5-hour" },
            result.Snapshot!.Meters.Select(meter => meter.DisplayName)
        );
    }

    [Fact]
    public async Task CreditBalancesAreReportedAlongsideTheRateWindows()
    {
        using HttpClient client = new(new SchemeSensitiveHandler(servedScheme: "https"));
        AgyLimitStrategy strategy = new(new StubClock(), AgyTestSession.Listening(4810), client);

        FetchResult result = await strategy.FetchAsync(Account, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { "Prompt", "Flow" }, result.Snapshot!.Balances.Select(balance => balance.DisplayName));
        Assert.Equal(500m, result.Snapshot.Balances[0].Value);
        // A verbatim FormattedValue would render as a bare "500" on the card.
        Assert.Null(result.Snapshot.Balances[0].FormattedValue);
        Assert.Equal(MeterUnit.Credits, result.Snapshot.Balances[0].Unit);
        Assert.Equal("person@example.com", result.Snapshot.Extensions["email"].GetString());
    }

    [Fact]
    public async Task PerModelQuotaCarriesTheSnapshotWhenTheSummaryEndpointIsMissing()
    {
        // Older CLI builds answer GetUserStatus but 501 the quota summary.
        using HttpClient client = new(new SummaryUnavailableHandler());
        AgyLimitStrategy strategy = new(new StubClock(), AgyTestSession.Listening(4820), client);

        FetchResult result = await strategy.FetchAsync(Account, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(
            new[] { "Claude/GPT 5-hour", "Gemini 5-hour" },
            result.Snapshot!.Meters.Select(meter => meter.DisplayName)
        );
        // Each family reports its most-consumed model, because Antigravity bills
        // every model in a family against one shared window.
        Assert.Equal(new double?[] { 40, 75 }, result.Snapshot.Meters.Select(meter => meter.UsedPercent));
    }

    [Fact]
    public async Task UnreachableCliFailsOverToTheNextStrategy()
    {
        using HttpClient client = new(new UnreachableHandler());
        AgyLimitStrategy strategy = new(new StubClock(), AgyTestSession.Listening(4830), client);

        FetchResult result = await strategy.FetchAsync(Account, default);

        Assert.False(result.IsSuccess);
        Assert.Equal(FallbackPolicy.TryNextStrategy, result.FallbackPolicy);
    }

    private const string QuotaSummaryJson = """
        {
          "response": {
            "groups": [{
              "displayName": "Gemini Models",
              "buckets": [
                { "bucketId": "gemini-weekly", "displayName": "Weekly Limit Remaining", "window": "weekly", "remainingFraction": 0.8 },
                { "bucketId": "gemini-5h", "displayName": "Five Hour Limit Remaining", "window": "5h", "remainingFraction": 0.5 }
              ]
            }]
          }
        }
        """;

    private const string UserStatusJson = """
        {
          "userStatus": {
            "email": "person@example.com",
            "planStatus": {
              "planInfo": { "planName": "Pro" },
              "availablePromptCredits": 500,
              "availableFlowCredits": 100
            },
            "cascadeModelConfigData": {
              "clientModelConfigs": [
                { "modelId": "gemini-pro-agent", "label": "Gemini 3.1 Pro", "quotaInfo": { "remainingFraction": 0.25 } },
                { "modelId": "gemini-3-flash-agent", "label": "Gemini Flash", "quotaInfo": { "remainingFraction": 0.9 } },
                { "modelId": "claude-opus-4-6", "label": "Claude Opus", "quotaInfo": { "remainingFraction": 0.6 } }
              ]
            }
          }
        }
        """;

    /// <summary>Answers on one scheme only, the way each agy port does.</summary>
    private sealed class SchemeSensitiveHandler(string servedScheme) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.Scheme != servedScheme)
            {
                throw new HttpRequestException("wrong scheme for this port");
            }
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        request.RequestUri.AbsolutePath.EndsWith("GetUserStatus", StringComparison.Ordinal)
                            ? UserStatusJson
                            : QuotaSummaryJson
                    ),
                }
            );
        }
    }

    private sealed class SummaryUnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("RetrieveUserQuotaSummary", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotImplemented));
            }
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(UserStatusJson) }
            );
        }
    }

    private sealed class UnreachableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("connection refused");
    }

    private sealed class StubClock : IClock
    {
        public DateTimeOffset UtcNow => new(2026, 8, 25, 12, 0, 0, TimeSpan.Zero);
    }
}
