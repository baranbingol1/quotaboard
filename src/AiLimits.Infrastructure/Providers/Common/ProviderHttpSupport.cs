// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;
using System.Net;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Common;

internal static class ProviderHttpSupport
{
    public static FetchResult FailureFor(HttpStatusCode status, string strategyId, TimeSpan duration)
    {
        FetchResult result = status switch
        {
            HttpStatusCode.Unauthorized => FetchResult.Failure(FetchFailureKind.Authentication, "Provider credentials expired or were rejected.", FallbackPolicy.TryNextStrategy, strategyId, duration), 
            HttpStatusCode.Forbidden => FetchResult.Failure(FetchFailureKind.Authorization, "The connected account is not authorized to read usage.", FallbackPolicy.TryNextStrategy, strategyId, duration), 
            HttpStatusCode.TooManyRequests => FetchResult.Failure(FetchFailureKind.RateLimited, "The provider rate-limited this refresh.", FallbackPolicy.Stop, strategyId, duration), 
            _ => FetchResult.Failure(FetchFailureKind.Network, $"Provider returned HTTP {(int)status}.", FallbackPolicy.TryNextStrategy, strategyId, duration), 
        };
        return result;
    }

    public static IReadOnlyDictionary<string, JsonElement> SafeExtensions(params (string Key, string Value)[] values)
    {
        Dictionary<string, JsonElement> dictionary = new Dictionary<string, JsonElement>();
        for (int i = 0; i < values.Length; i++)
        {
            (string, string) tuple = values[i];
            dictionary[tuple.Item1] = JsonSerializer.SerializeToElement(tuple.Item2);
        }
        return dictionary;
    }
}
