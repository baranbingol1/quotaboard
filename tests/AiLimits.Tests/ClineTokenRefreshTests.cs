// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Cline;

namespace AiLimits.Tests;

/// <summary>
/// The Cline CLI's idToken only lives about an hour, so the strategy must
/// refresh it itself (the "Cline card is SIGN IN unless Cline ran within the
/// hour" bug). These tests drive the refresh + cache + retry paths against a
/// scripted handler.
/// </summary>
[Collection("ClineEnv")]
public sealed class ClineTokenRefreshTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);

    private const string UsageUri = "https://api.cline.bot/api/v1/users/me/plan/usage-limits";
    private const string RefreshUri = "https://api.cline.bot/api/v1/auth/refresh";

    private readonly string _cacheDirectory = Path.Combine(
        Path.GetTempPath(), "quotaboard-tests", Guid.NewGuid().ToString("N"));

    private string LegacyCachePath => Path.Combine(_cacheDirectory, "cline-session.json");

    private readonly InMemorySecretStore _secrets = new();

    private ClineSessionStore Store() => new(_secrets);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_cacheDirectory))
            {
                Directory.Delete(_cacheDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task Expired_cli_token_is_refreshed_before_the_usage_call()
    {
        var handler = new RoutedHandler(request => request.RequestUri!.ToString() switch
        {
            RefreshUri => Json("""
                {"success":true,"data":{"accessToken":"fresh-token","refreshToken":"rotated-refresh","expiresAt":"2026-07-24T13:05:00Z"}}
                """),
            _ => Json("""
                {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":42,"resetsAt":null}]}}
                """),
        });
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(),
            () => new ClineCredential("stale-token", "Cline CLI account",
                ExpiresAt: Now - TimeSpan.FromHours(23), RefreshToken: "stored-refresh",
                IsWorkOsSession: true),
            Store());

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        Assert.Equal(2, handler.Requests.Count);

        (string method, string uri, string? authorization, string? body) refresh = handler.Requests[0];
        Assert.Equal("POST", refresh.method);
        Assert.Equal(RefreshUri, refresh.uri);
        using (JsonDocument requestBody = JsonDocument.Parse(refresh.body!))
        {
            Assert.Equal("stored-refresh", requestBody.RootElement.GetProperty("refreshToken").GetString());
            Assert.Equal("refresh_token", requestBody.RootElement.GetProperty("grantType").GetString());
        }

        (string method, string uri, string? authorization, string? body) usage = handler.Requests[1];
        Assert.Equal("GET", usage.method);
        Assert.Equal(UsageUri, usage.uri);
        Assert.Equal("Bearer workos:fresh-token", usage.authorization);
        Assert.Null(refresh.authorization);

        ClineSession? cached = await Store().LoadAsync(default);
        Assert.NotNull(cached);
        Assert.Equal("fresh-token", cached!.AccessToken);
        Assert.Equal("rotated-refresh", cached.RefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 13, 5, 0, TimeSpan.Zero), cached.ExpiresAt);
    }

    [Fact]
    public async Task Fresher_cached_session_is_used_without_refreshing()
    {
        await Store().SaveAsync(
            new ClineSession("cached-token", "cached-refresh", Now + TimeSpan.FromMinutes(40)), default);
        var handler = new RoutedHandler(_ => Json("""
            {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":7,"resetsAt":null}]}}
            """));
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(),
            () => new ClineCredential("stale-token", "Cline CLI account",
                ExpiresAt: Now - TimeSpan.FromHours(23), RefreshToken: "stored-refresh",
                IsWorkOsSession: true),
            Store());

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        (string method, string uri, string? authorization, string? body) only = Assert.Single(handler.Requests);
        Assert.Equal(UsageUri, only.uri);
        Assert.Equal("Bearer workos:cached-token", only.authorization);
    }

    [Fact]
    public async Task Concurrent_fetches_share_the_first_refreshed_session()
    {
        var handler = new ConcurrentRefreshHandler();
        ClineSessionStore store = Store();
        string lockName = $"Local\\QuotaBoard.Tests.ClineRefresh.{Guid.NewGuid():N}";
        ClineCredential Credential() => new(
            "stale-token",
            "Cline CLI account",
            ExpiresAt: Now - TimeSpan.FromHours(1),
            RefreshToken: "stored-refresh",
            IsWorkOsSession: true);
        var first = new ClinePassLimitStrategy(
            new HttpClient(handler), new FixedClock(), Credential, store,
            new ClineNamedRefreshLock(lockName, TimeSpan.FromSeconds(5)));
        var second = new ClinePassLimitStrategy(
            new HttpClient(handler), new FixedClock(), Credential, store,
            new ClineNamedRefreshLock(lockName, TimeSpan.FromSeconds(5)));

        Task<FetchResult> firstFetch = first.FetchAsync(Account(), default);
        await handler.FirstRefreshEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<FetchResult> secondFetch = second.FetchAsync(Account(), default);
        try
        {
            await Task.Delay(100);
            Assert.Equal(1, handler.RefreshCount);
        }
        finally
        {
            handler.ReleaseFirstRefresh.TrySetResult();
        }
        FetchResult[] results = await Task.WhenAll(firstFetch, secondFetch);

        Assert.All(results, result => Assert.True(result.IsSuccess, result.SafeMessage));
        Assert.Equal(1, handler.RefreshCount);
        Assert.Equal(2, handler.UsageCount);
    }

    [Fact]
    public async Task Named_lock_timeout_runs_the_fetch_unlocked()
    {
        string name = $"Local\\QuotaBoard.Tests.ClineRefresh.{Guid.NewGuid():N}";
        using var acquired = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task holder = Task.Run(() =>
        {
            using var mutex = new Mutex(initiallyOwned: false, name);
            mutex.WaitOne();
            acquired.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        acquired.Wait();

        try
        {
            var refreshLock = new ClineNamedRefreshLock(name, TimeSpan.Zero);

            int result = await refreshLock.RunAsync(() => Task.FromResult(42), default);

            Assert.Equal(42, result);
        }
        finally
        {
            release.Set();
            await holder;
        }
    }

    [Fact]
    public async Task Unauthorized_usage_call_triggers_one_refresh_and_retry()
    {
        var handler = new RoutedHandler(request =>
        {
            if (request.RequestUri!.ToString() == RefreshUri)
            {
                return Json("""
                    {"success":true,"data":{"accessToken":"fresh-token","expiresAt":"2026-07-24T13:05:00Z"}}
                    """);
            }
            return request.Headers.Authorization?.Parameter == "workos:fresh-token"
                ? Json("""
                    {"success":true,"data":{"limits":[{"type":"five_hour","percentUsed":3,"resetsAt":null}]}}
                    """)
                : new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                };
        });
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(),
            () => new ClineCredential("revoked-token", "Cline CLI account",
                ExpiresAt: Now + TimeSpan.FromMinutes(30), RefreshToken: "stored-refresh",
                IsWorkOsSession: true),
            Store());

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal(UsageUri, handler.Requests[0].Uri);
        Assert.Equal(RefreshUri, handler.Requests[1].Uri);
        Assert.Equal("Bearer workos:fresh-token", handler.Requests[2].Authorization);
    }

    [Fact]
    public async Task Rejected_refresh_surfaces_as_authentication_failure_without_echoing_tokens()
    {
        var handler = new RoutedHandler(request =>
            request.RequestUri!.ToString() == RefreshUri
                ? new HttpResponseMessage(HttpStatusCode.Unauthorized)
                {
                    Content = new StringContent("{\"error\":\"revoked stored-refresh\"}", Encoding.UTF8, "application/json")
                }
                : Json("{}"));
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(),
            () => new ClineCredential("stale-token", "Cline CLI account",
                ExpiresAt: Now - TimeSpan.FromHours(23), RefreshToken: "stored-refresh",
                IsWorkOsSession: true),
            Store());

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.Authentication, result.FailureKind);
        Assert.DoesNotContain("stored-refresh", result.SafeMessage);
        Assert.DoesNotContain("stale-token", result.SafeMessage);
        Assert.Null(await Store().LoadAsync(default));
    }

    [Fact]
    public async Task Env_style_credential_without_expiry_is_used_as_is()
    {
        var handler = new RoutedHandler(_ => Json("""
            {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":1,"resetsAt":null}]}}
            """));
        var strategy = new ClinePassLimitStrategy(new HttpClient(handler), new FixedClock(),
            () => new ClineCredential("env-token", "API key (CLINE_API_KEY)"),
            Store());

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.True(result.IsSuccess, result.SafeMessage);
        (string method, string uri, string? authorization, string? body) only = Assert.Single(handler.Requests);
        Assert.Equal("Bearer env-token", only.authorization);
    }

    [Fact]
    public void Cli_blob_exposes_refresh_token_and_expiry()
    {
        string path = Path.Combine(_cacheDirectory, "secrets.json");
        Directory.CreateDirectory(_cacheDirectory);
        File.WriteAllText(path, """
            {"cline:clineAccountId":"{\"idToken\":\"blob-token\",\"refreshToken\":\"blob-refresh\",\"expiresAt\":1784772484,\"provider\":\"cline\",\"userInfo\":{\"email\":\"x@y.z\"}}"}
            """);

        ClineCredential? credential = ClineCredentialReader.ResolveSecrets(path);

        Assert.NotNull(credential);
        Assert.Equal("blob-token", credential!.Token);
        Assert.Equal("blob-refresh", credential.RefreshToken);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1784772484), credential.ExpiresAt);
        Assert.True(credential.IsWorkOsSession);
    }

    [Fact]
    public async Task Legacy_plaintext_cache_is_migrated_into_the_secret_store_and_deleted()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(LegacyCachePath, """
            {"accessToken":"file-token","refreshToken":"file-refresh","expiresAt":"2026-07-24T13:05:00+00:00"}
            """);

        ClineSession? migrated = await new ClineSessionStore(_secrets, LegacyCachePath).LoadAsync(default);

        Assert.NotNull(migrated);
        Assert.Equal("file-token", migrated!.AccessToken);
        Assert.Equal("file-refresh", migrated.RefreshToken);
        Assert.Equal(new DateTimeOffset(2026, 7, 24, 13, 5, 0, TimeSpan.Zero), migrated.ExpiresAt);

        Assert.False(File.Exists(LegacyCachePath));
        // The tokens must now only exist in the vault.
        Assert.Equal("file-token", await _secrets.GetAsync("cline", "session.access-token", default));
        Assert.Equal("file-refresh", await _secrets.GetAsync("cline", "session.refresh-token", default));
    }

    [Fact]
    public async Task A_signed_out_user_still_gets_the_plaintext_cache_cleaned_up()
    {
        // The migration cannot hang off the token-refresh path: a user who has
        // since signed out of Cline never reaches it, and their old plaintext
        // tokens would sit on disk forever.
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(LegacyCachePath, """
            {"accessToken":"orphaned-token","refreshToken":"orphaned-refresh","expiresAt":"2026-07-24T13:05:00+00:00"}
            """);
        var strategy = new ClinePassLimitStrategy(
            new HttpClient(new RoutedHandler(_ => Json("{}"))), new FixedClock(),
            () => null,
            new ClineSessionStore(_secrets, LegacyCachePath));

        FetchResult result = await strategy.FetchAsync(Account(), default);

        Assert.False(result.IsSuccess);
        Assert.Equal(FetchFailureKind.Authentication, result.FailureKind);
        Assert.False(File.Exists(LegacyCachePath));
        Assert.Equal("orphaned-token", await _secrets.GetAsync("cline", "session.access-token", default));
    }

    [Fact]
    public async Task A_failed_migration_keeps_the_plaintext_cache_and_retries_on_next_load()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(LegacyCachePath, """
            {"accessToken":"file-token","refreshToken":"file-refresh","expiresAt":"2026-07-24T13:05:00+00:00"}
            """);
        _secrets.Fault = new System.ComponentModel.Win32Exception(5);
        var store = new ClineSessionStore(_secrets, LegacyCachePath);

        // The vault is down: the session must not be deleted from disk.
        Assert.Null(await store.LoadAsync(default));
        Assert.True(File.Exists(LegacyCachePath));

        // The vault recovering lets the very next load finish the migration,
        // because the failed attempt did not latch.
        _secrets.Fault = null;
        ClineSession? migrated = await store.LoadAsync(default);

        Assert.NotNull(migrated);
        Assert.Equal("file-token", migrated!.AccessToken);
        Assert.Equal("file-refresh", migrated.RefreshToken);
        Assert.False(File.Exists(LegacyCachePath));
    }

    [Fact]
    public async Task A_partially_written_migration_keeps_the_plaintext_cache()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(LegacyCachePath, """
            {"accessToken":"file-token","refreshToken":"file-refresh","expiresAt":"2026-07-24T13:05:00+00:00"}
            """);
        // The staging expires-at write fails after staging access succeeds:
        // the vault now holds a half-written staging set, but the live keys are
        // untouched and LoadAsync rejects them, so deleting the plaintext file
        // here would strand the user.
        _secrets.FaultFor = key => key is { Scope: "cline", Key: "session.expires-at.staging" }
            ? new System.ComponentModel.Win32Exception(5)
            : null;
        var store = new ClineSessionStore(_secrets, LegacyCachePath);

        Assert.Null(await store.LoadAsync(default));
        Assert.True(File.Exists(LegacyCachePath));

        _secrets.FaultFor = null;
        ClineSession? migrated = await store.LoadAsync(default);

        Assert.NotNull(migrated);
        Assert.Equal("file-token", migrated!.AccessToken);
        Assert.False(File.Exists(LegacyCachePath));
    }

    [Fact]
    public async Task A_refresh_write_failure_after_access_and_expiry_succeed_keeps_plaintext_and_hides_partial_state()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(LegacyCachePath, """
            {"accessToken":"file-token","refreshToken":"file-refresh","expiresAt":"2026-07-24T13:05:00+00:00"}
            """);
        // The staging refresh-token write fails after staging access and
        // expiry succeed. The live keys must stay untouched so LoadAsync
        // cannot return a session with the new access but a stale or missing
        // refresh.
        _secrets.FaultFor = key => key is { Scope: "cline", Key: "session.refresh-token.staging" }
            ? new System.ComponentModel.Win32Exception(5)
            : null;
        var store = new ClineSessionStore(_secrets, LegacyCachePath);

        // No partial session leaks, and the plaintext cache is kept for retry.
        Assert.Null(await store.LoadAsync(default));
        Assert.True(File.Exists(LegacyCachePath));

        // The vault recovering lets the next load finish the migration.
        _secrets.FaultFor = null;
        ClineSession? migrated = await store.LoadAsync(default);

        Assert.NotNull(migrated);
        Assert.Equal("file-token", migrated!.AccessToken);
        Assert.Equal("file-refresh", migrated.RefreshToken);
        Assert.False(File.Exists(LegacyCachePath));
    }

    [Fact]
    public async Task A_save_that_finds_no_staging_values_reports_failure_and_keeps_legacy_cache()
    {
        Directory.CreateDirectory(_cacheDirectory);
        await File.WriteAllTextAsync(LegacyCachePath, """
            {"accessToken":"file-token","refreshToken":"file-refresh","expiresAt":"2026-07-24T13:05:00+00:00"}
            """);
        bool removed = false;
        _secrets.BeforeGet = key =>
        {
            if (removed || key is not { Scope: "cline", Key: "session.access-token.staging" })
            {
                return;
            }
            removed = true;
            foreach (string stagingKey in new[]
            {
                "session.access-token.staging",
                "session.expires-at.staging",
                "session.refresh-token.staging",
                "session.clear-refresh.staging"
            })
            {
                _secrets.DeleteAsync("cline", stagingKey, default).GetAwaiter().GetResult();
            }
        };
        var store = new ClineSessionStore(_secrets, LegacyCachePath);

        ClineSession? loaded = await store.LoadAsync(default);

        Assert.Null(loaded);
        Assert.True(File.Exists(LegacyCachePath));
        Assert.DoesNotContain(_secrets.Keys, key =>
            key is { Scope: "cline", Key: "session.commit" });
    }

    [Fact]
    public async Task A_mid_write_failure_in_non_migration_save_leaves_previous_session_intact()
    {
        ClineSessionStore store = Store();
        // Save a good session first.
        await store.SaveAsync(
            new ClineSession("good-access", "good-refresh", Now + TimeSpan.FromHours(1)), default);
        ClineSession? good = await store.LoadAsync(default);
        Assert.NotNull(good);
        Assert.Equal("good-access", good!.AccessToken);

        // Attempt to save a new session, but fault on the staging expiry write.
        _secrets.FaultFor = key => key is { Scope: "cline", Key: "session.expires-at.staging" }
            ? new System.ComponentModel.Win32Exception(5)
            : null;
        await store.SaveAsync(
            new ClineSession("bad-access", "bad-refresh", Now + TimeSpan.FromHours(2)), default);

        // The previous good session must still be intact.
        _secrets.FaultFor = null;
        ClineSession? loaded = await store.LoadAsync(default);
        Assert.NotNull(loaded);
        Assert.Equal("good-access", loaded!.AccessToken);
        Assert.Equal("good-refresh", loaded.RefreshToken);
    }

    [Fact]
    public async Task A_failed_interrupted_promotion_does_not_expose_a_mixed_session()
    {
        ClineSessionStore store = Store();
        await store.SaveAsync(
            new ClineSession("old-access", "old-refresh", Now + TimeSpan.FromHours(1)), default);

        // Fail the live refresh-token promotion after the new access token and
        // expiry have already been promoted. The commit marker and staging
        // values remain so LoadAsync can retry atomically later.
        _secrets.SetFaultFor = key => key is { Scope: "cline", Key: "session.refresh-token" }
            ? new System.ComponentModel.Win32Exception(5)
            : null;
        await store.SaveAsync(
            new ClineSession("new-access", "new-refresh", Now + TimeSpan.FromHours(2)), default);

        Assert.Null(await store.LoadAsync(default));

        _secrets.SetFaultFor = null;
        ClineSession? recovered = await store.LoadAsync(default);
        Assert.NotNull(recovered);
        Assert.Equal("new-access", recovered!.AccessToken);
        Assert.Equal("new-refresh", recovered.RefreshToken);
    }

    [Fact]
    public async Task Incomplete_recovery_keeps_the_commit_marker_and_never_exposes_mixed_live_keys()
    {
        ClineSessionStore store = Store();
        await store.SaveAsync(
            new ClineSession("old-access", "old-refresh", Now + TimeSpan.FromHours(1)), default);
        _secrets.SetFaultFor = key => key is { Scope: "cline", Key: "session.refresh-token" }
            ? new System.ComponentModel.Win32Exception(5)
            : null;
        await store.SaveAsync(
            new ClineSession("new-access", "new-refresh", Now + TimeSpan.FromHours(2)), default);
        await _secrets.DeleteAsync("cline", "session.refresh-token.staging", default);
        _secrets.SetFaultFor = null;

        Assert.Null(await store.LoadAsync(default));
        Assert.Null(await store.LoadAsync(default));
        Assert.Contains(_secrets.Keys, key => key is { Scope: "cline", Key: "session.commit" });
    }

    [Fact]
    public async Task An_unavailable_secret_store_degrades_to_no_cache_rather_than_failing()
    {
        _secrets.Fault = new System.ComponentModel.Win32Exception(5);

        ClineSessionStore store = Store();
        await store.SaveAsync(new ClineSession("t", "r", Now), default);

        Assert.Null(await store.LoadAsync(default));
    }

    [Fact]
    public async Task Dropping_a_refresh_token_clears_the_stored_one()
    {
        ClineSessionStore store = Store();
        await store.SaveAsync(new ClineSession("first", "rotating-refresh", Now), default);
        await store.SaveAsync(new ClineSession("second", null, Now), default);

        ClineSession? loaded = await store.LoadAsync(default);

        Assert.NotNull(loaded);
        Assert.Equal("second", loaded!.AccessToken);
        Assert.Null(loaded.RefreshToken);
    }

    private static ProviderAccount Account() => new(
        new AccountKey(new ProviderId("cline"), "default"), "Cline", null, "fixture", 1, true);

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class RoutedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(string Method, string Uri, string? Authorization, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add((
                request.Method.Method,
                request.RequestUri?.ToString() ?? "",
                request.Headers.Authorization?.ToString(),
                body));
            return respond(request);
        }
    }

    private sealed class ConcurrentRefreshHandler : HttpMessageHandler
    {
        private int _refreshCount;
        private int _usageCount;

        public int RefreshCount => Volatile.Read(ref _refreshCount);

        public int UsageCount => Volatile.Read(ref _usageCount);

        public TaskCompletionSource FirstRefreshEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstRefresh { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.ToString() == RefreshUri)
            {
                Interlocked.Increment(ref _refreshCount);
                FirstRefreshEntered.TrySetResult();
                await ReleaseFirstRefresh.Task.WaitAsync(cancellationToken);
                return Json("""
                    {"success":true,"data":{"accessToken":"fresh-token","refreshToken":"rotated-refresh","expiresAt":"2026-07-24T13:05:00Z"}}
                    """);
            }

            Interlocked.Increment(ref _usageCount);
            return Json("""
                {"success":true,"data":{"limits":[{"type":"weekly","percentUsed":42,"resetsAt":null}]}}
                """);
        }
    }
}
