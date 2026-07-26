// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using AiLimits.Infrastructure.Providers.Statuspage;

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
            ["copilot"] = new("https://status.test/copilot")
        };
        var client = new ProviderStatusClient(httpClient, endpoints);

        IReadOnlyDictionary<string, ProviderServiceStatus> result =
            await client.PollAsync(default);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal(4, handler.Requests.Distinct().Count());
        Assert.True(result["codex"].IsOperational);
        Assert.False(result["claude"].IsOperational);
        Assert.Equal("Partial System Outage", result["claude"].Description);
    }

    [Fact]
    public async Task A_failed_status_page_does_not_fail_the_refresh()
    {
        var handler = new StatusHandler { FailingProvider = "droid" };
        using var httpClient = new HttpClient(handler);
        var endpoints = new Dictionary<string, Uri>
        {
            ["codex"] = new("https://status.test/codex"),
            ["droid"] = new("https://status.test/droid")
        };

        IReadOnlyDictionary<string, ProviderServiceStatus> result =
            await new ProviderStatusClient(httpClient, endpoints).PollAsync(default);

        Assert.True(result.ContainsKey("codex"));
        Assert.False(result.ContainsKey("droid"));
    }

    [Fact]
    public async Task Oversized_response_is_dropped_without_throwing()
    {
        var handler = new OversizedHandler();
        using var httpClient = new HttpClient(handler);
        var endpoints = new Dictionary<string, Uri>
        {
            ["codex"] = new("https://status.test/codex")
        };

        IReadOnlyDictionary<string, ProviderServiceStatus> result =
            await new ProviderStatusClient(httpClient, endpoints).PollAsync(default);

        Assert.Empty(result);
    }

    private sealed class StatusHandler : HttpMessageHandler
    {
        public string? FailingProvider { get; init; }
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
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
            string payload = incident
                ? """{"status":{"indicator":"minor","description":"Partial System Outage"}}"""
                : """{"status":{"indicator":"none","description":"All Systems Operational"}}""";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload)
            });
        }
    }

    private sealed class OversizedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string oversized = "{\"status\":{\"indicator\":\"none\",\"description\":\"" + new string('x', 3_000_000) + "\"}}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(oversized, Encoding.UTF8, "application/json")
            });
        }
    }
}
