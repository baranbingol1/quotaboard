// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Copilot;

internal sealed class CopilotQuotaStrategy(HttpClient httpClient, IClock clock) : ILimitFetchStrategy
{
    private static readonly Uri UsageUri = new Uri("https://api.github.com/copilot_internal/user");

    public string Id => "copilot.quota-entitlements";

    public int Order => 10;

    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        return Task.FromResult(string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AILIMITS_GITHUB_TOKEN")) ? new StrategyAvailabilityResult(StrategyAvailability.NotConfigured, "GitHub device authorization is not connected.") : StrategyAvailabilityResult.Ready());
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string token = Environment.GetEnvironmentVariable("AILIMITS_GITHUB_TOKEN")?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return FetchResult.Failure(FetchFailureKind.Authentication, "GitHub device authorization is not connected.", FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Editor-Version", "vscode/1.96.2");
        request.Headers.TryAddWithoutValidation("Editor-Plugin-Version", "copilot-chat/0.26.7");
        request.Headers.TryAddWithoutValidation("X-Github-Api-Version", "2025-04-01");
        request.Headers.UserAgent.ParseAdd("GitHubCopilotChat/0.26.7");
        using ProviderJsonResult exchange = await ProviderHttp.GetJsonAsync(httpClient, request, Id, "GitHub Copilot", started, cancellationToken).ConfigureAwait(false);
        if (!exchange.IsSuccess)
        {
            return exchange.Failure!;
        }
        JsonDocument document = exchange.Document!;
        IReadOnlyList<UsageMeter> meters = ParseMeters(account.Key.Provider, document.RootElement, clock.UtcNow);
        var extensionValues = new List<(string Key, string Value)> { ("source", "copilot-internal") };
        string? email = ReadString(document.RootElement, "email");
        string? login = ReadString(document.RootElement, "login") ?? ReadString(document.RootElement, "user_login");
        if (!string.IsNullOrWhiteSpace(email)) extensionValues.Add(("email", email.Trim()));
        if (!string.IsNullOrWhiteSpace(login)) extensionValues.Add(("login", login.Trim()));
        if (meters.Count == 0 && !ReadBool(document.RootElement, "token_based_billing"))
        {
            return FetchResult.Failure(FetchFailureKind.ProviderChanged,
                "GitHub returned no usable Copilot quota meters.",
                FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
        }

        // A token-based-billing account legitimately reports no quota windows,
        // but an empty *authoritative* snapshot tells the merger the previous
        // meters are gone for good, so the whole card emptied out to
        // "connected — no quota". Declaring it Partial keeps what was last
        // known, badged Stale, until GitHub reports windows again.
        SnapshotCompleteness completeness = meters.Count == 0
            ? SnapshotCompleteness.Partial
            : SnapshotCompleteness.Authoritative;

        return FetchResult.Success(
            new ProviderSnapshot(account.Key, meters, Array.Empty<BalanceMetric>(), completeness,
                clock.UtcNow, DataConfidence.High,
                ProviderHttpSupport.SafeExtensions(extensionValues.ToArray())),
            Id, Stopwatch.GetElapsedTime(started));
    }

    internal IReadOnlyList<UsageMeter> ParseMeters(ProviderId provider, JsonElement root, DateTimeOffset now)
    {
        if (!root.TryGetProperty("quota_snapshots", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<UsageMeter>();
        }
        DateTimeOffset? dateTimeOffset = ReadDate(root, "quota_reset_date");
        List<UsageMeter> list = new List<UsageMeter>();
        foreach (JsonProperty item in value.EnumerateObject())
        {
            if (item.Value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            decimal? num = ReadDecimal(item.Value, "entitlement");
            decimal? num2 = ReadDecimal(item.Value, "remaining");
            decimal? num3 = ReadDecimal(item.Value, "percent_remaining");
            bool flag = ReadBool(item.Value, "unlimited");
            if (!flag)
            {
                decimal? num4 = num;
                if ((num4.GetValueOrDefault() == default(decimal)) & num4.HasValue)
                {
                    num4 = num2;
                    if ((num4.GetValueOrDefault() == default(decimal)) & num4.HasValue)
                    {
                        continue;
                    }
                }
            }
            double? num5;
            if (!flag)
            {
                if (num3.HasValue)
                {
                    decimal valueOrDefault = num3.GetValueOrDefault();
                    num5 = (double)(100m - valueOrDefault);
                }
                else if (num.HasValue && num.GetValueOrDefault() > 0m && num2.HasValue)
                {
                    decimal valueOrDefault2 = num2.GetValueOrDefault();
                    num5 = (double)((num.Value - valueOrDefault2) / num.Value * 100m);
                }
                else
                {
                    num5 = null;
                }
            }
            else
            {
                num5 = 0.0;
            }
            double? num6 = num5;
            if (num6.HasValue)
            {
                num6 = Math.Max(0.0, num6.Value);
                string text = ReadString(item.Value, "quota_id") ?? item.Name;
                MeterStatus status = ((num6 >= 100.0) ? MeterStatus.Exhausted : ((num6 >= 95.0) ? MeterStatus.Critical : ((!(num6 >= 80.0)) ? MeterStatus.Healthy : MeterStatus.Approaching)));
                list.Add(new UsageMeter(new MeterKey(provider.Value + ":" + text), Friendly(item.Name), MeterScope.Feature, MeterUnit.Requests, (num.HasValue && num2.HasValue) ? Math.Max(0m, num.Value - num2.Value) : null, num, num6, null, flag ? null : dateTimeOffset, null, status, new MeterProvenance(Id, "$.quota_snapshots." + item.Name, now, IsAuthoritative: true)));
            }
        }
        return list;
    }

    private static string Friendly(string value)
    {
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(value.Replace('_', ' '));
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDecimal(out var value2))
        {
            return value2;
        }
        return (value.ValueKind == JsonValueKind.String && decimal.TryParse(value.GetString(), CultureInfo.InvariantCulture, out value2)) ? value2 : null;
    }

    private static string? ReadString(JsonElement element, string name)
    {
        JsonElement value;
        return (element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String) ? value.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string name)
    {
        JsonElement value;
        return element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.True;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        JsonElement value;
        DateTimeOffset result;
        return (element.TryGetProperty(name, out value) && value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out result)) ? result : null;
    }
}
