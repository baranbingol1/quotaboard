// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Application.Pricing;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using System.Net.Http.Headers;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AiLimits.Infrastructure.Pricing;

public sealed class ModelsDevPricingCatalog : IPricingCatalog, IDisposable
{
    private sealed record CatalogMetadata(string Hash, DateTimeOffset FetchedAt, string? ETag, DateTimeOffset? LastModified);

    private sealed record CachedCatalog(PricingCatalogSnapshot Snapshot, CatalogMetadata Metadata);

    public static readonly Uri CatalogUri = new Uri("https://models.dev/api.json");

    /// <summary>
    /// Response cap for the catalog body. This is deliberately NOT
    /// <see cref="ProviderHttp.DefaultMaxResponseBytes"/> (2 MB): that bound
    /// guards small provider API replies, whereas api.json is a bulk data file
    /// that was already 3.2 MB in July 2026 and grows as models are added.
    /// Applying the 2 MB bound here silently froze the catalog — an over-cap
    /// read returns null, which falls back to the cached snapshot forever.
    /// </summary>
    internal const long MaxCatalogBytes = 32L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;

    private readonly string _bodyPath;

    private readonly string _metadataPath;

    private readonly IClock _clock;

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);

    private string? _lastError;

    private DateTimeOffset? _lastAttemptAt;

    public string? LastError => _lastError;

    public DateTimeOffset? LastAttemptAt => _lastAttemptAt;

    public ModelsDevPricingCatalog(HttpClient httpClient, string cacheDirectory, IClock clock)
    {
        _httpClient = httpClient;
        _clock = clock;
        Directory.CreateDirectory(cacheDirectory);
        _bodyPath = Path.Combine(cacheDirectory, "models-dev-api.json");
        _metadataPath = Path.Combine(cacheDirectory, "models-dev-api.meta.json");
    }

    public async Task<PricingCatalogSnapshot?> GetCurrentAsync(CancellationToken cancellationToken)
    {
        return (await LoadAsync(cancellationToken).ConfigureAwait(false))?.Snapshot;
    }

    public async Task<PricingCatalogSnapshot?> RefreshIfStaleAsync(CancellationToken cancellationToken)
    {
        return (await RefreshAsync(force: false, cancellationToken).ConfigureAwait(false)).Snapshot;
    }

    public async Task<PricingCatalogRefresh> RefreshAsync(bool force, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CachedCatalog? cached = await LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!force && cached is not null)
            {
                // Twice a day, on wall-clock slots, unless the last attempt
                // failed recently — a failure does not advance FetchedAt, so
                // without the retry gap every background tick would retry.
                if (!PricingCatalogSchedule.IsDue(cached.Snapshot.FetchedAt, _clock.UtcNow))
                {
                    return new PricingCatalogRefresh(PricingCatalogOutcome.NotDue, cached.Snapshot);
                }
                if (_lastAttemptAt is { } attempted
                    && _clock.UtcNow - attempted < PricingCatalogSchedule.MinimumRetryGap)
                {
                    return new PricingCatalogRefresh(PricingCatalogOutcome.NotDue, cached.Snapshot, _lastError);
                }
            }

            using HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Get, CatalogUri);
            // Conditional headers make the common "nothing changed" case a
            // zero-byte 304 instead of a 3 MB download. Skipped on a forced
            // refresh so the button always re-reads the real body.
            if (!force && !string.IsNullOrWhiteSpace(cached?.Metadata.ETag))
            {
                request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(cached.Metadata.ETag));
            }
            if (!force && cached?.Metadata.LastModified is { } modified)
            {
                request.Headers.IfModifiedSince = modified;
            }

            _lastAttemptAt = _clock.UtcNow;
            try
            {
                using HttpResponseMessage response = await _httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
                {
                    CatalogMetadata revalidated = cached.Metadata with { FetchedAt = _clock.UtcNow };
                    await WriteMetadataAsync(revalidated, cancellationToken).ConfigureAwait(false);
                    _lastError = null;
                    return new PricingCatalogRefresh(
                        PricingCatalogOutcome.Unchanged,
                        cached.Snapshot with { FetchedAt = revalidated.FetchedAt });
                }

                response.EnsureSuccessStatusCode();
                byte[]? bytes = await ProviderHttp
                    .ReadBoundedContentAsync(response.Content, MaxCatalogBytes, cancellationToken)
                    .ConfigureAwait(false);
                if (bytes is null)
                {
                    return Fail($"Catalog exceeded the {MaxCatalogBytes / (1024 * 1024)} MB limit", cached);
                }

                IReadOnlyDictionary<(string Provider, string Model), ModelPrice> index = ParseAndValidate(bytes);
                CatalogMetadata metadata = new CatalogMetadata(
                    Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(),
                    _clock.UtcNow,
                    response.Headers.ETag?.ToString(),
                    response.Content.Headers.LastModified ?? response.Headers.Date);
                await AtomicWriteAsync(_bodyPath, bytes, cancellationToken).ConfigureAwait(false);
                await WriteMetadataAsync(metadata, cancellationToken).ConfigureAwait(false);
                _lastError = null;
                return new PricingCatalogRefresh(
                    PricingCatalogOutcome.Updated,
                    new PricingCatalogSnapshot(metadata.Hash, metadata.FetchedAt, metadata.ETag, index));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Never rethrow: a catalog outage must not fail the refresh
                // that carries provider quota. But never swallow silently
                // either — the reason is what Settings shows.
                return Fail(Describe(ex), cached);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private PricingCatalogRefresh Fail(string error, CachedCatalog? cached)
    {
        _lastError = error;
        return new PricingCatalogRefresh(PricingCatalogOutcome.Failed, cached?.Snapshot, error);
    }

    private static string Describe(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } status } => $"models.dev returned HTTP {(int)status}",
        HttpRequestException => "Could not reach models.dev",
        TaskCanceledException => "models.dev timed out",
        InvalidDataException => $"Catalog rejected: {ex.Message}",
        JsonException => "Catalog was not valid JSON",
        IOException => $"Could not write the catalog cache: {ex.Message}",
        _ => ex.Message
    };

    public static IReadOnlyDictionary<(string Provider, string Model), ModelPrice> ParseAndValidate(ReadOnlySpan<byte> body)
    {
        using JsonDocument jsonDocument = JsonDocument.Parse(body.ToArray());
        if (jsonDocument.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("models.dev catalog root must be an object.");
        }
        Dictionary<(string, string), ModelPrice> dictionary = new Dictionary<(string, string), ModelPrice>();
        foreach (JsonProperty item in jsonDocument.RootElement.EnumerateObject())
        {
            if (!item.Value.TryGetProperty("models", out var value) || value.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            foreach (JsonProperty item2 in value.EnumerateObject())
            {
                if (item2.Value.TryGetProperty("cost", out var value2) && value2.ValueKind == JsonValueKind.Object)
                {
                    decimal? inputPerMillion = ReadRate(value2, "input");
                    decimal? outputPerMillion = ReadRate(value2, "output");
                    decimal? cacheReadPerMillion = ReadRate(value2, "cache_read");
                    decimal? cacheWritePerMillion = ReadRate(value2, "cache_write");
                    decimal? reasoningPerMillion = ReadRate(value2, "reasoning");
                    decimal? longContextInputPerMillion = null;
                    decimal? longContextOutputPerMillion = null;
                    if (value2.TryGetProperty("context_over_200k", out var value3) && value3.ValueKind == JsonValueKind.Object)
                    {
                        longContextInputPerMillion = ReadRate(value3, "input");
                        longContextOutputPerMillion = ReadRate(value3, "output");
                    }
                    if (inputPerMillion.HasValue || outputPerMillion.HasValue || cacheReadPerMillion.HasValue || cacheWritePerMillion.HasValue || reasoningPerMillion.HasValue)
                    {
                        string text = item.Name.Trim().ToLowerInvariant();
                        string text2 = item2.Name.Trim();
                        // Index keys are lowercase on both sides: models.dev mixes
                        // casings per host ("MiniMax-M3" vs "minimax-m3") and local
                        // telemetry always reports lowercase ids.
                        dictionary[(text, text2.ToLowerInvariant())] = new ModelPrice(text, text2, inputPerMillion, outputPerMillion, cacheReadPerMillion, cacheWritePerMillion, reasoningPerMillion, longContextInputPerMillion, longContextOutputPerMillion, (!longContextInputPerMillion.HasValue && !longContextOutputPerMillion.HasValue) ? null : 200000L);
                    }
                }
            }
        }
        if (dictionary.Count == 0)
        {
            throw new InvalidDataException("models.dev catalog did not contain a priced model.");
        }
        return dictionary;
    }

    public void Dispose()
    {
        _gate.Dispose();
    }

    private async Task<CachedCatalog?> LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_bodyPath) || !File.Exists(_metadataPath))
        {
            return null;
        }
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(_bodyPath, cancellationToken).ConfigureAwait(false);
            CatalogMetadata metadata = JsonSerializer.Deserialize<CatalogMetadata>(await File.ReadAllTextAsync(_metadataPath, cancellationToken).ConfigureAwait(false), JsonOptions);
            if (metadata is null || !string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), metadata.Hash, StringComparison.Ordinal))
            {
                return null;
            }
            return new CachedCatalog(new PricingCatalogSnapshot(metadata.Hash, metadata.FetchedAt, metadata.ETag, ParseAndValidate(bytes)), metadata);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private Task WriteMetadataAsync(CatalogMetadata metadata, CancellationToken cancellationToken)
    {
        return AtomicWriteAsync(_metadataPath, JsonSerializer.SerializeToUtf8Bytes(metadata, JsonOptions), cancellationToken);
    }

    private static async Task AtomicWriteAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        string temporary = path + ".tmp";
        await File.WriteAllBytesAsync(temporary, bytes, cancellationToken).ConfigureAwait(false);
        File.Move(temporary, path, overwrite: true);
    }

    private static decimal? ReadRate(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number || !value.TryGetDecimal(out var value2))
        {
            return null;
        }
        if (value2 < 0m)
        {
            throw new InvalidDataException("models.dev rate '" + name + "' cannot be negative.");
        }
        return value2;
    }
}
