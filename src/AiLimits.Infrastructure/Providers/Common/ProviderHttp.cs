// SPDX-License-Identifier: Apache-2.0
using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiLimits.Domain;

namespace AiLimits.Infrastructure.Providers.Common;

internal sealed record ProviderHttpOptions(TimeSpan? Timeout = null, long MaxResponseBytes = ProviderHttp.DefaultMaxResponseBytes);

/// <summary>
/// Outcome of one provider HTTP+JSON exchange: either a parsed document or a
/// fully classified <see cref="FetchResult"/> failure ready to return from a
/// strategy. Dispose to release the document.
/// </summary>
internal sealed class ProviderJsonResult : IDisposable
{
    private ProviderJsonResult(JsonDocument? document, FetchResult? failure)
    {
        Document = document;
        Failure = failure;
    }

    public JsonDocument? Document { get; }

    public FetchResult? Failure { get; }

    public bool IsSuccess => Document is not null;

    public static ProviderJsonResult Success(JsonDocument document) => new ProviderJsonResult(document, null);

    public static ProviderJsonResult FromFailure(FetchResult failure) => new ProviderJsonResult(null, failure);

    public void Dispose() => Document?.Dispose();
}

/// <summary>
/// The one shared provider HTTP contract. Every provider request goes through
/// here so that authentication (401), authorization (403), rate limiting
/// (429 + typed Retry-After), server errors (5xx), network failures, internal
/// timeouts (distinct from caller cancellation), malformed JSON, and oversized
/// responses are classified identically for all providers, with bounded
/// response sizes and bounded operation duration. Caller cancellation always
/// propagates as <see cref="OperationCanceledException"/>; the internal
/// timeout never does.
/// </summary>
internal static class ProviderHttp
{
    public const long DefaultMaxResponseBytes = 2_000_000;

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    public static async Task<ProviderJsonResult> GetJsonAsync(
        HttpClient client,
        HttpRequestMessage request,
        string strategyId,
        string providerLabel,
        long startedTimestamp,
        CancellationToken cancellationToken,
        ProviderHttpOptions? options = null)
    {
        options ??= new ProviderHttpOptions();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout ?? DefaultTimeout);
        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return Fail(FetchFailureKind.Timeout, providerLabel + " did not respond in time.", FallbackPolicy.TryNextStrategy, strategyId, startedTimestamp);
        }
        catch (HttpRequestException)
        {
            return Fail(FetchFailureKind.Network, providerLabel + " could not be reached.", FallbackPolicy.TryNextStrategy, strategyId, startedTimestamp);
        }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                return ProviderJsonResult.FromFailure(FailureForResponse(response, strategyId, providerLabel, startedTimestamp));
            }
            byte[]? body;
            try
            {
                body = await ReadBoundedContentAsync(response.Content, options.MaxResponseBytes, timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return Fail(FetchFailureKind.Timeout, providerLabel + " did not respond in time.", FallbackPolicy.TryNextStrategy, strategyId, startedTimestamp);
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException)
            {
                return Fail(FetchFailureKind.Network, providerLabel + " connection failed while reading the response.", FallbackPolicy.TryNextStrategy, strategyId, startedTimestamp);
            }
            if (body is null)
            {
                return Fail(FetchFailureKind.OversizedResponse, providerLabel + " returned an unexpectedly large response.", FallbackPolicy.TryNextStrategy, strategyId, startedTimestamp);
            }
            try
            {
                return ProviderJsonResult.Success(JsonDocument.Parse(body));
            }
            catch (JsonException)
            {
                return Fail(FetchFailureKind.MalformedResponse, providerLabel + " returned malformed JSON.", FallbackPolicy.TryNextStrategy, strategyId, startedTimestamp);
            }
        }
    }

    internal static FetchResult FailureForResponse(HttpResponseMessage response, string strategyId, string providerLabel, long startedTimestamp)
    {
        TimeSpan elapsed = System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp);
        TimeSpan? retryAfter = ParseRetryAfter(response.Headers);
        int status = (int)response.StatusCode;
        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => FetchResult.Failure(FetchFailureKind.Authentication, providerLabel + " credentials expired or were rejected.", FallbackPolicy.TryNextStrategy, strategyId, elapsed),
            HttpStatusCode.Forbidden => FetchResult.Failure(FetchFailureKind.Authorization, "The connected account is not authorized to read " + providerLabel + " usage.", FallbackPolicy.TryNextStrategy, strategyId, elapsed),
            HttpStatusCode.TooManyRequests => FetchResult.Failure(FetchFailureKind.RateLimited, providerLabel + " rate-limited this refresh.", FallbackPolicy.Stop, strategyId, elapsed, retryAfter),
            _ when status >= 500 => FetchResult.Failure(FetchFailureKind.Network, $"{providerLabel} reported a server error (HTTP {status}).", FallbackPolicy.TryNextStrategy, strategyId, elapsed, retryAfter),
            _ => FetchResult.Failure(FetchFailureKind.Network, $"{providerLabel} returned HTTP {status}.", FallbackPolicy.TryNextStrategy, strategyId, elapsed),
        };
    }

    internal static TimeSpan? ParseRetryAfter(HttpResponseHeaders headers)
    {
        RetryConditionHeaderValue? retryAfter = headers.RetryAfter;
        if (retryAfter is null)
        {
            return null;
        }
        if (retryAfter.Delta is TimeSpan delta)
        {
            return delta >= TimeSpan.Zero ? delta : null;
        }
        if (retryAfter.Date is DateTimeOffset date)
        {
            TimeSpan untilDate = date - DateTimeOffset.UtcNow;
            return untilDate > TimeSpan.Zero ? untilDate : TimeSpan.Zero;
        }
        return null;
    }

    private static ProviderJsonResult Fail(FetchFailureKind kind, string safeMessage, FallbackPolicy fallback, string strategyId, long startedTimestamp)
    {
        return ProviderJsonResult.FromFailure(FetchResult.Failure(kind, safeMessage, fallback, strategyId, System.Diagnostics.Stopwatch.GetElapsedTime(startedTimestamp)));
    }

    /// <summary>Reads at most <paramref name="maxBytes"/>; returns null when the response is larger.</summary>
    internal static async Task<byte[]?> ReadBoundedContentAsync(HttpContent content, long maxBytes, CancellationToken cancellationToken)
    {
        await using Stream stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new MemoryStream();
        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            {
                buffer.Write(rented, 0, read);
                if (buffer.Length > maxBytes)
                {
                    return null;
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
        return buffer.ToArray();
    }
}
