// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using AiLimits.Infrastructure.Providers;
using AiLimits.Infrastructure.Providers.Statuspage;
using Microsoft.Extensions.Logging;

namespace AiLimits.Tests;

public sealed class ProviderStatusClientTests
{
    [Fact]
    public async Task Polls_each_provider_once_and_returns_operational_and_incident_states()
    {
        var handler = new StatusHandler();
        using var httpClient = new HttpClient(handler);
        var endpoints = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new("https://status.test/codex"),
            ["claude"] = new("https://status.test/claude"),
            ["droid"] = new("https://status.test/droid"),
            ["amp"] = new("https://status.test/amp"),
            ["copilot"] = new("https://status.test/copilot"),
            ["cursor"] = new("https://status.test/cursor"),
        };
        var client = new ProviderStatusClient(httpClient, endpoints);

        IReadOnlyDictionary<string, ProviderServiceStatus> result = await client.PollAsync(default);

        Assert.Equal(6, handler.Requests.Count);
        Assert.Equal(6, handler.Requests.Distinct().Count());
        Assert.True(result["codex"].IsOperational);
        Assert.False(result["claude"].IsOperational);
        Assert.Equal("Partial System Outage", result["claude"].Description);
    }

    [Fact]
    public void Default_endpoints_cover_each_vendor_operated_status_page()
    {
        var expected = new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new("https://status.openai.com/api/v2/status.json"),
            ["claude"] = new("https://anthropic.statuspage.io/api/v2/status.json"),
            ["droid"] = new("https://status.factory.ai/api/v2/status.json"),
            ["amp"] = new("https://ampcodestatus.com/api/v2/status.json"),
            ["cursor"] = new("https://status.cursor.com/api/v2/status.json"),
        };

        Assert.Equal(expected.Count, ProviderStatusClient.DefaultEndpoints.Count);
        foreach (KeyValuePair<string, Uri> endpoint in expected)
        {
            Assert.Equal(endpoint.Value, ProviderStatusClient.DefaultEndpoints[endpoint.Key]);
        }

        HashSet<string> knownProviderIds = BuiltInProviderDescriptors
            .All.Select(provider => provider.Id.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        Assert.All(
            ProviderStatusClient.DefaultEndpoints.Keys,
            providerId => Assert.Contains(providerId, knownProviderIds)
        );
    }

    [Fact]
    public async Task A_failed_status_page_is_logged_and_does_not_fail_the_refresh()
    {
        var handler = new StatusHandler { FailingProvider = "droid" };
        using var httpClient = new HttpClient(handler);
        var logger = new CapturingLogger<ProviderStatusClient>();
        var endpoints = new Dictionary<string, Uri>
        {
            ["codex"] = new("https://status.test/codex"),
            ["droid"] = new("https://status.test/droid"),
        };

        IReadOnlyDictionary<string, ProviderServiceStatus> result = await new ProviderStatusClient(
            httpClient,
            endpoints,
            logger
        ).PollAsync(default);

        Assert.True(result.ContainsKey("codex"));
        Assert.False(result.ContainsKey("droid"));
        (LogLevel level, string message, Exception? exception) = Assert.Single(logger.Warnings);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("droid", message, StringComparison.OrdinalIgnoreCase);
        Assert.IsType<HttpRequestException>(exception);
    }

    [Fact]
    public async Task Oversized_response_is_dropped_without_throwing()
    {
        var handler = new OversizedHandler();
        using var httpClient = new HttpClient(handler);
        var logger = new CapturingLogger<ProviderStatusClient>();
        var endpoints = new Dictionary<string, Uri> { ["codex"] = new("https://status.test/codex") };

        IReadOnlyDictionary<string, ProviderServiceStatus> result = await new ProviderStatusClient(
            httpClient,
            endpoints,
            logger
        ).PollAsync(default);

        Assert.Empty(result);
        (LogLevel level, string message, Exception? exception) = Assert.Single(logger.Warnings);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("codex", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception);
    }

    [Fact]
    public async Task Incomplete_response_is_logged_and_dropped()
    {
        var handler = new StatusHandler { IncompleteProvider = "cursor" };
        using var httpClient = new HttpClient(handler);
        var logger = new CapturingLogger<ProviderStatusClient>();
        var endpoints = new Dictionary<string, Uri> { ["cursor"] = new("https://status.test/cursor") };

        IReadOnlyDictionary<string, ProviderServiceStatus> result = await new ProviderStatusClient(
            httpClient,
            endpoints,
            logger
        ).PollAsync(default);

        Assert.Empty(result);
        (LogLevel level, string message, Exception? exception) = Assert.Single(logger.Warnings);
        Assert.Equal(LogLevel.Warning, level);
        Assert.Contains("cursor", message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(exception);
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public string? FailingProvider { get; init; }
        public string? IncompleteProvider { get; init; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string provider = request.RequestUri!.Segments.Last().Trim('/');
            lock (Requests)
            {
                Requests.Add(provider);
            }

            if (string.Equals(provider, FailingProvider, StringComparison.Ordinal))
            {
                throw new HttpRequestException("offline");
            }

            bool incident = provider == "claude";
            string payload =
                string.Equals(provider, IncompleteProvider, StringComparison.Ordinal)
                    ? """{"status":{"indicator":"none","description":""}}"""
                : incident ? """{"status":{"indicator":"minor","description":"Partial System Outage"}}"""
                : """{"status":{"indicator":"none","description":"All Systems Operational"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) });
        }
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            string oversized =
                "{\"status\":{\"indicator\":\"none\",\"description\":\"" + new string('x', 3_000_000) + "\"}}";
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(oversized, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel < LogLevel.Warning)
            {
                return;
            }

            lock (Warnings)
            {
                Warnings.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }
}
