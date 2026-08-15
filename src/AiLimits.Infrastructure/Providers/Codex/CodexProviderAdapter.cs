// SPDX-License-Identifier: Apache-2.0
using System.Net.Http.Headers;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Codex;

public sealed class CodexProviderAdapter : IProviderAdapter, IResetCreditSource
{
    private static readonly Uri ResetCreditsUri = new Uri(
        "https://chatgpt.com/backend-api/wham/rate-limit-reset-credits"
    );

    private readonly HttpClient _httpClient;

    private readonly IClock _clock;

    private readonly string _codexHome;

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Codex;

    public CodexProviderAdapter(HttpClient httpClient, IClock clock, string? codexHome = null)
    {
        _httpClient = httpClient;
        _clock = clock;
        _codexHome =
            codexHome
            ?? Environment.GetEnvironmentVariable("CODEX_HOME")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");
    }

    public async Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        CliCredential credential = await CliCredentialReader
            .ReadCodexAsync(Path.Combine(_codexHome, "auth.json"), cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<ProviderAccount> result;
        if (credential is not null)
        {
            IReadOnlyList<ProviderAccount> readOnlyList = new ProviderAccount[]
            {
                new ProviderAccount(
                    new AccountKey(Descriptor.Id, credential.AccountId),
                    credential.Login ?? "Codex CLI account",
                    credential.Login,
                    "Codex CLI OAuth",
                    1L,
                    IsConnected: true
                ),
            };
            result = readOnlyList;
        }
        else
        {
            IReadOnlyList<ProviderAccount> readOnlyList = Array.Empty<ProviderAccount>();
            result = readOnlyList;
        }
        return result;
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account)
    {
        return new ILimitFetchStrategy[]
        {
            new CodexOAuthLimitStrategy(_httpClient, _clock, account, Path.Combine(_codexHome, "auth.json")),
        };
    }

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account)
    {
        return new ITokenUsageSource[] { new CodexSessionTokenSource(_codexHome) };
    }

    public async Task<ResetCreditInventory?> FetchResetCreditsAsync(
        ProviderAccount account,
        CancellationToken cancellationToken
    )
    {
        CliCredential credential = await CliCredentialReader
            .ReadCodexAsync(Path.Combine(_codexHome, "auth.json"), cancellationToken)
            .ConfigureAwait(false);
        if (credential is null || !string.Equals(credential.AccountId, account.Key.Value, StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, ResetCreditsUri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("AI-Limits-Windows/0.1");
            request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
            using HttpResponseMessage response = await _httpClient
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }
            await using Stream stream = await response
                .Content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            // Bound the response before parsing to avoid OOM on an oversized body,
            // matching ProviderHttp.DefaultMaxResponseBytes (2,000,000 bytes).
            const long maxResponseBytes = 2_000_000;
            using var buffer = new MemoryStream();
            var chunk = new byte[64 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(chunk, 0, read);
                if (buffer.Length > maxResponseBytes)
                    return null;
            }
            using JsonDocument document = JsonDocument.Parse(buffer.ToArray());
            return ParseResetCredits(document.RootElement, _clock.UtcNow);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Best-effort supplemental data: any failure simply yields no inventory.
            return null;
        }
    }

    private static ResetCreditInventory? ParseResetCredits(JsonElement root, DateTimeOffset observedAt)
    {
        if (
            !root.TryGetProperty("credits", out JsonElement creditsElement)
            || creditsElement.ValueKind != JsonValueKind.Array
        )
        {
            return null;
        }
        List<ResetCredit> credits = new List<ResetCredit>();
        foreach (JsonElement item in creditsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            credits.Add(
                new ResetCredit(
                    ReadString(item, "id") ?? $"credit-{credits.Count}",
                    ReadString(item, "reset_type") ?? "unknown",
                    ReadString(item, "status")?.ToLowerInvariant() switch
                    {
                        "available" => ResetCreditStatus.Available,
                        "redeeming" => ResetCreditStatus.Redeeming,
                        "redeemed" => ResetCreditStatus.Redeemed,
                        "expired" => ResetCreditStatus.Expired,
                        _ => ResetCreditStatus.Unknown,
                    },
                    ReadTimestamp(item, "granted_at"),
                    ReadTimestamp(item, "expires_at"),
                    ReadString(item, "title"),
                    ReadString(item, "description")
                )
            );
        }
        return new ResetCreditInventory(credits, observedAt);
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static DateTimeOffset? ReadTimestamp(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
        {
            return null;
        }
        if (
            value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out DateTimeOffset parsed)
        )
        {
            return parsed;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long unixSeconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(unixSeconds);
        }
        return null;
    }
}
