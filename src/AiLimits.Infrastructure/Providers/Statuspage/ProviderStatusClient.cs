// SPDX-License-Identifier: Apache-2.0
using System.Net.Http.Headers;
using System.Text.Json;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Statuspage;

public sealed record ProviderServiceStatus(
    string ProviderId,
    string Indicator,
    string Description)
{
    public bool IsOperational =>
        string.Equals(Indicator, "none", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Indicator, "operational", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Polls the providers' public Atlassian Statuspage endpoints. Each refresh
/// performs one unauthenticated request per provider. Failed providers are
/// omitted so callers can retain their last known state.
/// </summary>
public sealed class ProviderStatusClient
{
    public static IReadOnlyDictionary<string, Uri> DefaultEndpoints { get; } =
        new Dictionary<string, Uri>(StringComparer.OrdinalIgnoreCase)
        {
            ["codex"] = new("https://status.openai.com/api/v2/status.json"),
            ["claude"] = new("https://anthropic.statuspage.io/api/v2/status.json"),
            ["droid"] = new("https://status.factory.ai/api/v2/status.json"),
            ["copilot"] = new("https://www.githubstatus.com/api/v2/status.json")
        };

    private readonly HttpClient _httpClient;
    private readonly IReadOnlyDictionary<string, Uri> _endpoints;

    public ProviderStatusClient(
        HttpClient httpClient,
        IReadOnlyDictionary<string, Uri>? endpoints = null)
    {
        _httpClient = httpClient;
        _endpoints = endpoints ?? DefaultEndpoints;
    }

    public async Task<IReadOnlyDictionary<string, ProviderServiceStatus>> PollAsync(
        CancellationToken cancellationToken)
    {
        Task<KeyValuePair<string, ProviderServiceStatus>?>[] requests = _endpoints
            .Select(endpoint => PollOneSafelyAsync(endpoint.Key, endpoint.Value, cancellationToken))
            .ToArray();
        KeyValuePair<string, ProviderServiceStatus>?[] results = await Task.WhenAll(requests)
            .ConfigureAwait(false);

        return results
            .Where(result => result.HasValue)
            .Select(result => result!.Value)
            .ToDictionary(result => result.Key, result => result.Value, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<KeyValuePair<string, ProviderServiceStatus>?> PollOneSafelyAsync(
        string providerId,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("QuotaBoard/1.0");
            using HttpResponseMessage response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            byte[]? body = await ProviderHttp.ReadBoundedContentAsync(
                response.Content, ProviderHttp.DefaultMaxResponseBytes, cancellationToken)
                .ConfigureAwait(false);
            if (body is null) return null;
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement status = document.RootElement.GetProperty("status");
            string indicator = status.GetProperty("indicator").GetString()?.Trim() ?? "";
            string description = status.GetProperty("description").GetString()?.Trim() ?? "";
            if (string.IsNullOrEmpty(indicator) || string.IsNullOrEmpty(description))
            {
                return null;
            }

            if (description.Length > 180)
            {
                description = description[..177] + "...";
            }

            return new KeyValuePair<string, ProviderServiceStatus>(
                providerId,
                new ProviderServiceStatus(providerId, indicator, description));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
