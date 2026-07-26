// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Net.Http;
using System.Text;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

public sealed class ProviderHttpTests
{
    private static HttpRequestMessage Request() => new(HttpMethod.Get, "https://provider.example/usage");

    private static Task<ProviderJsonResult> SendAsync(HttpMessageHandler handler, CancellationToken cancellationToken = default, ProviderHttpOptions? options = null)
    {
        var client = new HttpClient(handler);
        using var request = Request();
        return ProviderHttp.GetJsonAsync(client, request, "test.strategy", "TestProvider",
            System.Diagnostics.Stopwatch.GetTimestamp(), cancellationToken, options);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FetchFailureKind.Authentication, FallbackPolicy.TryNextStrategy)]
    [InlineData(HttpStatusCode.Forbidden, FetchFailureKind.Authorization, FallbackPolicy.TryNextStrategy)]
    [InlineData(HttpStatusCode.TooManyRequests, FetchFailureKind.RateLimited, FallbackPolicy.Stop)]
    [InlineData(HttpStatusCode.InternalServerError, FetchFailureKind.Network, FallbackPolicy.TryNextStrategy)]
    [InlineData(HttpStatusCode.BadGateway, FetchFailureKind.Network, FallbackPolicy.TryNextStrategy)]
    [InlineData(HttpStatusCode.NotFound, FetchFailureKind.Network, FallbackPolicy.TryNextStrategy)]
    public async Task StatusCodesClassifyConsistently(HttpStatusCode status, FetchFailureKind expectedKind, FallbackPolicy expectedFallback)
    {
        using var result = await SendAsync(new ScriptedHandler(_ => new HttpResponseMessage(status)));

        Assert.False(result.IsSuccess);
        Assert.Equal(expectedKind, result.Failure!.FailureKind);
        Assert.Equal(expectedFallback, result.Failure.FallbackPolicy);
        Assert.Equal("test.strategy", result.Failure.StrategyId);
    }

    [Fact]
    public async Task RetryAfterDeltaSecondsBecomesTypedResult()
    {
        using var result = await SendAsync(new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", "120");
            return response;
        }));

        Assert.Equal(TimeSpan.FromSeconds(120), result.Failure!.RetryAfter);
    }

    [Fact]
    public async Task RetryAfterHttpDateBecomesPositiveDelay()
    {
        using var result = await SendAsync(new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            response.Headers.Add("Retry-After", DateTimeOffset.UtcNow.AddMinutes(5).ToString("R"));
            return response;
        }));

        Assert.NotNull(result.Failure!.RetryAfter);
        Assert.InRange(result.Failure.RetryAfter!.Value, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(6));
    }

    [Fact]
    public async Task ServerErrorRetryAfterIsAlsoCaptured()
    {
        using var result = await SendAsync(new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.Add("Retry-After", "30");
            return response;
        }));

        Assert.Equal(FetchFailureKind.Network, result.Failure!.FailureKind);
        Assert.Equal(TimeSpan.FromSeconds(30), result.Failure.RetryAfter);
    }

    [Fact]
    public async Task ConnectionFailureClassifiesAsNetwork()
    {
        using var result = await SendAsync(new ScriptedHandler(_ => throw new HttpRequestException("unreachable")));

        Assert.Equal(FetchFailureKind.Network, result.Failure!.FailureKind);
    }

    [Fact]
    public async Task InternalTimeoutClassifiesAsTimeoutNotCancellation()
    {
        var handler = new StallingHandler();
        using var result = await SendAsync(handler, options: new ProviderHttpOptions(Timeout: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(FetchFailureKind.Timeout, result.Failure!.FailureKind);
    }

    [Fact]
    public async Task CallerCancellationPropagatesAsException()
    {
        using var cancelled = new CancellationTokenSource();
        var handler = new StallingHandler(onStarted: cancelled.Cancel);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => SendAsync(handler, cancelled.Token));
    }

    [Fact]
    public async Task OversizedResponseIsRejectedWithoutBuffering()
    {
        string oversized = "{\"data\":\"" + new string('x', 4096) + "\"}";
        using var result = await SendAsync(
            new ScriptedHandler(_ => Json(oversized)),
            options: new ProviderHttpOptions(MaxResponseBytes: 1024));

        Assert.Equal(FetchFailureKind.OversizedResponse, result.Failure!.FailureKind);
    }

    [Fact]
    public async Task MalformedJsonClassifiesAsMalformedResponse()
    {
        using var result = await SendAsync(new ScriptedHandler(_ => Json("{not json")));

        Assert.Equal(FetchFailureKind.MalformedResponse, result.Failure!.FailureKind);
        Assert.Contains("TestProvider", result.Failure.SafeMessage);
    }

    [Fact]
    public async Task SuccessfulExchangeYieldsParsedDocument()
    {
        using var result = await SendAsync(new ScriptedHandler(_ => Json("{\"value\":41}")));

        Assert.True(result.IsSuccess);
        Assert.Equal(41, result.Document!.RootElement.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task FailureMessagesNeverEchoResponseBodies()
    {
        using var result = await SendAsync(new ScriptedHandler(_ =>
        {
            var response = new HttpResponseMessage(HttpStatusCode.Unauthorized)
            {
                Content = new StringContent("{\"secret\":\"Bearer sk-canary-12345\"}", Encoding.UTF8, "application/json")
            };
            return response;
        }));

        Assert.DoesNotContain("canary", result.Failure!.SafeMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ReadBoundedContentAsyncReturnsNullForOversizedContent()
    {
        var content = new StringContent(new string('x', 4096));
        byte[]? result = await ProviderHttp.ReadBoundedContentAsync(content, 1024, default);
        Assert.Null(result);
    }

    [Fact]
    public async Task ReadBoundedContentAsyncReturnsBytesForValidContent()
    {
        var content = new StringContent("hello");
        byte[]? result = await ProviderHttp.ReadBoundedContentAsync(content, 1024, default);
        Assert.NotNull(result);
        Assert.Equal("hello", Encoding.UTF8.GetString(result!));
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    private sealed class StallingHandler(Action? onStarted = null) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            onStarted?.Invoke();
            await Task.Delay(System.Threading.Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }
    }
}
