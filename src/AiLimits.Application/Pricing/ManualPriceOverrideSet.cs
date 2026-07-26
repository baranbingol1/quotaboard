// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace AiLimits.Application.Pricing;

/// <summary>USD per one million tokens, entered by the user for a model the catalog cannot price.</summary>
public sealed record ManualModelPrice(
    decimal InputPerMillion,
    decimal OutputPerMillion,
    decimal? CacheReadPerMillion = null,
    decimal? CacheWritePerMillion = null)
{
    /// <summary>
    /// Prices the four token lanes. A missing cache-write rate falls back to
    /// the input rate (most providers bill cache writes as ordinary input; an
    /// explicit 0 means free); a missing cache-read rate prices that lane at
    /// zero because the read discount cannot be guessed. Reasoning tokens fold
    /// into the output lane, matching <see cref="PricingEngine"/>.
    /// </summary>
    public decimal QuoteUsd(long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, long reasoningTokens = 0)
    {
        const decimal PerMillion = 1000000m;
        return inputTokens * InputPerMillion / PerMillion
            + (outputTokens + reasoningTokens) * OutputPerMillion / PerMillion
            + cacheReadTokens * CacheReadPerMillion.GetValueOrDefault() / PerMillion
            + cacheWriteTokens * (CacheWritePerMillion ?? InputPerMillion) / PerMillion;
    }
}

/// <summary>
/// User-entered per-model pricing keyed by (service provider id, raw model id)
/// — the identity usage rows actually carry. Overrides only ever fill gaps:
/// a model the catalog prices is never shadowed by a manual rate.
/// </summary>
public sealed class ManualPriceOverrideSet
{
    public static readonly ManualPriceOverrideSet Empty = new ManualPriceOverrideSet(new Dictionary<(string, string), ManualModelPrice>());

    private readonly Dictionary<(string Service, string Model), ManualModelPrice> _prices;

    private ManualPriceOverrideSet(Dictionary<(string, string), ManualModelPrice> prices)
    {
        _prices = prices;
    }

    public int Count => _prices.Count;

    public IReadOnlyCollection<(string Service, string Model)> Keys => _prices.Keys;

    public bool TryGet(string serviceId, string rawModelId, out ManualModelPrice price)
    {
        price = null!;
        return !string.IsNullOrWhiteSpace(serviceId)
            && !string.IsNullOrWhiteSpace(rawModelId)
            && _prices.TryGetValue(Key(serviceId, rawModelId), out price!);
    }

    public ManualPriceOverrideSet With(string serviceId, string rawModelId, ManualModelPrice? price)
    {
        if (string.IsNullOrWhiteSpace(serviceId) || string.IsNullOrWhiteSpace(rawModelId))
        {
            return this;
        }
        var prices = new Dictionary<(string, string), ManualModelPrice>(_prices);
        if (price is null || price.InputPerMillion < 0m || price.OutputPerMillion < 0m
            || (price.InputPerMillion == 0m && price.OutputPerMillion == 0m))
        {
            prices.Remove(Key(serviceId, rawModelId));
        }
        else
        {
            prices[Key(serviceId, rawModelId)] = price;
        }
        return new ManualPriceOverrideSet(prices);
    }

    public static ManualPriceOverrideSet Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Empty;
            }
            var prices = new Dictionary<(string, string), ManualModelPrice>();
            foreach (JsonProperty service in document.RootElement.EnumerateObject())
            {
                if (service.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                foreach (JsonProperty model in service.Value.EnumerateObject())
                {
                    ManualModelPrice? price = ReadPrice(model.Value);
                    if (price is not null && !string.IsNullOrWhiteSpace(service.Name) && !string.IsNullOrWhiteSpace(model.Name))
                    {
                        prices[Key(service.Name, model.Name)] = price;
                    }
                }
            }
            return prices.Count == 0 ? Empty : new ManualPriceOverrideSet(prices);
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    public string Serialize()
    {
        var document = new SortedDictionary<string, SortedDictionary<string, Dictionary<string, decimal>>>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((service, model), price) in _prices)
        {
            if (!document.TryGetValue(service, out var models))
            {
                models = new SortedDictionary<string, Dictionary<string, decimal>>(StringComparer.OrdinalIgnoreCase);
                document[service] = models;
            }
            var lanes = new Dictionary<string, decimal>
            {
                ["inputPer1M"] = price.InputPerMillion,
                ["outputPer1M"] = price.OutputPerMillion
            };
            if (price.CacheReadPerMillion is decimal cacheRead)
            {
                lanes["cacheReadPer1M"] = cacheRead;
            }
            if (price.CacheWritePerMillion is decimal cacheWrite)
            {
                lanes["cacheWritePer1M"] = cacheWrite;
            }
            document[service][model] = lanes;
        }
        return JsonSerializer.Serialize(document);
    }

    private static ManualModelPrice? ReadPrice(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        decimal input = ReadRate(element, "inputPer1M") ?? -1m;
        decimal output = ReadRate(element, "outputPer1M") ?? -1m;
        if (input < 0m || output < 0m || (input == 0m && output == 0m))
        {
            return null;
        }
        return new ManualModelPrice(input, output, ReadRate(element, "cacheReadPer1M"), ReadRate(element, "cacheWritePer1M"));
    }

    private static decimal? ReadRate(JsonElement element, string property)
    {
        return element.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDecimal(out decimal rate)
            && rate >= 0m
            ? rate
            : null;
    }

    private static (string, string) Key(string serviceId, string rawModelId)
    {
        return (serviceId.Trim().ToLowerInvariant(), rawModelId.Trim().ToLowerInvariant());
    }
}
