// SPDX-License-Identifier: Apache-2.0
using System.Net;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers;
using AiLimits.Infrastructure.Providers.Cursor;
using Microsoft.Data.Sqlite;

namespace AiLimits.Tests;

public sealed class CursorProviderTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DiscoversAccountFromCursorSqliteJwt()
    {
        using var database = TestDatabase.Create("cursorAuth/accessToken", Jwt("auth0|user-123", Now.AddHours(1)));
        var adapter = new CursorProviderAdapter(new HttpClient(), new FixedClock(), database.Path);

        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

        Assert.Equal("cursor", account.Key.Provider.Value);
        Assert.Equal("auth0|user-123", account.Key.Value);
        Assert.Equal("Cursor app session", account.AuthSource);
        Assert.True(account.IsConnected);
    }

    [Fact]
    public async Task ReadsUtf16BlobCredential()
    {
        using var database = TestDatabase.Create(
            "cursorAuth/accessToken",
            Encoding.Unicode.GetBytes(Jwt("auth0|blob-user", Now.AddHours(1)))
        );
        var source = new CursorCredentialSource(database.Path, new FixedClock());

        CursorCredential? credential = await source.ReadAsync(default);

        Assert.NotNull(credential);
        Assert.Equal("blob-user", credential.CookieUserId);
    }

    [Fact]
    public void RejectsExpiredMalformedAndUnsafeJwtSubjects()
    {
        Assert.False(CursorCredentialSource.TryParse(Jwt("auth0|old-user", Now), Now, out _));
        Assert.False(CursorCredentialSource.TryParse("not-a-jwt", Now, out _));
        Assert.False(CursorCredentialSource.TryParse(Jwt("auth0|unsafe:user", Now.AddHours(1)), Now, out _));
    }

    [Fact]
    public void MapsModernUsageSummaryUsingCursorSchema()
    {
        using JsonDocument summary = JsonDocument.Parse(
            """
            {
              "billingCycleStart":"2026-07-01T00:00:00Z",
              "billingCycleEnd":"2026-08-01T00:00:00Z",
              "membershipType":"pro",
              "individualUsage": {
                "plan": {
                  "used":1500, "limit":5000,
                  "totalPercentUsed":30, "autoPercentUsed":12.5, "apiPercentUsed":3
                },
                "onDemand": {"used":500, "limit":10000}
              }
            }
            """
        );
        using JsonDocument identity = JsonDocument.Parse("""{"sub":"auth0|user-123","email":"user@example.com"}""");

        ProviderSnapshot snapshot = CursorUsageMapper.Map(
            Account(),
            summary.RootElement,
            null,
            identity.RootElement,
            Now,
            "cursor.test"
        );

        Assert.Collection(
            snapshot.Meters,
            meter =>
            {
                Assert.Equal("cursor:total", meter.Key.Value);
                Assert.Equal(MeterUnit.Usd, meter.Unit);
                Assert.Equal(15m, meter.Used);
                Assert.Equal(50m, meter.Limit);
                Assert.Equal(30, meter.UsedPercent);
                Assert.Equal(TimeSpan.FromDays(31), meter.WindowDuration);
            },
            meter => Assert.Equal(("cursor:auto", 12.5), (meter.Key.Value, meter.UsedPercent)),
            meter => Assert.Equal(("cursor:api", 3d), (meter.Key.Value, meter.UsedPercent)),
            meter =>
            {
                Assert.Equal("cursor:extra", meter.Key.Value);
                Assert.Equal(5m, meter.Used);
                Assert.Equal(100m, meter.Limit);
            }
        );
        Assert.Equal("user@example.com", snapshot.Extensions["email"].GetString());
        Assert.Equal("pro", snapshot.Extensions["plan_type"].GetString());
        Assert.Equal(DataConfidence.High, snapshot.Confidence);
        Assert.Equal(SnapshotCompleteness.Authoritative, snapshot.Completeness);
    }

    [Fact]
    public void UsesEnterpriseOverallThenTeamPoolForTotal()
    {
        using JsonDocument overall = JsonDocument.Parse(
            """
            {"membershipType":"enterprise","individualUsage":{"overall":{"used":7384,"limit":10000}}}
            """
        );
        using JsonDocument pooled = JsonDocument.Parse(
            """
            {"membershipType":"team","teamUsage":{"pooled":{"used":2500,"limit":10000}}}
            """
        );

        ProviderSnapshot personal = CursorUsageMapper.Map(Account(), overall.RootElement, null, null, Now, "test");
        ProviderSnapshot team = CursorUsageMapper.Map(Account(), pooled.RootElement, null, null, Now, "test");

        Assert.Equal(73.84, Assert.Single(personal.Meters).UsedPercent!.Value, 2);
        Assert.Equal(25, Assert.Single(team.Meters).UsedPercent);
    }

    [Fact]
    public void ZeroValuedPlanFallsThroughToEnterpriseOverall()
    {
        using JsonDocument summary = JsonDocument.Parse(
            """
            {
              "individualUsage": {
                "plan":{"used":0,"limit":0},
                "overall":{"used":2500,"limit":10000}
              }
            }
            """
        );

        UsageMeter meter = Assert.Single(
            CursorUsageMapper.Map(Account(), summary.RootElement, null, null, Now, "test").Meters
        );

        Assert.Equal(25, meter.UsedPercent);
        Assert.Equal(25m, meter.Used);
        Assert.Equal(100m, meter.Limit);
    }

    [Fact]
    public void LegacyRequestPlanReplacesModernMeters()
    {
        using JsonDocument summary = JsonDocument.Parse(
            """
            {"individualUsage":{"plan":{"totalPercentUsed":44,"autoPercentUsed":20,"apiPercentUsed":68}}}
            """
        );
        using JsonDocument legacy = JsonDocument.Parse(
            """
            {"gpt-4":{"numRequests":12,"numRequestsTotal":24,"maxRequestUsage":100}}
            """
        );

        ProviderSnapshot snapshot = CursorUsageMapper.Map(
            Account(),
            summary.RootElement,
            legacy.RootElement,
            null,
            Now,
            "test"
        );

        UsageMeter meter = Assert.Single(snapshot.Meters);
        Assert.Equal("cursor:requests", meter.Key.Value);
        Assert.Equal(MeterUnit.Requests, meter.Unit);
        Assert.Equal(24m, meter.Used);
        Assert.Equal(100m, meter.Limit);
    }

    [Fact]
    public async Task StrategyBuildsExactCookieAndFetchesAllCursorEndpoints()
    {
        string token = Jwt("auth0|user-123", Now.AddHours(1));
        using var database = TestDatabase.Create("cursorAuth/accessToken", token);
        var handler = new CursorHandler();
        var adapter = new CursorProviderAdapter(new HttpClient(handler), new FixedClock(), database.Path);
        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));

        FetchResult result = await Assert.Single(adapter.CreateLimitStrategies(account)).FetchAsync(account, default);

        Assert.True(result.IsSuccess);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal($"WorkosCursorSessionToken=user-123%3A%3A{token}", request.Cookie)
        );
        Assert.Contains(handler.Requests, request => request.Path == "/api/usage-summary");
        Assert.Contains(handler.Requests, request => request.Path == "/api/auth/me");
        Assert.Contains(handler.Requests, request => request.Path == "/api/usage?user=auth0%7Cuser-123");
        Assert.Equal("cursor:total", Assert.Single(result.Snapshot!.Meters).Key.Value);
    }

    [Fact]
    public async Task AccountSwitchIsRejectedBeforeNetworkAndDoesNotLeakToken()
    {
        string token = Jwt("auth0|new-user", Now.AddHours(1));
        using var database = TestDatabase.Create("cursorAuth/accessToken", token);
        var handler = new CursorHandler();
        var source = new CursorCredentialSource(database.Path, new FixedClock());
        var strategy = new CursorWebLimitStrategy(new HttpClient(handler), new FixedClock(), source);
        var oldAccount = new ProviderAccount(
            new AccountKey(BuiltInProviderDescriptors.Cursor.Id, "auth0|old-user"),
            "Cursor",
            null,
            "Cursor app session",
            1,
            true
        );

        FetchResult result = await strategy.FetchAsync(oldAccount, default);

        Assert.Equal(FetchFailureKind.AccountMismatch, result.FailureKind);
        Assert.Empty(handler.Requests);
        Assert.DoesNotContain(token, result.SafeMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task LockedDatabaseFailsDiscoveryInsteadOfReturningAuthoritativeEmpty()
    {
        string token = Jwt("auth0|user-123", Now.AddHours(1));
        using var database = TestDatabase.Create("cursorAuth/accessToken", token);
        SqliteConnection.ClearAllPools();

        // Hold the file with an exclusive lock so SQLite cannot open it.
        await using var lockedStream = new FileStream(
            database.Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );

        var adapter = new CursorProviderAdapter(new HttpClient(), new FixedClock(), database.Path);

        await Assert.ThrowsAsync<IOException>(() => adapter.DiscoverAccountsAsync(default));
    }

    [Fact]
    public async Task LockedDatabaseSurfacesTransientFailureInFetch()
    {
        string token = Jwt("auth0|user-123", Now.AddHours(1));
        using var database = TestDatabase.Create("cursorAuth/accessToken", token);
        SqliteConnection.ClearAllPools();

        // Pre-discover the account while the DB is still accessible.
        var adapter = new CursorProviderAdapter(new HttpClient(), new FixedClock(), database.Path);
        ProviderAccount account = Assert.Single(await adapter.DiscoverAccountsAsync(default));
        SqliteConnection.ClearAllPools();

        // Now lock the file exclusively.
        await using var lockedStream = new FileStream(
            database.Path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None
        );

        var strategy = Assert.Single(adapter.CreateLimitStrategies(account));
        FetchResult result = await strategy.FetchAsync(account, default);

        // The fetch must not crash; it returns a failure (not success).
        Assert.False(result.IsSuccess);
    }

    private static AccountKey Account() => new(BuiltInProviderDescriptors.Cursor.Id, "auth0|user-123");

    private static string Jwt(string subject, DateTimeOffset expiresAt)
    {
        string header = Base64Url("""{"alg":"none","typ":"JWT"}""");
        string payload = Base64Url(
            JsonSerializer.Serialize(new { sub = subject, exp = expiresAt.ToUnixTimeSeconds() })
        );
        return $"{header}.{payload}.test-signature";
    }

    private static string Base64Url(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => Now;
    }

    private sealed class CursorHandler : HttpMessageHandler
    {
        public List<(string Path, string? Cookie)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            Requests.Add((request.RequestUri!.PathAndQuery, request.Headers.GetValues("Cookie").Single()));
            string json = request.RequestUri.AbsolutePath switch
            {
                "/api/usage-summary" => """
                    {"individualUsage":{"plan":{"used":100,"limit":1000,"totalPercentUsed":10}}}
                    """,
                "/api/auth/me" => """{"sub":"auth0|user-123","email":"user@example.com"}""",
                "/api/usage" => "{}",
                _ => "{}",
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                }
            );
        }
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly string directory;
        public string Path { get; }

        private TestDatabase(string directory, string path)
        {
            this.directory = directory;
            Path = path;
        }

        public static TestDatabase Create(string key, object value)
        {
            string directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "ai-limits-cursor-" + Guid.NewGuid().ToString("N")
            );
            Directory.CreateDirectory(directory);
            string path = System.IO.Path.Combine(directory, "state.vscdb");
            using var connection = new SqliteConnection(
                new SqliteConnectionStringBuilder { DataSource = path }.ToString()
            );
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "CREATE TABLE ItemTable(key TEXT PRIMARY KEY, value BLOB); INSERT INTO ItemTable(key, value) VALUES($key, $value);";
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.ExecuteNonQuery();
            return new TestDatabase(directory, path);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch { }
        }
    }
}
