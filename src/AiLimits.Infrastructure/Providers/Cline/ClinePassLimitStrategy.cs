// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>
/// ClinePass subscription windows (5-hour / weekly / monthly) from the account
/// API; the same endpoint CodexBar reads. The API returns used-percentages
/// only (no token counts, no cost); exact tokens come from the local-history
/// source. Unknown limit types are tolerated so new windows never break the
/// fetch. The CLI's idToken only lives about an hour, so an expired CLI
/// credential is refreshed here (POST /auth/refresh, same call the CLI makes)
/// and the refreshed session lands in QuotaBoard's own secret store (Credential
/// Manager on Windows) — the CLI's secrets.json is never written. Neither the
/// bearer nor the refresh token ever appears in logs or failure messages.
/// </summary>
internal sealed class ClinePassLimitStrategy(
    HttpClient http,
    IClock clock,
    Func<ClineCredential?>? credentialProbe = null,
    ClineSessionStore? sessionStore = null,
    IClineRefreshLock? refreshLock = null
) : ILimitFetchStrategy
{
    private static readonly Uri Uri = new("https://api.cline.bot/api/v1/users/me/plan/usage-limits");

    private static readonly Uri RefreshUri = new("https://api.cline.bot/api/v1/auth/refresh");

    /// <summary>Refresh this close to expiry so a token never dies mid-request.</summary>
    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(2);

    private const string NotConfiguredMessage = "No Cline credential. Set CLINE_API_KEY or sign in with the Cline CLI.";

    private readonly IClineRefreshLock _refreshLock = refreshLock ?? ClineNamedRefreshLock.Instance;

    public string Id => "cline.pass-usage-limits-api";

    public int Order => 10;

    public Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken
    ) =>
        Task.FromResult(
            ResolveCredential() is not null
                ? StrategyAvailabilityResult.Ready()
                : new StrategyAvailabilityResult(StrategyAvailability.NotConfigured, NotConfiguredMessage)
        );

    public Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken) =>
        sessionStore is null
            ? FetchCoreAsync(account, cancellationToken)
            : FetchSerializedAsync(account, cancellationToken);

    /// <summary>
    /// The store stages a save under fixed keys and tolerates exactly one
    /// writer, so a fetch that does not own the lock must not run: two
    /// unserialized writers can promote a mixed access/expiry/refresh triple
    /// into the live keys, or leave the live keys holding a refresh token the
    /// other writer already rotated away. Losing one refresh cycle is
    /// recoverable; a corrupted session costs the user a CLI re-login.
    /// </summary>
    private async Task<FetchResult> FetchSerializedAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        try
        {
            return await _refreshLock
                .RunAsync(() => FetchCoreAsync(account, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (ClineRefreshLockException ex)
        {
            // TemporarilyUnavailable renders as retrying rather than as a
            // sign-in prompt, and TryNextStrategy still lets the local-history
            // source fill the card this cycle.
            return FetchResult.Failure(
                FetchFailureKind.TemporarilyUnavailable,
                ex.Message,
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }
    }

    private async Task<FetchResult> FetchCoreAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();

        ClineCredential? credential = ResolveCredential();
        // Resolve identity first: fixed cache keys are safe only when the live
        // credential proves they belong to the same account. A null identity
        // also causes legacy/unsafe cache cleanup.
        ClineSession? cached = sessionStore is null
            ? null
            : await sessionStore.LoadAsync(credential?.AccountFingerprint, cancellationToken).ConfigureAwait(false);

        if (credential is null)
        {
            return FetchResult.Failure(
                FetchFailureKind.Authentication,
                NotConfiguredMessage,
                FallbackPolicy.TryNextStrategy,
                Id,
                Stopwatch.GetElapsedTime(started)
            );
        }

        string token = credential.Token;
        string? refreshToken = credential.RefreshToken;
        DateTimeOffset? expiresAt = credential.ExpiresAt;
        bool workOsSession = credential.IsWorkOsSession;

        // Prefer a session we refreshed ourselves when it is fresher than what
        // the CLI last stored; a CLI re-login or CLI-side refresh writes a
        // later expiry and wins back automatically. Env-var credentials carry
        // no refresh token and skip all of this.
        if (refreshToken is not null && cached is not null && cached.ExpiresAt > (expiresAt ?? DateTimeOffset.MinValue))
        {
            token = cached.AccessToken;
            refreshToken = cached.RefreshToken ?? refreshToken;
            expiresAt = cached.ExpiresAt;
        }

        bool refreshedThisFetch = false;
        if (refreshToken is not null && expiresAt is { } expiry && expiry <= clock.UtcNow + ExpirySkew)
        {
            (ClineSession? session, FetchResult? failure) = await RefreshAsync(
                    refreshToken,
                    credential.AccountFingerprint,
                    started,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (session is null)
            {
                return failure!;
            }
            token = session.AccessToken;
            refreshToken = session.RefreshToken ?? refreshToken;
            workOsSession = true;
            refreshedThisFetch = true;
        }

        while (true)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, Uri);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Parameter(token, workOsSession));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using ProviderJsonResult exchange = await ProviderHttp
                .GetJsonAsync(
                    http,
                    request,
                    Id,
                    "Cline",
                    started,
                    cancellationToken,
                    new ProviderHttpOptions(Timeout: TimeSpan.FromSeconds(15))
                )
                .ConfigureAwait(false);
            if (!exchange.IsSuccess)
            {
                // A 401 on a token we believed valid usually means the server
                // revoked it early; one refresh+retry covers that without ever
                // looping.
                if (
                    exchange.Failure!.FailureKind == FetchFailureKind.Authentication
                    && refreshToken is not null
                    && !refreshedThisFetch
                )
                {
                    (ClineSession? session, FetchResult? failure) = await RefreshAsync(
                            refreshToken,
                            credential.AccountFingerprint,
                            started,
                            cancellationToken
                        )
                        .ConfigureAwait(false);
                    if (session is null)
                    {
                        return failure!;
                    }
                    token = session.AccessToken;
                    workOsSession = true;
                    refreshedThisFetch = true;
                    continue;
                }
                return exchange.Failure!;
            }
            try
            {
                return Parse(account, exchange.Document!, started);
            }
            catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
            {
                return Malformed(started);
            }
        }
    }

    // Same exchange the Cline CLI performs: POST {refreshToken, grantType} →
    // {success, data:{accessToken, refreshToken?, expiresAt}}. A missing
    // expiresAt is assumed ~1h out (the observed idToken lifetime) minus
    // headroom; a response without a rotated refresh token keeps the old one.
    private async Task<(ClineSession? Session, FetchResult? Failure)> RefreshAsync(
        string refreshToken,
        string? accountFingerprint,
        long started,
        CancellationToken cancellationToken
    )
    {
        using HttpRequestMessage request = new(HttpMethod.Post, RefreshUri);
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { refreshToken, grantType = "refresh_token" }),
            Encoding.UTF8,
            "application/json"
        );
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using ProviderJsonResult exchange = await ProviderHttp
            .GetJsonAsync(
                http,
                request,
                Id,
                "Cline",
                started,
                cancellationToken,
                new ProviderHttpOptions(Timeout: TimeSpan.FromSeconds(15))
            )
            .ConfigureAwait(false);
        if (!exchange.IsSuccess)
        {
            return (null, exchange.Failure!);
        }
        JsonElement root = exchange.Document!.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out JsonElement success)
            || success.ValueKind != JsonValueKind.True
            || !root.TryGetProperty("data", out JsonElement data)
            || data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("accessToken", out JsonElement access)
            || access.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(access.GetString())
        )
        {
            return (
                null,
                FetchResult.Failure(
                    FetchFailureKind.MalformedResponse,
                    "Cline returned an unrecognized token refresh response.",
                    FallbackPolicy.TryNextStrategy,
                    Id,
                    Stopwatch.GetElapsedTime(started)
                )
            );
        }
        string? rotated =
            data.TryGetProperty("refreshToken", out JsonElement rotatedElement)
            && rotatedElement.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(rotatedElement.GetString())
                ? rotatedElement.GetString()!.Trim()
                : null;
        DateTimeOffset expiresAt = ClineCredentialReader.ParseExpiry(data) ?? clock.UtcNow + TimeSpan.FromMinutes(55);
        ClineSession session = new(access.GetString()!.Trim(), rotated ?? refreshToken, expiresAt, accountFingerprint);
        if (sessionStore is not null && accountFingerprint is not null)
        {
            await sessionStore.SaveAsync(session, cancellationToken).ConfigureAwait(false);
        }
        return (session, null);
    }

    private FetchResult Parse(ProviderAccount account, JsonDocument document, long started)
    {
        JsonElement root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("success", out JsonElement success)
            || success.ValueKind != JsonValueKind.True
        )
        {
            return Malformed(started);
        }

        List<UsageMeter> meters = [];
        // A window we could not read is not the same as a window that is gone.
        // Counting them lets the snapshot declare itself Partial, which keeps
        // the previously known meter on the card with a Stale badge instead of
        // silently dropping it.
        int unreadable = 0;
        DateTimeOffset now = clock.UtcNow;
        if (
            root.TryGetProperty("data", out JsonElement data)
            && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("limits", out JsonElement limits)
            && limits.ValueKind == JsonValueKind.Array
        )
        {
            foreach (JsonElement element in limits.EnumerateArray())
            {
                if (element.ValueKind != JsonValueKind.Object)
                {
                    unreadable++;
                    continue;
                }
                if (ShapeFor(ReadString(element, "type")) is not { } meter)
                {
                    unreadable++;
                    continue;
                }
                if (
                    !element.TryGetProperty("percentUsed", out JsonElement percentNode)
                    || !percentNode.TryGetDouble(out double rawPercent)
                )
                {
                    unreadable++;
                    continue;
                }
                double percent = Math.Clamp(rawPercent, 0, 100);
                meters.Add(
                    new UsageMeter(
                        new MeterKey(meter.Key),
                        meter.Name,
                        MeterScope.Account,
                        MeterUnit.Percent,
                        (decimal)percent,
                        100m,
                        percent,
                        meter.Window,
                        ParseResetsAt(element),
                        null,
                        percent >= 100 ? MeterStatus.Exhausted
                            : percent >= 95 ? MeterStatus.Critical
                            : percent >= 80 ? MeterStatus.Approaching
                            : MeterStatus.Healthy,
                        new MeterProvenance(Id, "clinepass usage-limits api", now, true)
                    )
                );
            }
        }

        return meters.Count > 0
            ? FetchResult.Success(
                new ProviderSnapshot(
                    account.Key,
                    meters,
                    [],
                    unreadable > 0 ? SnapshotCompleteness.Partial : SnapshotCompleteness.Authoritative,
                    now,
                    DataConfidence.High,
                    ProviderHttpSupport.SafeExtensions(("source", "api"))
                ),
                Id,
                Stopwatch.GetElapsedTime(started)
            )
            : Malformed(started);
    }

    private static (string Key, string Name, TimeSpan Window)? ShapeFor(string? type) =>
        type switch
        {
            "five_hour" => ("cline:5h", "ClinePass 5-hour", TimeSpan.FromHours(5)),
            "weekly" => ("cline:weekly", "ClinePass Weekly", TimeSpan.FromDays(7)),
            "monthly" => ("cline:monthly", "ClinePass Monthly", TimeSpan.FromDays(30)),
            // New windows appear before we know them; never fail the fetch.
            _ => null,
        };

    private static DateTimeOffset? ParseResetsAt(JsonElement element)
    {
        if (!element.TryGetProperty("resetsAt", out JsonElement raw) || raw.ValueKind != JsonValueKind.String)
        {
            return null;
        }
        return DateTimeOffset.TryParse(
            raw.GetString(),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out DateTimeOffset parsed
        )
            ? parsed
            : null;
    }

    // api.cline.bot namespaces its bearer by identity provider: a WorkOS
    // session token is only accepted as "workos:<token>" (the same value the
    // Cline extension and CLI send); a bare session token 401s on every
    // endpoint. Dashboard API keys carry no prefix.
    private const string WorkOsPrefix = "workos:";

    private static string Parameter(string token, bool workOsSession) =>
        workOsSession && !token.StartsWith(WorkOsPrefix, StringComparison.Ordinal) ? WorkOsPrefix + token : token;

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private FetchResult Malformed(long started) =>
        FetchResult.Failure(
            FetchFailureKind.MalformedResponse,
            "Cline returned an unrecognized usage-limits response.",
            FallbackPolicy.TryNextStrategy,
            Id,
            Stopwatch.GetElapsedTime(started)
        );

    private ClineCredential? ResolveCredential() =>
        credentialProbe is not null ? credentialProbe() : ClineCredentialReader.Resolve();
}

/// <summary>
/// The cross-process refresh lock could not be taken. <see cref="ClineSessionStore"/>
/// stages a save under fixed keys and tolerates exactly one writer, so the
/// caller has to fail the fetch instead of proceeding without ownership.
/// </summary>
internal sealed class ClineRefreshLockException(string message, Exception? innerException = null)
    : Exception(message, innerException);

/// <summary>
/// Serializes the store load, refresh-token exchange, and save across app
/// processes. A named <see cref="Mutex"/> is thread-affine, so the complete
/// asynchronous operation runs synchronously on the worker thread that owns
/// it. Failing to take the lock throws <see cref="ClineRefreshLockException"/>;
/// it never runs the operation unowned.
/// </summary>
internal interface IClineRefreshLock
{
    Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken);
}

internal sealed class ClineNamedRefreshLock : IClineRefreshLock
{
    // Worst case inside the lock is a usage call, a 401-triggered refresh and
    // a usage retry — three 15-second requests — plus the secret-store round
    // trips around them. The wait has to outlast that: contention now fails
    // the fetch instead of bypassing the lock, so timing out while the owner
    // is merely slow would throw away a refresh for no reason.
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

    private const string GlobalPrefix = @"Global\";
    private const string LocalPrefix = @"Local\";

    internal const string ContendedMessage = "Another QuotaBoard refresh is using the Cline session; retrying shortly.";

    internal const string UnavailableMessage =
        "The Cline session lock is unavailable on this machine; retrying shortly.";

    public static ClineNamedRefreshLock Instance { get; } = new(CreateName(), DefaultTimeout);

    private readonly string _name;
    private readonly TimeSpan _timeout;

    internal ClineNamedRefreshLock(string name, TimeSpan timeout)
    {
        _name = name;
        _timeout = timeout;
    }

    public Task<T> RunAsync<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (!OperatingSystem.IsWindows())
        {
            return operation();
        }

        return Task.Run(() => Run(operation, cancellationToken), CancellationToken.None);
    }

    private T Run<T>(Func<Task<T>> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Mutex mutex = OpenMutex();
        bool acquired = false;
        try
        {
            try
            {
                int signaled = WaitHandle.WaitAny([mutex, cancellationToken.WaitHandle], _timeout);
                if (signaled == 1)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                acquired = signaled == 0;
            }
            catch (AbandonedMutexException)
            {
                // The previous owner died holding the lock, so we own it now.
                // Whatever staging it left behind is reconciled by LoadAsync's
                // commit-marker recovery.
                acquired = true;
            }

            if (!acquired)
            {
                throw new ClineRefreshLockException(ContendedMessage);
            }
            return operation().GetAwaiter().GetResult();
        }
        finally
        {
            if (acquired)
            {
                mutex.ReleaseMutex();
            }
            mutex.Dispose();
        }
    }

    /// <summary>
    /// A <c>Global\</c> name also serializes across terminal-server sessions,
    /// but creating one needs SeCreateGlobalPrivilege, which a locked-down
    /// machine can withhold. Session-local serialization still covers every
    /// instance such a user can launch, so fall back to it rather than lose
    /// the provider outright.
    /// </summary>
    private Mutex OpenMutex()
    {
        try
        {
            return new Mutex(initiallyOwned: false, _name);
        }
        catch (Exception ex) when (IsLockSetupFailure(ex))
        {
            if (!_name.StartsWith(GlobalPrefix, StringComparison.Ordinal))
            {
                throw new ClineRefreshLockException(UnavailableMessage, ex);
            }
            try
            {
                return new Mutex(initiallyOwned: false, string.Concat(LocalPrefix, _name.AsSpan(GlobalPrefix.Length)));
            }
            catch (Exception fallbackFailure) when (IsLockSetupFailure(fallbackFailure))
            {
                throw new ClineRefreshLockException(UnavailableMessage, fallbackFailure);
            }
        }
    }

    private static bool IsLockSetupFailure(Exception ex) =>
        ex is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException;

    private static string CreateName()
    {
        string identity = Environment.UserName;
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using WindowsIdentity current = WindowsIdentity.GetCurrent();
                identity = current.User?.Value ?? identity;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or SystemException) { }
        }
        return $"Global\\QuotaBoard.ClineRefresh.{identity}";
    }
}
