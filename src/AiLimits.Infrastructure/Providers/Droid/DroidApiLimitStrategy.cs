// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Providers.Shared;

namespace AiLimits.Infrastructure.Providers.Droid;

internal sealed class DroidApiLimitStrategy(
    HttpClient httpClient,
    IClock clock,
    string? strategyId = null,
    int strategyOrder = 20
) : ILimitFetchStrategy
{
    private static readonly Uri LimitsUri = new Uri("https://api.factory.ai/api/billing/limits");

    private static readonly Uri UsageUri = new Uri(
        "https://api.factory.ai/api/organization/subscription/usage?useCache=true"
    );

    public string Id => strategyId ?? "droid.factory-api";

    public int Order => strategyOrder;

    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken
    )
    {
        return Task.FromResult(
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("FACTORY_API_KEY"))
                ? new StrategyAvailabilityResult(
                    StrategyAvailability.NotConfigured,
                    "Optional Factory API key is not configured."
                )
                : StrategyAvailabilityResult.Ready()
        );
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        string token = Environment.GetEnvironmentVariable("FACTORY_API_KEY")?.Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return FetchResult.Failure(
                FetchFailureKind.Authentication,
                "Optional Factory API key is not configured.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        return await FetchWithTokenAsync(account, token, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<FetchResult> FetchWithTokenAsync(
        ProviderAccount account,
        string token,
        CancellationToken cancellationToken
    )
    {
        long started = Stopwatch.GetTimestamp();
        (string? PlanName, string? Email) accountInfo = await FetchAccountInfoAsync(token, cancellationToken)
            .ConfigureAwait(false);
        using (
            ProviderJsonResult limits = await FetchJsonAsync(LimitsUri, token, started, cancellationToken)
                .ConfigureAwait(false)
        )
        {
            if (limits.IsSuccess)
            {
                FetchResult result = BuildSnapshot(
                    account.Key,
                    limits.Document!.RootElement,
                    billingLimits: true,
                    started,
                    accountInfo.PlanName,
                    accountInfo.Email
                );
                if (result.IsSuccess)
                {
                    return result;
                }
            }
        }
        using ProviderJsonResult usage = await FetchJsonAsync(UsageUri, token, started, cancellationToken)
            .ConfigureAwait(false);
        if (!usage.IsSuccess)
        {
            return usage.Failure!;
        }
        return BuildSnapshot(
            account.Key,
            usage.Document!.RootElement,
            billingLimits: false,
            started,
            accountInfo.PlanName,
            accountInfo.Email
        );
    }

    // Best-effort plan lookup ("Pro", "Max"…): app.factory.ai/api/app/auth/me →
    // organization.subscription.orbSubscription.plan.name (CodexBar parity).
    private async Task<(string? PlanName, string? Email)> FetchAccountInfoAsync(
        string token,
        CancellationToken cancellationToken
    )
    {
        using ProviderJsonResult me = await FetchJsonAsync(
                new Uri("https://app.factory.ai/api/app/auth/me"),
                token,
                Stopwatch.GetTimestamp(),
                cancellationToken
            )
            .ConfigureAwait(false);
        if (!me.IsSuccess)
        {
            return (null, null);
        }
        JsonElement root = me.Document!.RootElement;
        string? planName = ReadNestedString(root, "organization", "subscription", "orbSubscription", "plan", "name");
        string? email =
            ReadNestedString(root, "userProfile", "email")
            ?? ReadNestedString(root, "userProfile", "workosUser", "email");
        return (planName, email);
    }

    internal FetchResult BuildSnapshot(
        AccountKey account,
        JsonElement root,
        bool billingLimits,
        long started,
        string? planName = null,
        string? email = null
    )
    {
        DateTimeOffset utcNow = clock.UtcNow;
        Dictionary<string, MeterAlias> aliases = new Dictionary<string, MeterAlias>(StringComparer.OrdinalIgnoreCase)
        {
            ["fiveHour"] = new MeterAlias("fiveHour", "5-hour limit", MeterScope.Account, MeterUnit.Tokens),
            ["weekly"] = new MeterAlias("weekly", "Weekly limit", MeterScope.Account, MeterUnit.Tokens),
            ["monthly"] = new MeterAlias("monthly", "Monthly limit", MeterScope.Account, MeterUnit.Tokens),
            ["standard"] = new MeterAlias("standard", "Standard tokens", MeterScope.Account, MeterUnit.Tokens),
            ["premium"] = new MeterAlias("premium", "Premium tokens", MeterScope.Account, MeterUnit.Tokens),
        };
        // /billing/limits carries two sibling pools ("standard" and "core" —
        // the Droid Core model allowance). The extractor names meters by leaf
        // field only, so without this both pools render as identical
        // "5-hour/Weekly/Monthly limit" triplets.
        List<UsageMeter> list = new DynamicMeterExtractor()
            .Extract(account.Provider, root, Id, utcNow, authoritative: true, aliases)
            .Select(meter =>
                meter.Provenance.SourcePath.Contains(".core.", StringComparison.OrdinalIgnoreCase)
                    ? meter with
                    {
                        DisplayName =
                            "Droid Core " + char.ToLowerInvariant(meter.DisplayName[0]) + meter.DisplayName[1..],
                    }
                    : meter
            )
            // Factory's rolling windows start on first use and the API keeps
            // serving the LAST completed window (past windowEnd, secondsRemaining
            // null) with its old usedPercent. Factory's own dashboard renders
            // those as 0% ("Use Droid to start"), so an expired window must not
            // be shown as current usage.
            .Select(meter =>
                meter.ResetsAt.HasValue && meter.ResetsAt.Value <= utcNow
                    ? meter with
                    {
                        Used = null,
                        UsedPercent = 0.0,
                        ResetsAt = null,
                        WindowDuration = null,
                        Status = MeterStatus.Healthy,
                    }
                    : meter
            )
            .ToList();
        if (!billingLimits && root.TryGetProperty("usage", out var value) && value.ValueKind == JsonValueKind.Object)
        {
            AddLegacyTokenMeters(account, value, utcNow, list);
        }
        if (list.Count == 0)
        {
            return FetchResult.Failure(
                FetchFailureKind.ProviderChanged,
                "Factory returned no recognizable subscription limits.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
        List<BalanceMetric> list2 = new List<BalanceMetric>();
        if (root.TryGetProperty("extraUsageBalanceCents", out var value2) && value2.TryGetDecimal(out var value3))
        {
            list2.Add(new BalanceMetric("extra-usage", "Extra usage balance", value3 / 100m, MeterUnit.Usd));
        }
        var extensionValues = new List<(string Key, string Value)>
        {
            ("source", billingLimits ? "billing-limits" : "subscription-usage"),
        };
        if (!string.IsNullOrWhiteSpace(planName))
            extensionValues.Add(("plan_type", planName.Trim()));
        if (!string.IsNullOrWhiteSpace(email))
            extensionValues.Add(("email", email.Trim()));
        var extensions = ProviderHttpSupport.SafeExtensions(extensionValues.ToArray());
        return FetchResult.Success(
            new ProviderSnapshot(
                account,
                list,
                list2,
                SnapshotCompleteness.Authoritative,
                utcNow,
                DataConfidence.High,
                extensions
            ),
            Id,
            Stopwatch.GetElapsedTime(started)
        );
    }

    private void AddLegacyTokenMeters(
        AccountKey account,
        JsonElement usage,
        DateTimeOffset now,
        ICollection<UsageMeter> meters
    )
    {
        DateTimeOffset? resetsAt = null;
        if (usage.TryGetProperty("endDate", out var value) && value.TryGetInt64(out var value2))
        {
            resetsAt = (
                (value2 > 10000000000L)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(value2)
                    : DateTimeOffset.FromUnixTimeSeconds(value2)
            );
        }
        string[] array = new string[] { "standard", "premium" };
        foreach (string text in array)
        {
            if (!usage.TryGetProperty(text, out var value3) || value3.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            decimal valueOrDefault = ReadDecimal(value3, "userTokens").GetValueOrDefault();
            decimal valueOrDefault2 = ReadDecimal(value3, "totalAllowance").GetValueOrDefault();
            decimal? num = ReadDecimal(value3, "usedRatio");
            if (!(valueOrDefault == 0m) || !(valueOrDefault2 == 0m) || num.HasValue)
            {
                double num2;
                if (!num.HasValue)
                {
                    num2 = ((valueOrDefault2 > 0m) ? ((double)(valueOrDefault / valueOrDefault2 * 100m)) : 0.0);
                }
                else
                {
                    decimal valueOrDefault3 = num.GetValueOrDefault();
                    num2 = ((valueOrDefault3 <= 1m) ? ((double)(valueOrDefault3 * 100m)) : ((double)valueOrDefault3));
                }
                double num3 = num2;
                meters.Add(
                    new UsageMeter(
                        new MeterKey("droid:" + text),
                        CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text) + " tokens",
                        MeterScope.Account,
                        MeterUnit.Tokens,
                        valueOrDefault,
                        (valueOrDefault2 > 0m) ? valueOrDefault2 : null,
                        Math.Clamp(num3, 0.0, 100.0),
                        null,
                        resetsAt,
                        null,
                        (num3 >= 95.0)
                            ? MeterStatus.Critical
                            : ((!(num3 >= 80.0)) ? MeterStatus.Healthy : MeterStatus.Approaching),
                        new MeterProvenance(Id, "$.usage." + text, now, IsAuthoritative: true)
                    )
                );
            }
        }
    }

    private static decimal? ReadDecimal(JsonElement element, string name)
    {
        JsonElement value;
        decimal value2;
        return (element.TryGetProperty(name, out value) && value.TryGetDecimal(out value2)) ? value2 : null;
    }

    private static string? ReadNestedString(JsonElement root, params string[] path)
    {
        JsonElement cursor = root;
        foreach (string segment in path)
        {
            if (cursor.ValueKind != JsonValueKind.Object || !cursor.TryGetProperty(segment, out cursor))
            {
                return null;
            }
        }
        return cursor.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(cursor.GetString())
            ? cursor.GetString()!.Trim()
            : null;
    }

    private async Task<ProviderJsonResult> FetchJsonAsync(
        Uri uri,
        string token,
        long startedTimestamp,
        CancellationToken cancellationToken
    )
    {
        using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Origin", "https://app.factory.ai");
        request.Headers.Referrer = new Uri("https://app.factory.ai/");
        request.Headers.TryAddWithoutValidation("x-factory-client", "web-app");
        return await ProviderHttp
            .GetJsonAsync(httpClient, request, Id, "Factory", startedTimestamp, cancellationToken)
            .ConfigureAwait(false);
    }
}
