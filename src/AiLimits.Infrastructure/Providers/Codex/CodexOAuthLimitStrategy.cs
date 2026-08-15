// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Providers.Shared;

namespace AiLimits.Infrastructure.Providers.Codex;

internal sealed class CodexOAuthLimitStrategy(
    HttpClient httpClient,
    IClock clock,
    ProviderAccount expectedAccount,
    string credentialPath
) : ILimitFetchStrategy
{
    private static readonly Uri UsageUri = new Uri("https://chatgpt.com/backend-api/wham/usage");

    private static readonly IReadOnlyDictionary<string, MeterAlias> Aliases = new Dictionary<string, MeterAlias>(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["primary_window"] = new MeterAlias("primary_window", "Primary limit"),
        ["secondary_window"] = new MeterAlias("secondary_window", "Secondary limit"),
        ["individual_limit"] = new MeterAlias("individual_limit", "Individual limit"),
    };

    public string Id => "codex.oauth-usage";

    public int Order => 10;

    public async Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken
    )
    {
        return await CliCredentialReader.ReadCodexAsync(credentialPath, cancellationToken).ConfigureAwait(false) is null
            ? new StrategyAvailabilityResult(
                StrategyAvailability.NotConfigured,
                "Codex CLI OAuth credentials were not found."
            )
            : StrategyAvailabilityResult.Ready();
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        CliCredential credential = await CliCredentialReader
            .ReadCodexAsync(credentialPath, cancellationToken)
            .ConfigureAwait(false);
        if (credential is null)
        {
            return FetchResult.Failure(
                FetchFailureKind.Authentication,
                "Codex CLI OAuth credentials are unavailable.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        if (!string.Equals(credential.AccountId, expectedAccount.Key.Value, StringComparison.Ordinal))
        {
            return FetchResult.Failure(
                FetchFailureKind.AccountMismatch,
                "Codex credentials now belong to a different account.",
                FallbackPolicy.Stop,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, UsageUri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("AI-Limits-Windows/0.1");
        request.Headers.TryAddWithoutValidation("ChatGPT-Account-Id", credential.AccountId);
        using ProviderJsonResult exchange = await ProviderHttp
            .GetJsonAsync(httpClient, request, Id, "Codex", started, cancellationToken)
            .ConfigureAwait(false);
        if (!exchange.IsSuccess)
        {
            return exchange.Failure!;
        }
        JsonDocument document = exchange.Document!;
        DateTimeOffset now = clock.UtcNow;
        IReadOnlyList<UsageMeter> meters = ExtractMeters(document.RootElement, expectedAccount.Key.Provider, now);
        if (meters.Count == 0)
        {
            return FetchResult.Failure(
                FetchFailureKind.ProviderChanged,
                "Codex returned no recognizable limit meters.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        string? planType =
            document.RootElement.TryGetProperty("plan_type", out var planElement)
            && planElement.ValueKind == JsonValueKind.String
                ? planElement.GetString()
                : null;
        planType ??= credential.PlanHint;
        var extensions = string.IsNullOrWhiteSpace(planType)
            ? ProviderHttpSupport.SafeExtensions(("source", "oauth"))
            : ProviderHttpSupport.SafeExtensions(("source", "oauth"), ("plan_type", planType!));
        ProviderSnapshot snapshot = new ProviderSnapshot(
            expectedAccount.Key,
            meters,
            ExtractBalances(document.RootElement),
            SnapshotCompleteness.Authoritative,
            now,
            DataConfidence.High,
            extensions
        );
        return FetchResult.Success(snapshot, Id, Stopwatch.GetElapsedTime(started));
    }

    // Mirrors CodexBar's window handling: the two rate_limit lanes are slotted
    // by role (duration), and additional_rate_limits[] entries (model-specific
    // limits such as Codex Spark) are surfaced under their own limit_name so
    // they can never collide with the account-wide weekly window. Shapes we do
    // not model explicitly (e.g. individual_limit) still flow through the
    // generic extractor.
    internal IReadOnlyList<UsageMeter> ExtractMeters(JsonElement root, ProviderId provider, DateTimeOffset now)
    {
        List<UsageMeter> meters = new List<UsageMeter>();
        HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("rate_limit", out JsonElement rateLimit) && rateLimit.ValueKind == JsonValueKind.Object)
        {
            AddWindowMeter(
                meters,
                usedNames,
                rateLimit,
                "primary_window",
                "$.rate_limit.primary_window",
                "Primary limit",
                "codex:window:primary",
                MeterScope.Account,
                null,
                provider,
                now
            );
            AddWindowMeter(
                meters,
                usedNames,
                rateLimit,
                "secondary_window",
                "$.rate_limit.secondary_window",
                "Secondary limit",
                "codex:window:secondary",
                MeterScope.Account,
                null,
                provider,
                now
            );
        }
        if (
            root.TryGetProperty("additional_rate_limits", out JsonElement additional)
            && additional.ValueKind == JsonValueKind.Array
        )
        {
            int index = 0;
            foreach (JsonElement entry in additional.EnumerateArray())
            {
                AddAdditionalLimitMeters(meters, usedNames, entry, index++, provider, now);
            }
        }
        foreach (
            UsageMeter meter in new DynamicMeterExtractor().Extract(
                provider,
                root,
                Id,
                now,
                authoritative: true,
                Aliases
            )
        )
        {
            if (!IsWindowPath(meter.Provenance.SourcePath))
            {
                meters.Add(meter);
            }
        }
        return meters;
    }

    private static bool IsWindowPath(string path)
    {
        return path.EndsWith("primary_window", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith("secondary_window", StringComparison.OrdinalIgnoreCase);
    }

    private void AddWindowMeter(
        List<UsageMeter> meters,
        HashSet<string> usedNames,
        JsonElement parent,
        string property,
        string path,
        string fallbackName,
        string key,
        MeterScope scope,
        string? namePrefix,
        ProviderId provider,
        DateTimeOffset now
    )
    {
        if (!parent.TryGetProperty(property, out JsonElement window) || window.ValueKind != JsonValueKind.Object)
        {
            return;
        }
        if (!TryReadWindow(window, out double usedPercent, out DateTimeOffset? resetsAt, out TimeSpan? duration))
        {
            return;
        }
        string label = RoleLabel(duration) ?? fallbackName;
        if (namePrefix is not null)
        {
            // Model-specific limit: "GPT-5.3-Codex-Spark weekly limit".
            label = namePrefix + " " + char.ToLowerInvariant(label[0]) + label[1..];
        }
        if (!usedNames.Add(label))
        {
            label = "Secondary " + char.ToLowerInvariant(label[0]) + label[1..];
            usedNames.Add(label);
        }
        meters.Add(
            new UsageMeter(
                new MeterKey(key),
                label,
                scope,
                MeterUnit.Percent,
                null,
                null,
                Math.Clamp(usedPercent, 0.0, 100.0),
                duration,
                resetsAt,
                null,
                StatusFromPercent(usedPercent),
                new MeterProvenance(Id, path, now, IsAuthoritative: true)
            )
        );
    }

    private void AddAdditionalLimitMeters(
        List<UsageMeter> meters,
        HashSet<string> usedNames,
        JsonElement entry,
        int index,
        ProviderId provider,
        DateTimeOffset now
    )
    {
        if (
            entry.ValueKind != JsonValueKind.Object
            || !entry.TryGetProperty("rate_limit", out JsonElement rateLimit)
            || rateLimit.ValueKind != JsonValueKind.Object
        )
        {
            return;
        }
        string? name = ReadNonEmptyString(entry, "limit_name") ?? ReadNonEmptyString(entry, "metered_feature");
        string display = name ?? "Codex extra";
        string slug = Slug(name ?? $"extra-{index}");
        string basePath = $"$.additional_rate_limits[{index}].rate_limit";
        AddWindowMeter(
            meters,
            usedNames,
            rateLimit,
            "primary_window",
            basePath + ".primary_window",
            display + " limit",
            $"codex:extra:{slug}:primary",
            MeterScope.Model,
            display,
            provider,
            now
        );
        AddWindowMeter(
            meters,
            usedNames,
            rateLimit,
            "secondary_window",
            basePath + ".secondary_window",
            display + " limit",
            $"codex:extra:{slug}:secondary",
            MeterScope.Model,
            display,
            provider,
            now
        );
    }

    private static string? RoleLabel(TimeSpan? duration)
    {
        if (!duration.HasValue)
        {
            return null;
        }
        TimeSpan window = duration.Value;
        return window.TotalDays >= 6.5 && window.TotalDays <= 7.5 ? "Weekly limit"
            : window.TotalHours >= 4.5 && window.TotalHours <= 5.5 ? "5-hour limit"
            : window.TotalDays >= 27 && window.TotalDays <= 32 ? "Monthly limit"
            : null;
    }

    private static bool TryReadWindow(
        JsonElement window,
        out double usedPercent,
        out DateTimeOffset? resetsAt,
        out TimeSpan? duration
    )
    {
        usedPercent = 0.0;
        resetsAt = null;
        duration = null;
        bool hasPercent = false;
        if (window.TryGetProperty("used_percent", out JsonElement percentElement))
        {
            if (percentElement.ValueKind == JsonValueKind.Number && percentElement.TryGetDouble(out double numeric))
            {
                usedPercent = numeric;
                hasPercent = true;
            }
            else if (
                percentElement.ValueKind == JsonValueKind.String
                && double.TryParse(
                    percentElement.GetString(),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double parsed
                )
            )
            {
                usedPercent = parsed;
                hasPercent = true;
            }
        }
        if (!hasPercent)
        {
            return false;
        }
        foreach (string resetField in new[] { "reset_at", "resets_at" })
        {
            if (!window.TryGetProperty(resetField, out JsonElement resetElement))
            {
                continue;
            }
            if (resetElement.ValueKind == JsonValueKind.Number && resetElement.TryGetInt64(out long unix) && unix > 0)
            {
                resetsAt =
                    unix > 10000000000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(unix)
                        : DateTimeOffset.FromUnixTimeSeconds(unix);
                break;
            }
            if (
                resetElement.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(
                    resetElement.GetString(),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AdjustToUniversal
                        | System.Globalization.DateTimeStyles.AssumeUniversal,
                    out DateTimeOffset parsedReset
                )
            )
            {
                resetsAt = parsedReset;
                break;
            }
        }
        if (
            window.TryGetProperty("limit_window_seconds", out JsonElement secondsElement)
            && secondsElement.ValueKind == JsonValueKind.Number
            && secondsElement.TryGetDouble(out double seconds)
            && seconds > 0
        )
        {
            duration = TimeSpan.FromSeconds(seconds);
        }
        else if (
            window.TryGetProperty("limit_window_minutes", out JsonElement minutesElement)
            && minutesElement.ValueKind == JsonValueKind.Number
            && minutesElement.TryGetDouble(out double minutes)
            && minutes > 0
        )
        {
            duration = TimeSpan.FromMinutes(minutes);
        }
        return true;
    }

    private static MeterStatus StatusFromPercent(double percent)
    {
        return percent >= 100.0 ? MeterStatus.Exhausted
            : percent >= 95.0 ? MeterStatus.Critical
            : percent >= 80.0 ? MeterStatus.Approaching
            : MeterStatus.Healthy;
    }

    private static string? ReadNonEmptyString(JsonElement element, string property)
    {
        return
            element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()!.Trim()
            : null;
    }

    private static string Slug(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        bool lastWasDash = false;
        foreach (char c in value.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                builder.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                builder.Append('-');
                lastWasDash = true;
            }
        }
        return builder.ToString().Trim('-');
    }

    private static IReadOnlyList<BalanceMetric> ExtractBalances(JsonElement root)
    {
        if (!root.TryGetProperty("credits", out var value) || value.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<BalanceMetric>();
        }
        string[] array = new string[] { "balance", "remaining", "amount" };
        foreach (string propertyName in array)
        {
            // The balance arrives as a JSON string on current payloads
            // ("balance": "0"); TryGetDecimal throws on non-number kinds.
            if (!value.TryGetProperty(propertyName, out var value2))
            {
                continue;
            }
            if (value2.ValueKind == JsonValueKind.Number && value2.TryGetDecimal(out var value3))
            {
                return new BalanceMetric[] { new BalanceMetric("credits", "Credits", value3, MeterUnit.Credits) };
            }
            if (
                value2.ValueKind == JsonValueKind.String
                && decimal.TryParse(
                    value2.GetString(),
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsed
                )
            )
            {
                return new BalanceMetric[] { new BalanceMetric("credits", "Credits", parsed, MeterUnit.Credits) };
            }
        }
        return Array.Empty<BalanceMetric>();
    }
}
