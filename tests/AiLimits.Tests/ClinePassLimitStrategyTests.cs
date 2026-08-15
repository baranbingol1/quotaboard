// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Cline;

namespace AiLimits.Tests;

[Collection("ClineEnv")]
public sealed class ClinePassLimitStrategyTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 16, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task All_three_windows_become_percent_meters()
    {
        var handler = new ScriptedHandler(_ =>
            Json(
                """
                {"success":true,"data":{"limits":[
                  {"type":"five_hour","percentUsed":12.5,"resetsAt":"2026-07-16T15:00:00Z"},
                  {"type":"weekly","percentUsed":80,"resetsAt":"2026-07-20T00:00:00Z"},
                  {"type":"monthly","percentUsed":96.2,"resetsAt":null}
                ]}}
                """
            )
        );
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(), TestCredential);

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        ProviderSnapshot snapshot = result.Snapshot!;
        Assert.Equal(SnapshotCompleteness.Authoritative, snapshot.Completeness);
        Assert.Equal(DataConfidence.High, snapshot.Confidence);
        Assert.Equal("api", snapshot.Extensions["source"].GetString());
        Assert.Equal(Now, snapshot.ObservedAt);
        Assert.Collection(
            snapshot.Meters,
            meter =>
            {
                Assert.Equal("cline:5h", meter.Key.Value);
                Assert.Equal("ClinePass 5-hour", meter.DisplayName);
                Assert.Equal(MeterScope.Account, meter.Scope);
                Assert.Equal(MeterUnit.Percent, meter.Unit);
                Assert.Equal(12.5m, meter.Used);
                Assert.Equal(100m, meter.Limit);
                Assert.Equal(12.5, meter.UsedPercent);
                Assert.Equal(TimeSpan.FromHours(5), meter.WindowDuration);
                Assert.Equal(new DateTimeOffset(2026, 7, 16, 15, 0, 0, TimeSpan.Zero), meter.ResetsAt);
                Assert.Equal(MeterStatus.Healthy, meter.Status);
                Assert.Equal("cline.pass-usage-limits-api", meter.Provenance.StrategyId);
                Assert.True(meter.Provenance.IsAuthoritative);
            },
            meter =>
            {
                Assert.Equal("cline:weekly", meter.Key.Value);
                Assert.Equal("ClinePass Weekly", meter.DisplayName);
                Assert.Equal(80m, meter.Used);
                Assert.Equal(80, meter.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(7), meter.WindowDuration);
                Assert.Equal(new DateTimeOffset(2026, 7, 20, 0, 0, 0, TimeSpan.Zero), meter.ResetsAt);
                Assert.Equal(MeterStatus.Approaching, meter.Status);
            },
            meter =>
            {
                Assert.Equal("cline:monthly", meter.Key.Value);
                Assert.Equal("ClinePass Monthly", meter.DisplayName);
                Assert.Equal(96.2m, meter.Used);
                Assert.Equal(96.2, meter.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(30), meter.WindowDuration);
                Assert.Null(meter.ResetsAt);
                Assert.Equal(MeterStatus.Critical, meter.Status);
            }
        );
        Assert.Equal("https://api.cline.bot/api/v1/users/me/plan/usage-limits", handler.RequestUri);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("test-token", handler.AuthorizationParameter);
        Assert.Equal("application/json", handler.Accept);
    }

    [Fact]
    public async Task Workos_session_tokens_ride_behind_the_workos_scheme_prefix()
    {
        var handler = new ScriptedHandler(_ =>
            Json(
                """
                {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":41,"resetsAt":null}]}}
                """
            )
        );
        var strategy = new ClinePassLimitStrategy(
            new HttpClient(handler),
            new FixedClock(),
            () => new ClineCredential("header.payload.signature", "Cline CLI account", IsWorkOsSession: true)
        );

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        Assert.Equal("Bearer", handler.AuthorizationScheme);
        Assert.Equal("workos:header.payload.signature", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task An_already_prefixed_session_token_is_not_prefixed_twice()
    {
        var handler = new ScriptedHandler(_ =>
            Json(
                """
                {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":41,"resetsAt":null}]}}
                """
            )
        );
        var strategy = new ClinePassLimitStrategy(
            new HttpClient(handler),
            new FixedClock(),
            () => new ClineCredential("workos:already", "Cline CLI account", IsWorkOsSession: true)
        );

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        Assert.Equal("workos:already", handler.AuthorizationParameter);
    }

    [Fact]
    public async Task Unknown_limit_types_are_tolerated_and_percents_clamped()
    {
        var handler = new ScriptedHandler(_ =>
            Json(
                """
                {"success":true,"data":{"limits":[
                  {"type":"experimental_pool","percentUsed":61,"resetsAt":null},
                  {"type":"five_hour","percentUsed":101.4,"resetsAt":null},
                  {"type":"monthly","percentUsed":-3,"resetsAt":null}
                ]}}
                """
            )
        );
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(), TestCredential);

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        Assert.Collection(
            result.Snapshot!.Meters,
            meter =>
            {
                Assert.Equal("cline:5h", meter.Key.Value);
                Assert.Equal(100, meter.UsedPercent);
                Assert.Equal(MeterStatus.Exhausted, meter.Status);
            },
            meter =>
            {
                Assert.Equal("cline:monthly", meter.Key.Value);
                Assert.Equal(0, meter.UsedPercent);
                Assert.Equal(MeterStatus.Healthy, meter.Status);
            }
        );
    }

    [Fact]
    public async Task Null_reset_timestamps_stay_null()
    {
        var handler = new ScriptedHandler(_ =>
            Json(
                """
                {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":42,"resetsAt":null}]}}
                """
            )
        );
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(), TestCredential);

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        UsageMeter meter = Assert.Single(result.Snapshot!.Meters);
        Assert.Equal("cline:weekly", meter.Key.Value);
        Assert.Null(meter.ResetsAt);
    }

    [Fact]
    public async Task Unauthorized_marks_the_credential_as_the_problem_without_echoing_it()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("{\"error\":\"bad token test-token\"}", Encoding.UTF8, "application/json"),
        });
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(), TestCredential);

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.Authentication, result.FailureKind);
        Assert.Equal(FallbackPolicy.TryNextStrategy, result.FallbackPolicy);
        Assert.Equal("cline.pass-usage-limits-api", result.StrategyId);
        Assert.DoesNotContain("test-token", result.SafeMessage);
    }

    [Theory]
    [InlineData("{\"success\":false,\"data\":{\"limits\":[]}}")]
    [InlineData("{\"success\":true,\"data\":{\"limits\":[{\"type\":\"experimental_pool\",\"percentUsed\":1}]}}")]
    [InlineData("{\"success\":true}")]
    [InlineData("not json")]
    public async Task Unusable_responses_are_malformed(string body)
    {
        var handler = new ScriptedHandler(_ => Json(body));
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(), TestCredential);

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.MalformedResponse, result.FailureKind);
        Assert.Equal(FallbackPolicy.TryNextStrategy, result.FallbackPolicy);
    }

    [Fact]
    public async Task Availability_reflects_whether_a_credential_exists()
    {
        var ready = new ClinePassLimitStrategy(new HttpClient(), new FixedClock(), TestCredential);
        var missing = new ClinePassLimitStrategy(new HttpClient(), new FixedClock(), () => null);

        Assert.Equal(
            StrategyAvailability.Available,
            (await ready.CheckAvailabilityAsync(Account(), default)).Availability
        );
        StrategyAvailabilityResult unavailable = await missing.CheckAvailabilityAsync(Account(), default);
        Assert.Equal(StrategyAvailability.NotConfigured, unavailable.Availability);
        Assert.Contains("CLINE_API_KEY", unavailable.SafeReason);

        FetchResult fetch = await missing.FetchAsync(Account(), default);
        Assert.Equal(FetchFailureKind.Authentication, fetch.FailureKind);
        Assert.Equal(FallbackPolicy.TryNextStrategy, fetch.FallbackPolicy);
    }

    [Fact]
    public async Task Environment_api_key_satisfies_the_default_credential_reader()
    {
        string? original = Environment.GetEnvironmentVariable("CLINE_API_KEY");
        Environment.SetEnvironmentVariable("CLINE_API_KEY", "  \"env-token\"  ");
        try
        {
            var handler = new ScriptedHandler(_ =>
                Json(
                    """
                    {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":1,"resetsAt":null}]}}
                    """
                )
            );
            var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock());

            Assert.Equal(
                StrategyAvailability.Available,
                (await strategy.CheckAvailabilityAsync(Account(), default)).Availability
            );
            FetchResult result = await strategy.FetchAsync(Account(), default);

            Assert.True(result.IsSuccess, result.SafeMessage);
            Assert.Equal("env-token", handler.AuthorizationParameter);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLINE_API_KEY", original);
        }
    }

    private static ClineCredential? TestCredential() => new("test-token", "API key (CLINE_API_KEY)");

    private static ProviderAccount Account() =>
        new(new AccountKey(new ProviderId("cline"), "default"), "Cline", null, "fixture", 1, true);

    private static HttpResponseMessage Json(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public string? RequestUri;
        public string? AuthorizationScheme;
        public string? AuthorizationParameter;
        public string? Accept;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            RequestUri = request.RequestUri?.ToString();
            AuthorizationScheme = request.Headers.Authorization?.Scheme;
            AuthorizationParameter = request.Headers.Authorization?.Parameter;
            Accept = request.Headers.Accept.ToString();
            return Task.FromResult(respond(request));
        }
    }
}
