// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Providers.Shared;

namespace AiLimits.Infrastructure.Providers.Claude;

internal sealed class ClaudeOAuthLimitStrategy(
    HttpClient httpClient,
    IClock clock,
    ProviderAccount expectedAccount,
    string credentialPath
) : ILimitFetchStrategy
{
    private static readonly Uri UsageUri = new Uri("https://api.anthropic.com/api/oauth/usage");

    public string Id => "claude.oauth-usage";

    public int Order => 10;

    public async Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken
    )
    {
        return
            await CliCredentialReader.ReadClaudeAsync(credentialPath, cancellationToken).ConfigureAwait(false) is null
            ? new StrategyAvailabilityResult(
                StrategyAvailability.NotConfigured,
                "Claude Code OAuth credentials were not found."
            )
            : StrategyAvailabilityResult.Ready();
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        CliCredential credential = await CliCredentialReader
            .ReadClaudeAsync(credentialPath, cancellationToken)
            .ConfigureAwait(false);
        if (credential is null)
        {
            return FetchResult.Failure(
                FetchFailureKind.Authentication,
                "Claude Code OAuth credentials are unavailable.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        if (!string.Equals(credential.AccountId, expectedAccount.Key.Value, StringComparison.Ordinal))
        {
            return FetchResult.Failure(
                FetchFailureKind.AccountMismatch,
                "Claude credentials now belong to a different account.",
                FallbackPolicy.Stop,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.UserAgent.ParseAdd("AI-Limits-Windows/0.1");
        using ProviderJsonResult exchange = await ProviderHttp
            .GetJsonAsync(httpClient, request, Id, "Claude", started, cancellationToken)
            .ConfigureAwait(false);
        if (!exchange.IsSuccess)
        {
            return exchange.Failure!;
        }
        JsonDocument document = exchange.Document!;
        DateTimeOffset now = clock.UtcNow;
        Dictionary<string, MeterAlias> aliases = new Dictionary<string, MeterAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["five_hour"] = new MeterAlias("five_hour", "5-hour limit", PercentIsAbsolute: true),
            ["seven_day"] = new MeterAlias("seven_day", "Weekly limit", PercentIsAbsolute: true),
        };
        List<UsageMeter> meters = new DynamicMeterExtractor()
            .Extract(expectedAccount.Key.Provider, document.RootElement, Id, now, authoritative: true, aliases)
            .Select(WithKnownWindowDuration)
            .ToList();
        AddScopedLimitMeters(document.RootElement, now, meters);
        var extensions = string.IsNullOrWhiteSpace(credential.PlanHint)
            ? ProviderHttpSupport.SafeExtensions(("source", "oauth"))
            : ProviderHttpSupport.SafeExtensions(("source", "oauth"), ("plan_type", credential.PlanHint!));
        return (meters.Count != 0)
            ? FetchResult.Success(
                new ProviderSnapshot(
                    expectedAccount.Key,
                    meters,
                    Array.Empty<BalanceMetric>(),
                    SnapshotCompleteness.Authoritative,
                    now,
                    DataConfidence.High,
                    extensions
                ),
                Id,
                Stopwatch.GetElapsedTime(started)
            )
            : FetchResult.Failure(
                FetchFailureKind.ProviderChanged,
                "Claude returned no recognizable limit meters.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
    }

    // The payload carries no window duration for the flat five_hour/seven_day
    // objects; stamp the well-known spans so pace projection can work.
    private static UsageMeter WithKnownWindowDuration(UsageMeter meter)
    {
        if (meter.WindowDuration.HasValue)
        {
            return meter;
        }
        return meter.Provenance.SourcePath switch
        {
            "$.five_hour" => meter with { WindowDuration = TimeSpan.FromHours(5) },
            "$.seven_day" => meter with { WindowDuration = TimeSpan.FromDays(7) },
            _ => meter,
        };
    }

    // Model-scoped windows (e.g. the Fable promotional weekly limit) arrive in
    // the flat limits[] array as { kind, group, percent, resets_at,
    // scope.model.display_name }. The generic extractor cannot see them ("percent"
    // is not a recognized field), so parse them explicitly. Unscoped entries
    // (session / weekly_all) duplicate five_hour / seven_day and are skipped.
    internal void AddScopedLimitMeters(JsonElement root, DateTimeOffset now, List<UsageMeter> meters)
    {
        if (!root.TryGetProperty("limits", out JsonElement limits) || limits.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        int index = -1;
        foreach (JsonElement entry in limits.EnumerateArray())
        {
            index++;
            if (
                entry.ValueKind != JsonValueKind.Object
                || !entry.TryGetProperty("scope", out JsonElement scope)
                || scope.ValueKind != JsonValueKind.Object
            )
            {
                continue;
            }
            string? modelName =
                scope.TryGetProperty("model", out JsonElement model)
                && model.ValueKind == JsonValueKind.Object
                && model.TryGetProperty("display_name", out JsonElement displayName)
                && displayName.ValueKind == JsonValueKind.String
                    ? displayName.GetString()
                    : null;
            if (
                string.IsNullOrWhiteSpace(modelName)
                || !entry.TryGetProperty("percent", out JsonElement percentElement)
                || percentElement.ValueKind != JsonValueKind.Number
                || !percentElement.TryGetDouble(out double percent)
            )
            {
                continue;
            }
            string group =
                entry.TryGetProperty("group", out JsonElement groupElement)
                && groupElement.ValueKind == JsonValueKind.String
                    ? groupElement.GetString() ?? ""
                    : "";
            DateTimeOffset? resetsAt =
                entry.TryGetProperty("resets_at", out JsonElement resetElement)
                && resetElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    resetElement.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsedReset
                )
                    ? parsedReset
                    : null;
            (string suffix, TimeSpan? window) = group.ToLowerInvariant() switch
            {
                "weekly" => (" weekly limit", (TimeSpan?)TimeSpan.FromDays(7)),
                "session" => (" 5-hour limit", TimeSpan.FromHours(5)),
                _ => (" limit", null),
            };
            double clamped = Math.Clamp(percent, 0.0, 100.0);
            MeterStatus status =
                clamped >= 100.0 ? MeterStatus.Exhausted
                : clamped >= 95.0 ? MeterStatus.Critical
                : clamped >= 80.0 ? MeterStatus.Approaching
                : MeterStatus.Healthy;
            string kind =
                entry.TryGetProperty("kind", out JsonElement kindElement)
                && kindElement.ValueKind == JsonValueKind.String
                    ? kindElement.GetString() ?? "scoped"
                    : "scoped";
            meters.Add(
                new UsageMeter(
                    new MeterKey($"claude:scoped:{kind}:{modelName!.ToLowerInvariant()}"),
                    modelName + suffix,
                    MeterScope.Model,
                    MeterUnit.Percent,
                    null,
                    null,
                    clamped,
                    window,
                    resetsAt,
                    null,
                    status,
                    new MeterProvenance(Id, $"$.limits[{index}]", now, IsAuthoritative: true)
                )
            );
        }
    }
}
