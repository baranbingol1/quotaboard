// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Shared;

public sealed partial class DynamicMeterExtractor
{
    private static readonly string[] IdFields = new string[]
    {
        "id",
        "key",
        "meter_id",
        "meterId",
        "slug",
        "rate_limit_id",
    };

    private static readonly string[] ModelFields = new string[]
    {
        "model",
        "model_id",
        "modelId",
        "model_name",
        "modelName",
    };

    private static readonly string[] UsedFields = new string[] { "used", "usage", "current", "consumed", "spent" };

    private static readonly string[] LimitFields = new string[] { "limit", "total", "quota", "maximum", "max", "cap" };

    private static readonly string[] PercentFields = new string[]
    {
        "used_percent",
        "usedPercent",
        "percent_used",
        "percentUsed",
        "usage_percent",
        "utilization",
    };

    private static readonly string[] ResetFields = new string[]
    {
        "resets_at",
        "resetsAt",
        "reset_at",
        "resetAt",
        "reset_time",
        "next_reset_at",
        "nextResetAt",
        "windowEnd",
        "window_end",
    };

    private static readonly string[] DurationFields = new string[]
    {
        "window_seconds",
        "windowSeconds",
        "window_minutes",
        "windowMinutes",
        "window_duration",
        "windowDuration",
        "limit_window_seconds",
        "limitWindowSeconds",
        "limit_window_minutes",
        "limitWindowMinutes",
    };

    public IReadOnlyList<UsageMeter> Extract(
        ProviderId provider,
        JsonElement root,
        string strategyId,
        DateTimeOffset observedAt,
        bool authoritative,
        IReadOnlyDictionary<string, MeterAlias>? aliases = null
    )
    {
        Dictionary<MeterKey, UsageMeter> dictionary = new Dictionary<MeterKey, UsageMeter>();
        Visit(
            provider,
            root,
            "$",
            strategyId,
            observedAt,
            authoritative,
            aliases ?? new Dictionary<string, MeterAlias>(),
            dictionary
        );
        return dictionary.Values.ToArray();
    }

    private static void Visit(
        ProviderId provider,
        JsonElement element,
        string path,
        string strategyId,
        DateTimeOffset observedAt,
        bool authoritative,
        IReadOnlyDictionary<string, MeterAlias> aliases,
        IDictionary<MeterKey, UsageMeter> meters
    )
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (
                TryCreateMeter(
                    provider,
                    element,
                    path,
                    strategyId,
                    observedAt,
                    authoritative,
                    aliases,
                    out UsageMeter meter
                )
            )
            {
                meters[meter.Key] = meter;
            }
            {
                foreach (JsonProperty item in element.EnumerateObject())
                {
                    JsonValueKind valueKind = item.Value.ValueKind;
                    if (valueKind - 1 <= JsonValueKind.Object)
                    {
                        Visit(
                            provider,
                            item.Value,
                            path + "." + item.Name,
                            strategyId,
                            observedAt,
                            authoritative,
                            aliases,
                            meters
                        );
                    }
                }
                return;
            }
        }
        if (element.ValueKind != JsonValueKind.Array)
        {
            return;
        }
        foreach (JsonElement item2 in element.EnumerateArray())
        {
            Visit(provider, item2, path + "[]", strategyId, observedAt, authoritative, aliases, meters);
        }
    }

    private static bool TryCreateMeter(
        ProviderId provider,
        JsonElement element,
        string path,
        string strategyId,
        DateTimeOffset observedAt,
        bool authoritative,
        IReadOnlyDictionary<string, MeterAlias> aliases,
        out UsageMeter meter
    )
    {
        meter = null;
        decimal value;
        string matched;
        bool flag = TryReadDecimal(element, PercentFields, out value, out matched);
        decimal value2;
        string matched2;
        bool flag2 = TryReadDecimal(element, UsedFields, out value2, out matched2);
        decimal value3;
        string matched3;
        bool flag3 = TryReadDecimal(element, LimitFields, out value3, out matched3);
        DateTimeOffset reset;
        bool flag4 = TryReadReset(element, out reset);
        if (!flag && !(flag2 && flag3))
        {
            return false;
        }
        string text = ReadString(element, IdFields);
        string text2 = ReadString(element, ModelFields);
        MeterScope meterScope = InferScope(path, text2);
        string key = text ?? LastPathSegment(path);
        aliases.TryGetValue(key, out MeterAlias value4);
        if (value4 is null)
        {
            value4 = aliases
                .FirstOrDefault<KeyValuePair<string, MeterAlias>>(
                    (KeyValuePair<string, MeterAlias> pair) =>
                        path.EndsWith(pair.Key, StringComparison.OrdinalIgnoreCase)
                )
                .Value;
        }
        if (!flag && value3 > 0m)
        {
            value = value2 / value3 * 100m;
            flag = true;
        }
        // Only "utilization" conventionally carries a 0..1 fraction. The
        // *_percent aliases are deliberately never rescaled: 0.8 in a
        // used_percent field is a legitimate reading right after a quota
        // reset, and inflating it to 80% would corrupt real data. An alias
        // declaring PercentIsAbsolute pins the field to the 0..100 scale:
        // Claude's five_hour/seven_day "utilization" is a percent, and a real
        // 1% reading must not be inflated to 100%.
        else if (
            flag
            && value >= 0m
            && value <= 1m
            && string.Equals(matched, "utilization", StringComparison.OrdinalIgnoreCase)
            && value4?.PercentIsAbsolute != true
        )
        {
            value *= 100m;
        }
        meterScope = value4?.Scope ?? meterScope;
        string value5 = ((text != null) ? ("issued:" + text) : $"derived:{path}|{meterScope}|{text2 ?? string.Empty}");
        double? num = (flag ? Math.Clamp((double)value, 0.0, 100.0) : null);
        meter = new UsageMeter(
            new MeterKey(provider.Value + ":" + StableHash(value5)),
            value4?.DisplayName ?? FriendlyName(text ?? LastPathSegment(path)),
            meterScope,
            value4?.Unit ?? InferUnit(path, matched2, matched3),
            flag2 ? value2 : null,
            flag3 ? value3 : null,
            num,
            ReadDuration(element),
            flag4 ? reset : null,
            text2,
            StatusFrom(num),
            new MeterProvenance(strategyId, path, observedAt, authoritative)
        );
        return true;
    }

    private static MeterScope InferScope(string path, string? modelId)
    {
        if (!string.IsNullOrWhiteSpace(modelId) || path.Contains("model", StringComparison.OrdinalIgnoreCase))
        {
            return MeterScope.Model;
        }
        if (
            path.Contains("organization", StringComparison.OrdinalIgnoreCase)
            || path.Contains("org", StringComparison.OrdinalIgnoreCase)
        )
        {
            return MeterScope.Organization;
        }
        if (
            path.Contains("offering", StringComparison.OrdinalIgnoreCase)
            || path.Contains("zen", StringComparison.OrdinalIgnoreCase)
            || path.Contains("go", StringComparison.OrdinalIgnoreCase)
        )
        {
            return MeterScope.Offering;
        }
        return MeterScope.Account;
    }

    private static MeterUnit InferUnit(string path, string? usedField, string? limitField)
    {
        string text = $"{path} {usedField} {limitField}";
        if (text.Contains("credit", StringComparison.OrdinalIgnoreCase))
        {
            return MeterUnit.Credits;
        }
        if (text.Contains("token", StringComparison.OrdinalIgnoreCase))
        {
            return MeterUnit.Tokens;
        }
        if (
            text.Contains("dollar", StringComparison.OrdinalIgnoreCase)
            || text.Contains("usd", StringComparison.OrdinalIgnoreCase)
            || text.Contains("spend", StringComparison.OrdinalIgnoreCase)
        )
        {
            return MeterUnit.Usd;
        }
        if (text.Contains("request", StringComparison.OrdinalIgnoreCase))
        {
            return MeterUnit.Requests;
        }
        return MeterUnit.Percent;
    }

    private static MeterStatus StatusFrom(double? percent)
    {
        MeterStatus result;
        if (percent.HasValue)
        {
            double valueOrDefault = percent.GetValueOrDefault();
            result = (
                (valueOrDefault >= 100.0)
                    ? MeterStatus.Exhausted
                    : (
                        (valueOrDefault >= 95.0)
                            ? MeterStatus.Critical
                            : ((!(valueOrDefault >= 80.0)) ? MeterStatus.Healthy : MeterStatus.Approaching)
                    )
            );
        }
        else
        {
            result = MeterStatus.Unknown;
        }
        return result;
    }

    private static bool TryReadDecimal(
        JsonElement element,
        IReadOnlyList<string> names,
        out decimal value,
        out string? matched
    )
    {
        foreach (string name in names)
        {
            if (TryGetProperty(element, name, out var value2))
            {
                if (value2.ValueKind == JsonValueKind.Number && value2.TryGetDecimal(out value))
                {
                    matched = name;
                    return true;
                }
                if (
                    value2.ValueKind == JsonValueKind.String
                    && decimal.TryParse(
                        value2.GetString()?.TrimEnd('%'),
                        NumberStyles.Number,
                        CultureInfo.InvariantCulture,
                        out value
                    )
                )
                {
                    matched = name;
                    return true;
                }
            }
        }
        value = default(decimal);
        matched = null;
        return false;
    }

    private static string? ReadString(JsonElement element, IReadOnlyList<string> names)
    {
        foreach (string name in names)
        {
            if (
                TryGetProperty(element, name, out var value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
            )
            {
                return value.GetString().Trim();
            }
        }
        return null;
    }

    private static bool TryReadReset(JsonElement element, out DateTimeOffset reset)
    {
        string[] resetFields = ResetFields;
        foreach (string name in resetFields)
        {
            if (TryGetProperty(element, name, out var value))
            {
                if (
                    value.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(
                        value.GetString(),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                        out reset
                    )
                )
                {
                    return true;
                }
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var value2))
                {
                    // FromUnixTime* throws ArgumentOutOfRangeException on
                    // out-of-range input, and one malformed reset field must
                    // not fail the whole provider extraction.
                    try
                    {
                        reset = (
                            (value2 > 10000000000L)
                                ? DateTimeOffset.FromUnixTimeMilliseconds(value2)
                                : DateTimeOffset.FromUnixTimeSeconds(value2)
                        );
                        return true;
                    }
                    catch (ArgumentOutOfRangeException) { }
                }
            }
        }
        reset = default(DateTimeOffset);
        return false;
    }

    private static TimeSpan? ReadDuration(JsonElement element)
    {
        string[] durationFields = DurationFields;
        foreach (string text in durationFields)
        {
            if (!TryGetProperty(element, text, out var value))
            {
                continue;
            }
            if (value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var value2))
            {
                return text.Contains("minute", StringComparison.OrdinalIgnoreCase)
                    ? TimeSpan.FromMinutes(value2)
                    : TimeSpan.FromSeconds(value2);
            }
            if (value.ValueKind != JsonValueKind.String)
            {
                continue;
            }
            string text2 = value.GetString();
            if (TimeSpan.TryParse(text2, CultureInfo.InvariantCulture, out var result))
            {
                return result;
            }
            Match match = DurationPattern().Match(text2 ?? string.Empty);
            if (match.Success && double.TryParse(match.Groups[1].Value, CultureInfo.InvariantCulture, out value2))
            {
                string text3 = match.Groups[2].Value.ToLowerInvariant();
                TimeSpan value3 = text3 switch
                {
                    "m" => TimeSpan.FromMinutes(value2),
                    "h" => TimeSpan.FromHours(value2),
                    "d" => TimeSpan.FromDays(value2),
                    _ => TimeSpan.FromSeconds(value2),
                };
                return value3;
            }
        }
        return null;
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        if (element.TryGetProperty(name, out value))
        {
            return true;
        }
        foreach (JsonProperty item in element.EnumerateObject())
        {
            if (item.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                value = item.Value;
                return true;
            }
        }
        value = default(JsonElement);
        return false;
    }

    private static string LastPathSegment(string path)
    {
        return (path.Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "usage").Replace(
            "[]",
            string.Empty,
            StringComparison.Ordinal
        );
    }

    private static string FriendlyName(string value)
    {
        string text = WordBoundaryPattern().Replace(value.Replace('_', ' ').Replace('-', ' '), "$1 $2");
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(text.Trim().ToLowerInvariant());
    }

    private static string StableHash(string value)
    {
        return Convert
            .ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Normalize(NormalizationForm.FormKC)))[..12])
            .ToLowerInvariant();
    }

    /// <summary>Matches a whole-string duration such as "30m", "1.5 h" or "45S".</summary>
    [GeneratedRegex(@"^\s*(\d+(?:\.\d+)?)\s*([smhd])\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex DurationPattern();

    /// <summary>Matches a camelCase boundary so "usedPercent" can be split into words.</summary>
    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex WordBoundaryPattern();
}
