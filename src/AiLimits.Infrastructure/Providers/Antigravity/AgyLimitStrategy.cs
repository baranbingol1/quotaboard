// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Net;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Antigravity;

internal sealed class AgyLimitStrategy : ILimitFetchStrategy
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private const string GetUserStatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string QuotaSummaryPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private static readonly HttpClient LoopbackClient = CreateLoopbackClient();
    private readonly IClock _clock;
    private readonly Func<IReadOnlyList<int>> _findListeningPorts;
    private readonly HttpClient _httpClient;

    public AgyLimitStrategy(IClock clock, AgyProcessDiscovery processDiscovery)
        : this(clock, processDiscovery.FindListeningPorts, LoopbackClient)
    {
    }

    internal AgyLimitStrategy(
        IClock clock,
        Func<IReadOnlyList<int>> findListeningPorts,
        HttpClient httpClient)
    {
        _clock = clock;
        _findListeningPorts = findListeningPorts;
        _httpClient = httpClient;
    }

    public string Id => "antigravity.agy-local";

    public int Order => 10;

    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_findListeningPorts().Count > 0
            ? StrategyAvailabilityResult.Ready()
            : new StrategyAvailabilityResult(
                StrategyAvailability.TemporarilyUnavailable,
                "Start an authenticated agy session to read Google AI subscription quota."));
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();
        IReadOnlyList<int> ports = _findListeningPorts();
        if (ports.Count == 0)
        {
            return FetchResult.Failure(
                FetchFailureKind.Unknown,
                "No running agy session was found.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started));
        }

        Exception? lastError = null;
        foreach (int port in ports)
        {
            try
            {
                JsonElement quota = await PostAsync(
                    port,
                    QuotaSummaryPath,
                    new { forceRefresh = true },
                    cancellationToken).ConfigureAwait(false);
                JsonElement? identity = null;
                try
                {
                    identity = await PostAsync(
                        port,
                        GetUserStatusPath,
                        new
                        {
                            metadata = new
                            {
                                ideName = "antigravity",
                                extensionName = "antigravity",
                                ideVersion = "unknown",
                                locale = "en"
                            }
                        },
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch
                {
                    // Quota remains useful when the optional identity request changes or times out.
                }

                ProviderSnapshot snapshot = BuildSnapshot(account.Key, quota, identity, _clock.UtcNow);
                return FetchResult.Success(snapshot, Id, Stopwatch.GetElapsedTime(started));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error)
            {
                lastError = error;
            }
        }

        return FetchResult.Failure(
            lastError switch
            {
                JsonException => FetchFailureKind.MalformedResponse,
                OperationCanceledException => FetchFailureKind.Timeout,
                _ => FetchFailureKind.Network
            },
            "The running agy session did not expose readable subscription quota.",
            FallbackPolicy.TryNextStrategy,
            Id,
            Stopwatch.GetElapsedTime(started));
    }

    internal ProviderSnapshot BuildSnapshot(
        AccountKey account,
        JsonElement quotaRoot,
        JsonElement? identityRoot,
        DateTimeOffset observedAt)
    {
        JsonElement payload = ResolveQuotaPayload(quotaRoot);
        if (!payload.TryGetProperty("groups", out JsonElement groups) || groups.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("Missing quota groups.");
        }

        List<UsageMeter> meters = new();
        foreach (JsonElement group in groups.EnumerateArray())
        {
            if (group.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("Invalid quota group.");
            }
            string groupName = ReadString(group, "displayName") ?? "Quota";
            if (!group.TryGetProperty("buckets", out JsonElement buckets) || buckets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (JsonElement bucket in buckets.EnumerateArray())
            {
                if (bucket.ValueKind != JsonValueKind.Object)
                {
                    throw new JsonException("Invalid quota bucket.");
                }
                string? bucketId = ReadString(bucket, "bucketId")?.Trim();
                if (string.IsNullOrWhiteSpace(bucketId) || ReadBool(bucket, "disabled"))
                {
                    continue;
                }

                double? remainingFraction = ReadDouble(bucket, "remainingFraction")
                    ?? ReadNestedDouble(bucket, "remaining", "remainingFraction")
                    ?? ReadOneOfRemainingFraction(bucket);
                if (!remainingFraction.HasValue)
                {
                    continue;
                }
                if (!double.IsFinite(remainingFraction.Value)
                    || remainingFraction.Value is < 0 or > 1)
                {
                    throw new JsonException("Invalid remaining quota fraction.");
                }

                double usedPercent = 100 - remainingFraction.Value * 100;
                string bucketName = ReadString(bucket, "displayName") ?? bucketId;
                TimeSpan? duration = ResolveWindowDuration(bucketId, bucketName);
                DateTimeOffset? resetsAt = ReadDate(bucket, "resetTime");
                string displayName = $"{FriendlyGroup(groupName)} {FriendlyWindow(bucketName, duration)}";
                meters.Add(new UsageMeter(
                    new MeterKey($"antigravity:{bucketId}"),
                    displayName,
                    MeterScope.Feature,
                    MeterUnit.Percent,
                    (decimal)usedPercent,
                    100m,
                    usedPercent,
                    duration,
                    resetsAt,
                    null,
                    StatusFor(usedPercent),
                    new MeterProvenance(
                        Id,
                        $"$.groups[{groupName}].buckets[{bucketId}]",
                        observedAt,
                        IsAuthoritative: true)));
            }
        }

        if (meters.Count == 0)
        {
            throw new JsonException("No usable quota buckets.");
        }

        (string? Email, string? Plan) identity = ReadIdentity(identityRoot);
        List<(string Key, string Value)> extensions = new() { ("source", "agy-local") };
        if (!string.IsNullOrWhiteSpace(identity.Email)) extensions.Add(("email", identity.Email));
        if (!string.IsNullOrWhiteSpace(identity.Plan)) extensions.Add(("plan_type", identity.Plan));
        return new ProviderSnapshot(
            account,
            meters,
            Array.Empty<BalanceMetric>(),
            SnapshotCompleteness.Authoritative,
            observedAt,
            DataConfidence.High,
            ProviderHttpSupport.SafeExtensions(extensions.ToArray()));
    }

    private async Task<JsonElement> PostAsync(
        int port,
        string path,
        object body,
        CancellationToken cancellationToken)
    {
        if (!_findListeningPorts().Contains(port))
        {
            throw new HttpRequestException("The agy listener is no longer available.");
        }

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using HttpRequestMessage request = new(HttpMethod.Post, $"https://127.0.0.1:{port}{path}");
        request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        using HttpResponseMessage response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"agy returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(
            stream,
            default,
            timeout.Token).ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static HttpClient CreateLoopbackClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { Scheme: "https", Host: "127.0.0.1" }
        };
        return new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static JsonElement ResolveQuotaPayload(JsonElement root)
    {
        if (root.TryGetProperty("code", out JsonElement code) && !IsSuccessCode(code))
        {
            throw new JsonException("agy quota request failed.");
        }
        if (root.TryGetProperty("response", out JsonElement response) && response.ValueKind == JsonValueKind.Object)
        {
            return response;
        }
        if (root.TryGetProperty("summary", out JsonElement summary) && summary.ValueKind == JsonValueKind.Object)
        {
            return summary;
        }
        return root;
    }

    private static bool IsSuccessCode(JsonElement code)
    {
        if (code.ValueKind == JsonValueKind.Number)
        {
            return code.TryGetInt32(out int value) && value == 0;
        }
        if (code.ValueKind != JsonValueKind.String) return false;
        string? valueText = code.GetString();
        return valueText == "0"
            || string.Equals(valueText, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(valueText, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Email, string? Plan) ReadIdentity(JsonElement? root)
    {
        if (!root.HasValue
            || root.Value.ValueKind != JsonValueKind.Object
            || !root.Value.TryGetProperty("userStatus", out JsonElement status)
            || status.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }
        string? email = ReadString(status, "email")?.Trim();
        string? plan = ReadNestedString(status, "userTier", "name")
            ?? ReadNestedString(status, "planStatus", "planInfo", "planDisplayName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "displayName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "productName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "planName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "planShortName");
        return (email, plan?.Trim());
    }

    private static string FriendlyGroup(string value)
    {
        if (value.Contains("gemini", StringComparison.OrdinalIgnoreCase)) return "Gemini";
        if (value.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gpt", StringComparison.OrdinalIgnoreCase)) return "Claude/GPT";
        return value.Trim();
    }

    private static string FriendlyWindow(string value, TimeSpan? duration)
    {
        if (duration == TimeSpan.FromHours(5)) return "5-hour";
        if (duration == TimeSpan.FromDays(7)) return "weekly";
        return value.Trim();
    }

    private static TimeSpan? ResolveWindowDuration(string bucketId, string displayName)
    {
        string candidate = $"{bucketId} {displayName}".Replace('_', '-').ToLowerInvariant();
        if (candidate.Contains("weekly", StringComparison.Ordinal)) return TimeSpan.FromDays(7);
        string[] sessionAliases = { "session", "5h", "5-hour", "five hour", "five-hour" };
        return sessionAliases.Any(alias => candidate.Contains(alias, StringComparison.Ordinal))
            ? TimeSpan.FromHours(5)
            : null;
    }

    private static MeterStatus StatusFor(double usedPercent) => usedPercent switch
    {
        >= 100 => MeterStatus.Exhausted,
        >= 95 => MeterStatus.Critical,
        >= 80 => MeterStatus.Approaching,
        _ => MeterStatus.Healthy
    };

    private static double? ReadOneOfRemainingFraction(JsonElement bucket)
    {
        if (!bucket.TryGetProperty("remaining", out JsonElement remaining)
            || remaining.ValueKind != JsonValueKind.Object
            || ReadString(remaining, "case") != "remainingFraction")
        {
            return null;
        }
        return ReadDouble(remaining, "value");
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? ReadDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double number)
            ? number
            : null;

    private static double? ReadNestedDouble(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadDouble(nested, name)
            : null;

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string part in path)
        {
            if (!current.TryGetProperty(part, out current) || current.ValueKind != JsonValueKind.Object && part != path[^1])
            {
                return null;
            }
        }
        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static bool ReadBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

    private static DateTimeOffset? ReadDate(JsonElement element, string name)
    {
        string? value = ReadString(element, name);
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset date))
        {
            return date;
        }
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double seconds))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)(seconds * 1000));
        }
        return null;
    }
}
