// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Cursor;

internal static class CursorUsageMapper
{
    internal static ProviderSnapshot Map(
        AccountKey account,
        JsonElement summary,
        JsonElement? legacy,
        JsonElement? identity,
        DateTimeOffset observedAt,
        string strategyId
    )
    {
        DateTimeOffset? billingStart = ReadDate(summary, "billingCycleStart");
        DateTimeOffset? billingEnd = ReadDate(summary, "billingCycleEnd");
        TimeSpan? billingWindow = billingStart.HasValue && billingEnd > billingStart ? billingEnd - billingStart : null;

        List<UsageMeter> meters = [];
        List<BalanceMetric> balances = [];
        if (!TryAddLegacyRequestMeter(meters, legacy, billingWindow, billingEnd, observedAt, strategyId))
        {
            AddModernMeters(meters, balances, summary, billingWindow, billingEnd, observedAt, strategyId);
        }
        if (meters.Count == 0 && balances.Count == 0)
        {
            throw new JsonException("Cursor usage summary contained no supported usage fields.");
        }

        List<(string Key, string Value)> extensions = [("source", "cursor-app-session")];
        string? email = ReadString(identity, "email");
        string? accountId = ReadString(identity, "sub");
        string? planType = ReadString(summary, "membershipType");
        if (!string.IsNullOrWhiteSpace(email))
            extensions.Add(("email", email));
        if (!string.IsNullOrWhiteSpace(accountId))
            extensions.Add(("account_id", accountId));
        if (!string.IsNullOrWhiteSpace(planType))
            extensions.Add(("plan_type", planType));

        return new ProviderSnapshot(
            account,
            meters,
            balances,
            SnapshotCompleteness.Authoritative,
            observedAt,
            DataConfidence.High,
            ProviderHttpSupport.SafeExtensions(extensions.ToArray())
        );
    }

    internal static string? ReadString(JsonElement? element, string path)
    {
        if (!element.HasValue)
            return null;
        JsonElement value = Find(element.Value, path);
        return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
    }

    private static bool TryAddLegacyRequestMeter(
        List<UsageMeter> meters,
        JsonElement? legacy,
        TimeSpan? window,
        DateTimeOffset? resetsAt,
        DateTimeOffset observedAt,
        string strategyId
    )
    {
        if (!legacy.HasValue)
            return false;
        JsonElement requests = Find(legacy.Value, "gpt-4");
        double? limit = ReadNumber(requests, "maxRequestUsage");
        if (!(limit > 0))
            return false;

        double used = ReadNumber(requests, "numRequestsTotal") ?? ReadNumber(requests, "numRequests") ?? 0;
        used = Math.Max(0, used);
        double usedPercent = Math.Clamp(used / limit.Value * 100, 0, 100);
        meters.Add(
            CreateMeter(
                "cursor:requests",
                "Requests",
                MeterUnit.Requests,
                (decimal)used,
                (decimal)limit.Value,
                usedPercent,
                window,
                resetsAt,
                "$.gpt-4",
                observedAt,
                strategyId
            )
        );
        return true;
    }

    private static void AddModernMeters(
        List<UsageMeter> meters,
        List<BalanceMetric> balances,
        JsonElement summary,
        TimeSpan? window,
        DateTimeOffset? resetsAt,
        DateTimeOffset observedAt,
        string strategyId
    )
    {
        JsonElement individualUsage = Find(summary, "individualUsage");
        JsonElement plan = Find(individualUsage, "plan");
        JsonElement overall = Find(individualUsage, "overall");
        JsonElement pooled = Find(Find(summary, "teamUsage"), "pooled");

        double? autoPercent = ClampPercent(ReadNumber(plan, "autoPercentUsed"));
        double? apiPercent = ClampPercent(ReadNumber(plan, "apiPercentUsed"));
        double? totalPercent = ClampPercent(ReadNumber(plan, "totalPercentUsed"));
        // Auto and API are separate quotas, so neither their mean nor whichever
        // one happened to be present is a total: averaging an exhausted Auto
        // (100%) with an untouched API (0%) reported a comfortable "Total 50%"
        // while the user could no longer make a request. A total is only
        // derived from a real used/limit pair; when Cursor reports none, the
        // Total meter is simply absent and the Auto and API meters speak for
        // themselves.
        totalPercent ??= RatioPercent(plan) ?? RatioPercent(overall) ?? RatioPercent(pooled);

        JsonElement amountSource =
            HasPositiveAmount(plan) ? plan
            : HasPositiveAmount(overall) ? overall
            : pooled;
        double? amountUsedCents = ReadNumber(amountSource, "used");
        double? amountLimitCents = ReadNumber(amountSource, "limit");

        if (totalPercent.HasValue)
        {
            if (amountUsedCents.HasValue && amountLimitCents > 0)
            {
                meters.Add(
                    CreateMeter(
                        "cursor:total",
                        "Total",
                        MeterUnit.Usd,
                        (decimal)amountUsedCents.Value / 100m,
                        (decimal)amountLimitCents.Value / 100m,
                        totalPercent.Value,
                        window,
                        resetsAt,
                        "$.individualUsage.plan",
                        observedAt,
                        strategyId
                    )
                );
            }
            else
            {
                meters.Add(
                    CreatePercentMeter(
                        "cursor:total",
                        "Total",
                        totalPercent.Value,
                        window,
                        resetsAt,
                        "$.individualUsage.plan.totalPercentUsed",
                        observedAt,
                        strategyId
                    )
                );
            }
        }
        if (autoPercent.HasValue)
        {
            meters.Add(
                CreatePercentMeter(
                    "cursor:auto",
                    "Auto",
                    autoPercent.Value,
                    window,
                    resetsAt,
                    "$.individualUsage.plan.autoPercentUsed",
                    observedAt,
                    strategyId
                )
            );
        }
        if (apiPercent.HasValue)
        {
            meters.Add(
                CreatePercentMeter(
                    "cursor:api",
                    "API",
                    apiPercent.Value,
                    window,
                    resetsAt,
                    "$.individualUsage.plan.apiPercentUsed",
                    observedAt,
                    strategyId
                )
            );
        }

        JsonElement personalOnDemand = Find(individualUsage, "onDemand");
        JsonElement teamOnDemand = Find(Find(summary, "teamUsage"), "onDemand");
        JsonElement selectedOnDemand =
            ReadNumber(personalOnDemand, "limit") > 0 ? personalOnDemand
            : ReadNumber(teamOnDemand, "limit") > 0 ? teamOnDemand
            : personalOnDemand;
        double? onDemandUsedCents = ReadNumber(selectedOnDemand, "used");
        double? onDemandLimitCents = ReadNumber(selectedOnDemand, "limit");
        if (onDemandLimitCents > 0)
        {
            double usedCents = Math.Max(0, onDemandUsedCents ?? 0);
            double percent = Math.Clamp(usedCents / onDemandLimitCents.Value * 100, 0, 100);
            meters.Add(
                CreateMeter(
                    "cursor:extra",
                    "Extra usage",
                    MeterUnit.Usd,
                    (decimal)usedCents / 100m,
                    (decimal)onDemandLimitCents.Value / 100m,
                    percent,
                    window,
                    resetsAt,
                    "$.individualUsage.onDemand",
                    observedAt,
                    strategyId
                )
            );
        }
        else if (onDemandUsedCents > 0)
        {
            balances.Add(
                new BalanceMetric(
                    "cursor:extra-spend",
                    "Extra usage spend",
                    (decimal)onDemandUsedCents.Value / 100m,
                    MeterUnit.Usd
                )
            );
        }
    }

    private static UsageMeter CreatePercentMeter(
        string key,
        string name,
        double usedPercent,
        TimeSpan? window,
        DateTimeOffset? resetsAt,
        string sourcePath,
        DateTimeOffset observedAt,
        string strategyId
    ) =>
        CreateMeter(
            key,
            name,
            MeterUnit.Percent,
            (decimal)usedPercent,
            100m,
            usedPercent,
            window,
            resetsAt,
            sourcePath,
            observedAt,
            strategyId
        );

    private static UsageMeter CreateMeter(
        string key,
        string name,
        MeterUnit unit,
        decimal used,
        decimal? limit,
        double usedPercent,
        TimeSpan? window,
        DateTimeOffset? resetsAt,
        string sourcePath,
        DateTimeOffset observedAt,
        string strategyId
    ) =>
        new(
            new MeterKey(key),
            name,
            MeterScope.Account,
            unit,
            used,
            limit,
            usedPercent,
            window,
            resetsAt,
            null,
            StatusFor(usedPercent),
            new MeterProvenance(strategyId, sourcePath, observedAt, IsAuthoritative: true)
        );

    private static bool HasPositiveAmount(JsonElement element) =>
        ReadNumber(element, "used") > 0 || ReadNumber(element, "limit") > 0;

    private static double? RatioPercent(JsonElement element)
    {
        double? used = ReadNumber(element, "used");
        double? limit = ReadNumber(element, "limit");
        return used.HasValue && limit > 0 ? Math.Clamp(used.Value / limit.Value * 100, 0, 100) : null;
    }

    private static double? ClampPercent(double? value) =>
        value.HasValue && double.IsFinite(value.Value) ? Math.Clamp(value.Value, 0, 100) : null;

    private static MeterStatus StatusFor(double usedPercent) =>
        usedPercent >= 100 ? MeterStatus.Exhausted
        : usedPercent >= 95 ? MeterStatus.Critical
        : usedPercent >= 80 ? MeterStatus.Approaching
        : MeterStatus.Healthy;

    private static double? ReadNumber(JsonElement element, string path)
    {
        JsonElement value = Find(element, path);
        if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double number) && double.IsFinite(number))
        {
            return number;
        }
        if (
            value.ValueKind == JsonValueKind.String
            && double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number)
            && double.IsFinite(number)
        )
        {
            return number;
        }
        return null;
    }

    private static DateTimeOffset? ReadDate(JsonElement element, string path)
    {
        string? value = ReadString(element, path);
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out DateTimeOffset parsed
        )
            ? parsed
            : null;
    }

    private static JsonElement Find(JsonElement element, string path)
    {
        foreach (string segment in path.Split('.'))
        {
            if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(segment, out element))
            {
                return default;
            }
        }
        return element;
    }
}
