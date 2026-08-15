// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Cursor;

/// <summary>
/// Streams per-request token usage from cursor.com's dashboard usage-events
/// endpoint, authenticated with the same local app-session cookie the limit
/// strategy uses. The endpoint is undocumented and pages newest-first with no
/// server-side date filtering (date parameters are ignored by the current
/// deployment), so a scan pages forward until it reaches the last event the
/// previous scan recorded, then emits everything newer in ascending order —
/// the scan loop persists only the newest emitted timestamp, and ascending
/// emission keeps that cursor correct. On any mid-scan failure the whole scan
/// yields nothing rather than emitting a window with a gap below it.
/// </summary>
internal sealed class CursorUsageEventsTokenSource(
    HttpClient httpClient,
    IClock clock,
    CursorCredentialSource credentialSource
) : ITokenUsageSource
{
    private static readonly Uri UsageEventsUri = new("https://cursor.com/api/dashboard/get-filtered-usage-events");

    private const int PageSize = 100;

    // Hard bound per scan (~10000 events). Scans run every minute, so only the
    // very first backfill (or a catch-up after weeks offline) can realistically
    // hit this; if it does, the oldest part of the window is skipped once and
    // monitoring continues from there (documented, preferable to never
    // catching up).
    private const int MaxPagesPerScan = 100;

    private static readonly TimeSpan InitialBackfill = TimeSpan.FromDays(30);

    public string Id => "cursor.usage-events";

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        CursorCredential? credential = await credentialSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (credential is null || !string.Equals(credential.Subject, account.Key.Value, StringComparison.Ordinal))
        {
            yield break;
        }
        DateTimeOffset boundary = cursor?.LastObservedAt ?? clock.UtcNow - InitialBackfill;
        string cookieHeader = CursorWebLimitStrategy.BuildCookieHeader(credential);
        List<TokenUsageEvent> collected = new List<TokenUsageEvent>();
        bool reachedBoundary = false;
        for (int page = 1; page <= MaxPagesPerScan && !reachedBoundary; page++)
        {
            using ProviderJsonResult exchange = await FetchPageAsync(page, cookieHeader, cancellationToken)
                .ConfigureAwait(false);
            if (!exchange.IsSuccess)
            {
                // All-or-nothing: emitting the newer half of a window whose
                // older half failed would advance the cursor past a gap.
                // Thrown rather than yielded away so the scan loop records a
                // failure instead of "no new events" — nothing has been
                // emitted yet, so the cursor still cannot advance past a gap.
                throw new TokenScanException(exchange.Failure!.SafeMessage);
            }
            if (
                !exchange.Document!.RootElement.TryGetProperty("usageEventsDisplay", out JsonElement events)
                || events.ValueKind != JsonValueKind.Array
            )
            {
                throw new TokenScanException("Cursor returned an unrecognized usage-events response.");
            }
            int received = 0;
            foreach (JsonElement entry in events.EnumerateArray())
            {
                received++;
                if (!TryReadTimestamp(entry, out DateTimeOffset occurredAt))
                {
                    continue;
                }
                if (occurredAt < boundary)
                {
                    reachedBoundary = true;
                    break;
                }
                if (occurredAt == boundary)
                {
                    // The prior scan's newest millisecond may hold events that
                    // were written after that scan. Keep collecting at exactly
                    // the boundary timestamp — re-seen events carry the same
                    // content-hash id and are dropped by fingerprint dedup.
                    reachedBoundary = true;
                }
                if (TryMapEvent(account, entry, occurredAt, out TokenUsageEvent? mapped))
                {
                    collected.Add(mapped!);
                }
            }
            if (received < PageSize)
            {
                break;
            }
        }
        foreach (
            TokenUsageEvent tokenEvent in collected
                .OrderBy(item => item.OccurredAt)
                .ThenBy(item => item.SourceEventId, StringComparer.Ordinal)
        )
        {
            yield return tokenEvent;
        }
    }

    private async Task<ProviderJsonResult> FetchPageAsync(
        int page,
        string cookieHeader,
        CancellationToken cancellationToken
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, UsageEventsUri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        // The dashboard API rejects state-changing requests without a
        // first-party Origin.
        request.Headers.TryAddWithoutValidation("Origin", "https://cursor.com");
        request.Headers.Referrer = new Uri("https://cursor.com/dashboard");
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new Dictionary<string, object> { ["page"] = page, ["pageSize"] = PageSize }),
            Encoding.UTF8,
            "application/json"
        );
        return await ProviderHttp
            .GetJsonAsync(httpClient, request, Id, "Cursor", Stopwatch.GetTimestamp(), cancellationToken)
            .ConfigureAwait(false);
    }

    private static bool TryMapEvent(
        ProviderAccount account,
        JsonElement entry,
        DateTimeOffset occurredAt,
        out TokenUsageEvent? mapped
    )
    {
        mapped = null;
        // Events without token usage (non-token-based calls) carry nothing for
        // the usage pipeline; skipping one bad event never fails the batch.
        if (
            !entry.TryGetProperty("tokenUsage", out JsonElement tokenUsage)
            || tokenUsage.ValueKind != JsonValueKind.Object
        )
        {
            return false;
        }
        long input = ReadTokenCount(tokenUsage, "inputTokens");
        long output = ReadTokenCount(tokenUsage, "outputTokens");
        long cacheRead = ReadTokenCount(tokenUsage, "cacheReadTokens");
        long cacheWrite = ReadTokenCount(tokenUsage, "cacheWriteTokens");
        if (input == 0 && output == 0 && cacheRead == 0 && cacheWrite == 0)
        {
            return false;
        }
        string model =
            entry.TryGetProperty("model", out JsonElement modelElement)
            && modelElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(modelElement.GetString())
                ? modelElement.GetString()!.Trim()
                : "unknown";
        string conversation =
            entry.TryGetProperty("conversationId", out JsonElement conversationElement)
            && conversationElement.ValueKind == JsonValueKind.String
                ? conversationElement.GetString() ?? string.Empty
                : string.Empty;
        // The API exposes no per-event id; timestamp + a content hash is stable
        // across reads of the same event.
        string sourceEventId =
            $"cursor:{occurredAt.ToUnixTimeMilliseconds()}:{Hash16($"{model}|{input}|{output}|{cacheRead}|{cacheWrite}|{conversation}")}";
        mapped = new TokenUsageEvent(
            account.Key,
            new ServiceProviderId("cursor"),
            model,
            occurredAt,
            input,
            output,
            cacheRead,
            cacheWrite,
            ReasoningTokens: 0,
            sourceEventId,
            ProjectIdentity.Unknown
        );
        return true;
    }

    private static bool TryReadTimestamp(JsonElement entry, out DateTimeOffset occurredAt)
    {
        occurredAt = default;
        if (!entry.TryGetProperty("timestamp", out JsonElement timestamp))
        {
            return false;
        }
        long unixMs;
        if (timestamp.ValueKind == JsonValueKind.Number && timestamp.TryGetInt64(out long numeric))
        {
            unixMs = numeric;
        }
        else if (
            timestamp.ValueKind == JsonValueKind.String
            && long.TryParse(
                timestamp.GetString(),
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out long parsed
            )
        )
        {
            unixMs = parsed;
        }
        else
        {
            return false;
        }
        if (unixMs <= 0)
        {
            return false;
        }
        occurredAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
        return true;
    }

    private static long ReadTokenCount(JsonElement tokenUsage, string property)
    {
        return
            tokenUsage.TryGetProperty(property, out JsonElement value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt64(out long count)
            && count > 0
            ? count
            : 0L;
    }

    private static string Hash16(string value)
    {
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(Encoding.UTF8.GetBytes(value), digest);
        return Convert.ToHexStringLower(digest[..8]);
    }
}
