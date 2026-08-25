// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Antigravity;

/// <summary>What one <c>agy</c> port was able to tell us.</summary>
internal sealed record AgyPayload(JsonElement? Quota, JsonElement? Identity);

/// <summary>
/// Reads Google subscription quota from the Antigravity CLI.
///
/// <c>agy</c> exposes the same Connect endpoints as the Antigravity desktop
/// language server, on loopback and without a CSRF token. It binds two ports and
/// serves TLS on one and plaintext on the other, with no way to tell which is
/// which ahead of time, so each port is tried under both schemes and the one
/// that answers is remembered.
///
/// <see cref="AgyCliSession"/> owns getting a CLI running; this strategy only
/// decides what to ask a reachable port and how to read the answer.
/// </summary>
internal sealed class AgyLimitStrategy : ILimitFetchStrategy
{
    private const int MaximumResponseBytes = 1024 * 1024;
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(4);
    private const string GetUserStatusPath = "/exa.language_server_pb.LanguageServerService/GetUserStatus";
    private const string QuotaSummaryPath = "/exa.language_server_pb.LanguageServerService/RetrieveUserQuotaSummary";
    private static readonly HttpClient LoopbackClient = CreateLoopbackClient();
    private static readonly string[] Schemes = ["https", "http"];

    private readonly IClock _clock;
    private readonly AgyCliSession _session;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<int, string> _schemeByPort = new();

    public AgyLimitStrategy(IClock clock, AgyCliSession session)
        : this(clock, session, LoopbackClient) { }

    internal AgyLimitStrategy(IClock clock, AgyCliSession session, HttpClient httpClient)
    {
        _clock = clock;
        _session = session;
        _httpClient = httpClient;
    }

    public string Id => "antigravity.agy-local";

    public int Order => 10;

    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken
    )
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            _session.IsUsable()
                ? StrategyAvailabilityResult.Ready()
                : new StrategyAvailabilityResult(
                    StrategyAvailability.NotConfigured,
                    "Install the Antigravity CLI and run agy once to sign in."
                )
        );
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        long started = Stopwatch.GetTimestamp();

        // A snapshot that builds is the readiness signal. A starting CLI answers
        // GetUserStatus about a second before its quota service is live, so
        // treating any response as ready would accept a payload with no quota in
        // it and report a malformed provider instead of waiting one more poll.
        AgyAcquireResult<ProviderSnapshot> acquired = await _session
            .AcquireAsync(
                async (port, token) =>
                {
                    AgyPayload? payload = await ReadPayloadAsync(port, token).ConfigureAwait(false);
                    if (payload is null)
                    {
                        return null;
                    }
                    try
                    {
                        return BuildSnapshot(account.Key, payload.Quota, payload.Identity, _clock.UtcNow);
                    }
                    catch (JsonException)
                    {
                        return null;
                    }
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        return acquired.Payload is null
            ? FetchResult.Failure(
                FetchFailureKind.Unknown,
                acquired.Failure == AgyAcquireFailure.NotInstalled
                    ? "The Antigravity CLI is not installed."
                    : "The Antigravity CLI did not expose readable subscription quota. Run agy once and sign in.",
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            )
            : FetchResult.Success(acquired.Payload, Id, Stopwatch.GetElapsedTime(started));
    }

    /// <summary>
    /// Asks one port for quota and identity. Returns null when the port serves
    /// neither, which is how a still-initializing CLI looks from outside.
    /// </summary>
    private async Task<AgyPayload?> ReadPayloadAsync(int port, CancellationToken cancellationToken)
    {
        JsonElement? quota = await TryPostAsync(port, QuotaSummaryPath, new { forceRefresh = true }, cancellationToken)
            .ConfigureAwait(false);
        JsonElement? identity = await TryPostAsync(
                port,
                GetUserStatusPath,
                new
                {
                    metadata = new
                    {
                        ideName = "antigravity",
                        extensionName = "antigravity",
                        ideVersion = "unknown",
                        locale = "en",
                    },
                },
                cancellationToken
            )
            .ConfigureAwait(false);

        // Identity alone is still worth having: older CLI builds answer
        // GetUserStatus with per-model quota but have no quota summary endpoint.
        return quota is null && identity is null ? null : new AgyPayload(quota, identity);
    }

    private async Task<JsonElement?> TryPostAsync(
        int port,
        string path,
        object body,
        CancellationToken cancellationToken
    )
    {
        foreach (string scheme in SchemesFor(port))
        {
            try
            {
                JsonElement result = await PostAsync(scheme, port, path, body, cancellationToken).ConfigureAwait(false);
                _schemeByPort[port] = scheme;
                return result;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception error) when (error is HttpRequestException or JsonException or OperationCanceledException)
            {
                // Wrong scheme, endpoint not implemented on this build, or the
                // CLI is not serving yet. The caller retries until it gives up.
            }
        }
        return null;
    }

    /// <summary>The scheme known to work for this port first, then the other one.</summary>
    private IEnumerable<string> SchemesFor(int port)
    {
        if (_schemeByPort.TryGetValue(port, out string? known))
        {
            yield return known;
            foreach (string scheme in Schemes)
            {
                if (!string.Equals(scheme, known, StringComparison.Ordinal))
                {
                    yield return scheme;
                }
            }
            yield break;
        }
        foreach (string scheme in Schemes)
        {
            yield return scheme;
        }
    }

    internal ProviderSnapshot BuildSnapshot(
        AccountKey account,
        JsonElement? quotaRoot,
        JsonElement? identityRoot,
        DateTimeOffset observedAt
    )
    {
        List<UsageMeter> meters = quotaRoot.HasValue
            ? ReadQuotaSummaryMeters(quotaRoot.Value, observedAt)
            : new List<UsageMeter>();
        if (meters.Count == 0)
        {
            meters = ReadModelConfigMeters(identityRoot, observedAt);
        }
        if (meters.Count == 0)
        {
            throw new JsonException("No usable quota buckets.");
        }

        (string? Email, string? Plan) identity = ReadIdentity(identityRoot);
        List<(string Key, string Value)> extensions = new() { ("source", "agy-cli") };
        if (!string.IsNullOrWhiteSpace(identity.Email))
            extensions.Add(("email", identity.Email));
        if (!string.IsNullOrWhiteSpace(identity.Plan))
            extensions.Add(("plan_type", identity.Plan));
        return new ProviderSnapshot(
            account,
            meters,
            ReadBalances(identityRoot),
            SnapshotCompleteness.Authoritative,
            observedAt,
            DataConfidence.High,
            ProviderHttpSupport.SafeExtensions(extensions.ToArray())
        );
    }

    /// <summary>The preferred source: the two grouped windows the CLI shows itself.</summary>
    private List<UsageMeter> ReadQuotaSummaryMeters(JsonElement quotaRoot, DateTimeOffset observedAt)
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

                double? remainingFraction =
                    ReadDouble(bucket, "remainingFraction")
                    ?? ReadNestedDouble(bucket, "remaining", "remainingFraction")
                    ?? ReadOneOfRemainingFraction(bucket);
                if (!remainingFraction.HasValue)
                {
                    continue;
                }
                if (!double.IsFinite(remainingFraction.Value) || remainingFraction.Value is < 0 or > 1)
                {
                    throw new JsonException("Invalid remaining quota fraction.");
                }

                double usedPercent = 100 - remainingFraction.Value * 100;
                string bucketName = ReadString(bucket, "displayName") ?? bucketId;
                TimeSpan? duration = ResolveWindowDuration(
                    ReadString(bucket, "window") ?? string.Empty,
                    bucketId,
                    bucketName
                );
                meters.Add(
                    BuildMeter(
                        $"antigravity:{bucketId}",
                        $"{FriendlyGroup(groupName)} {FriendlyWindow(bucketName, duration)}",
                        usedPercent,
                        duration,
                        ReadDate(bucket, "resetTime"),
                        $"$.groups[{groupName}].buckets[{bucketId}]",
                        observedAt
                    )
                );
            }
        }
        return meters;
    }

    /// <summary>
    /// Fallback for CLI builds with no quota summary endpoint: collapse the
    /// per-model rows of <c>GetUserStatus</c> into the same two families the
    /// summary reports. Antigravity bills every model in a family against one
    /// shared window, so the most-consumed row speaks for its family.
    /// </summary>
    private List<UsageMeter> ReadModelConfigMeters(JsonElement? identityRoot, DateTimeOffset observedAt)
    {
        if (
            identityRoot is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("userStatus", out JsonElement status)
            || status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("cascadeModelConfigData", out JsonElement configData)
            || configData.ValueKind != JsonValueKind.Object
            || !configData.TryGetProperty("clientModelConfigs", out JsonElement configs)
            || configs.ValueKind != JsonValueKind.Array
        )
        {
            return new List<UsageMeter>();
        }

        Dictionary<string, (double UsedPercent, DateTimeOffset? ResetsAt)> worstByFamily = new(StringComparer.Ordinal);
        foreach (JsonElement config in configs.EnumerateArray())
        {
            if (config.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            string family = FriendlyGroup($"{ReadString(config, "modelId")} {ReadString(config, "label")}");
            if (family.Length == 0)
            {
                continue;
            }
            if (
                !config.TryGetProperty("quotaInfo", out JsonElement quotaInfo)
                || quotaInfo.ValueKind != JsonValueKind.Object
            )
            {
                continue;
            }
            double? remainingFraction = ReadDouble(quotaInfo, "remainingFraction");
            if (
                !remainingFraction.HasValue
                || !double.IsFinite(remainingFraction.Value)
                || remainingFraction.Value is < 0 or > 1
            )
            {
                continue;
            }

            double usedPercent = 100 - remainingFraction.Value * 100;
            if (!worstByFamily.TryGetValue(family, out var current) || usedPercent > current.UsedPercent)
            {
                worstByFamily[family] = (usedPercent, ReadDate(quotaInfo, "resetTime"));
            }
        }

        // Per-model rows only ever carry the session window; the weekly window
        // exists solely in the quota summary, so it is left out rather than guessed.
        return worstByFamily
            .OrderBy(entry => entry.Key, StringComparer.Ordinal)
            .Select(entry =>
                BuildMeter(
                    $"antigravity:{entry.Key.ToLowerInvariant()}-5h",
                    $"{entry.Key} 5-hour",
                    entry.Value.UsedPercent,
                    TimeSpan.FromHours(5),
                    entry.Value.ResetsAt,
                    "$.userStatus.cascadeModelConfigData.clientModelConfigs[].quotaInfo",
                    observedAt
                )
            )
            .ToList();
    }

    private UsageMeter BuildMeter(
        string key,
        string displayName,
        double usedPercent,
        TimeSpan? duration,
        DateTimeOffset? resetsAt,
        string sourcePath,
        DateTimeOffset observedAt
    ) =>
        new(
            new MeterKey(key),
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
            new MeterProvenance(Id, sourcePath, observedAt, IsAuthoritative: true)
        );

    /// <summary>
    /// Prompt and flow credits, which are a separate purse from the rate-limit
    /// windows above and so are reported as balances rather than meters.
    /// </summary>
    private static IReadOnlyList<BalanceMetric> ReadBalances(JsonElement? identityRoot)
    {
        if (
            identityRoot is not { ValueKind: JsonValueKind.Object } root
            || !root.TryGetProperty("userStatus", out JsonElement status)
            || status.ValueKind != JsonValueKind.Object
            || !status.TryGetProperty("planStatus", out JsonElement planStatus)
            || planStatus.ValueKind != JsonValueKind.Object
        )
        {
            return Array.Empty<BalanceMetric>();
        }

        List<BalanceMetric> balances = new();
        AddBalance(balances, planStatus, "availablePromptCredits", "prompt_credits", "Prompt");
        AddBalance(balances, planStatus, "availableFlowCredits", "flow_credits", "Flow");
        return balances;
    }

    private static void AddBalance(
        List<BalanceMetric> balances,
        JsonElement planStatus,
        string property,
        string key,
        string displayName
    )
    {
        double? value = ReadDouble(planStatus, property);
        if (value is null || !double.IsFinite(value.Value) || value.Value < 0)
        {
            return;
        }
        // No FormattedValue: the shell renders one verbatim, which would drop the
        // name and the localized "credits" suffix every other provider shows.
        balances.Add(new BalanceMetric(key, displayName, (decimal)value.Value, MeterUnit.Credits));
    }

    private async Task<JsonElement> PostAsync(
        string scheme,
        int port,
        string path,
        object body,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        using HttpRequestMessage request = new(HttpMethod.Post, $"{scheme}://127.0.0.1:{port}{path}");
        request.Content = JsonContent.Create(body);
        request.Headers.TryAddWithoutValidation("Connect-Protocol-Version", "1");
        using HttpResponseMessage response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"agy returned HTTP {(int)response.StatusCode}.", null, response.StatusCode);
        }

        await response.Content.LoadIntoBufferAsync(MaximumResponseBytes, timeout.Token).ConfigureAwait(false);
        await using Stream stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument
            .ParseAsync(stream, default, timeout.Token)
            .ConfigureAwait(false);
        return document.RootElement.Clone();
    }

    private static HttpClient CreateLoopbackClient()
    {
        HttpClientHandler handler = new()
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            // The CLI serves a self-signed certificate. Trusting it is only ever
            // safe because the request never leaves the loopback interface.
            ServerCertificateCustomValidationCallback = (request, _, _, _) =>
                request.RequestUri is { Scheme: "https", Host: "127.0.0.1" },
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
        if (code.ValueKind != JsonValueKind.String)
            return false;
        string? valueText = code.GetString();
        return valueText == "0"
            || string.Equals(valueText, "ok", StringComparison.OrdinalIgnoreCase)
            || string.Equals(valueText, "success", StringComparison.OrdinalIgnoreCase);
    }

    private static (string? Email, string? Plan) ReadIdentity(JsonElement? root)
    {
        if (
            !root.HasValue
            || root.Value.ValueKind != JsonValueKind.Object
            || !root.Value.TryGetProperty("userStatus", out JsonElement status)
            || status.ValueKind != JsonValueKind.Object
        )
        {
            return (null, null);
        }
        string? email = ReadString(status, "email")?.Trim();
        string? plan =
            ReadNestedString(status, "userTier", "name")
            ?? ReadNestedString(status, "planStatus", "planInfo", "planDisplayName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "displayName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "productName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "planName")
            ?? ReadNestedString(status, "planStatus", "planInfo", "planShortName");
        return (email, plan?.Trim());
    }

    private static string FriendlyGroup(string value)
    {
        if (value.Contains("gemini", StringComparison.OrdinalIgnoreCase))
            return "Gemini";
        if (
            value.Contains("claude", StringComparison.OrdinalIgnoreCase)
            || value.Contains("gpt", StringComparison.OrdinalIgnoreCase)
        )
            return "Claude/GPT";
        return value.Trim();
    }

    private static string FriendlyWindow(string value, TimeSpan? duration)
    {
        if (duration == TimeSpan.FromHours(5))
            return "5-hour";
        if (duration == TimeSpan.FromDays(7))
            return "weekly";
        return value.Trim();
    }

    private static TimeSpan? ResolveWindowDuration(string window, string bucketId, string displayName)
    {
        string candidate = $"{window} {bucketId} {displayName}".Replace('_', '-').ToLowerInvariant();
        if (candidate.Contains("weekly", StringComparison.Ordinal))
            return TimeSpan.FromDays(7);
        string[] sessionAliases = ["session", "5h", "5-hour", "five hour", "five-hour"];
        return sessionAliases.Any(alias => candidate.Contains(alias, StringComparison.Ordinal))
            ? TimeSpan.FromHours(5)
            : null;
    }

    private static MeterStatus StatusFor(double usedPercent) =>
        usedPercent switch
        {
            >= 100 => MeterStatus.Exhausted,
            >= 95 => MeterStatus.Critical,
            >= 80 => MeterStatus.Approaching,
            _ => MeterStatus.Healthy,
        };

    private static double? ReadOneOfRemainingFraction(JsonElement bucket)
    {
        if (
            !bucket.TryGetProperty("remaining", out JsonElement remaining)
            || remaining.ValueKind != JsonValueKind.Object
            || ReadString(remaining, "case") != "remainingFraction"
        )
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
        element.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double number) ? number : null;

    private static double? ReadNestedDouble(JsonElement element, string parent, string name) =>
        element.TryGetProperty(parent, out JsonElement nested) && nested.ValueKind == JsonValueKind.Object
            ? ReadDouble(nested, name)
            : null;

    private static string? ReadNestedString(JsonElement element, params string[] path)
    {
        JsonElement current = element;
        foreach (string part in path)
        {
            if (
                !current.TryGetProperty(part, out current)
                || current.ValueKind != JsonValueKind.Object && part != path[^1]
            )
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
        if (
            DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out DateTimeOffset date
            )
        )
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
