// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Providers.Common;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Copilot;

public sealed class GitHubDeviceFlowClient(HttpClient httpClient, string clientId)
{
    private static readonly Uri DeviceCodeUri = new Uri("https://github.com/login/device/code");

    private static readonly Uri AccessTokenUri = new Uri("https://github.com/login/oauth/access_token");

    public async Task<DeviceAuthorization> StartAsync(CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId, "clientId");
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, DeviceCodeUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "read:user read:org"
        });
        using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        byte[]? body = await ProviderHttp.ReadBoundedContentAsync(response.Content, ProviderHttp.DefaultMaxResponseBytes, cancellationToken).ConfigureAwait(false);
        if (body == null)
        {
            throw new InvalidOperationException("GitHub device-flow response was unexpectedly large.");
        }
        using JsonDocument document = JsonDocument.Parse(body);
        JsonElement root = document.RootElement;
        // GitHub can return HTTP 200 with an {"error": ...} payload; EnsureSuccessStatusCode
        // does not protect this path, so guard each required field explicitly.
        if (!root.TryGetProperty("device_code", out var deviceCodeEl) ||
            !root.TryGetProperty("user_code", out var userCodeEl) ||
            !root.TryGetProperty("verification_uri", out var verificationUriEl) ||
            !root.TryGetProperty("expires_in", out var expiresInEl))
        {
            string? error = root.TryGetProperty("error", out var errorEl) && errorEl.ValueKind == JsonValueKind.String
                ? errorEl.GetString()
                : null;
            throw new InvalidOperationException(
                "GitHub device-flow response was missing a required field" +
                (error is not null ? $": {error}" : "."));
        }
        int expires = expiresInEl.GetInt32();
        JsonElement interval;
        return new DeviceAuthorization(deviceCodeEl.GetString(), userCodeEl.GetString(), new Uri(verificationUriEl.GetString()), TimeSpan.FromSeconds(root.TryGetProperty("interval", out interval) ? interval.GetInt32() : 5), DateTimeOffset.UtcNow.AddSeconds(expires));
    }

    public async Task<string?> PollAsync(DeviceAuthorization authorization, CancellationToken cancellationToken)
    {
        TimeSpan delay = authorization.PollInterval;
        while (DateTimeOffset.UtcNow < authorization.ExpiresAt)
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            JsonElement token;
            JsonElement errorValue;
            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, AccessTokenUri))
            {
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId,
                    ["device_code"] = authorization.DeviceCode,
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
                });
                using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                byte[]? body = await ProviderHttp.ReadBoundedContentAsync(response.Content, ProviderHttp.DefaultMaxResponseBytes, cancellationToken).ConfigureAwait(false);
                if (body == null)
                {
                    return null;
                }
                using JsonDocument document = JsonDocument.Parse(body);
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("access_token", out token))
                {
                    return token.GetString();
                }
                bool flag;
                switch (root.TryGetProperty("error", out errorValue) ? errorValue.GetString() : null)
                {
                case "slow_down":
                    delay += TimeSpan.FromSeconds(5L);
                    goto end_IL_02a4;
                case "authorization_pending":
                case null:
                    flag = true;
                    break;
                default:
                    flag = false;
                    break;
                }
                if (!flag)
                {
                    return null;
                }
                end_IL_02a4:;
            }
            token = default(JsonElement);
            errorValue = default(JsonElement);
        }
        return null;
    }
}

public sealed record DeviceAuthorization(string DeviceCode, string UserCode, Uri VerificationUri, TimeSpan PollInterval, DateTimeOffset ExpiresAt);
