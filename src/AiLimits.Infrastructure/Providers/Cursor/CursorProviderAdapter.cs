// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLimits.Infrastructure.Providers.Cursor;

internal sealed record CursorCredential(string AccessToken, string Subject, string CookieUserId);

public sealed class CursorCredentialSource
{
    private const string AccessTokenKey = "cursorAuth/accessToken";
    private static readonly TimeSpan MinimumLifetime = TimeSpan.FromMinutes(1);
    private readonly string databasePath;
    private readonly IClock clock;
    private readonly ILogger<CursorCredentialSource> _logger;

    public CursorCredentialSource(string? databasePath = null, IClock? clock = null, ILogger<CursorCredentialSource>? logger = null)
    {
        this.databasePath = databasePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Cursor", "User", "globalStorage", "state.vscdb");
        this.clock = clock ?? new SystemClock();
        _logger = logger ?? NullLogger<CursorCredentialSource>.Instance;
    }

    internal async Task<CursorCredential?> ReadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(databasePath)) return null;

        try
        {
            return await ReadWithBusyRetryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (SqliteException ex) when (IsBusyOrLocked(ex))
        {
            // After the retry inside ReadWithBusyRetryAsync, the database is
            // still locked by the Cursor app. Surface a distinct transient
            // failure instead of the "not connected" null.
            _logger.LogWarning(ex, "Cursor app is holding the database (SQLITE_BUSY/LOCKED after retry).");
            throw new IOException("Cursor app is holding the database; please close it and try again.", ex);
        }
        catch (Exception error) when (error is SqliteException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(error, "Cursor credential store could not be read.");
            return null;
        }
    }

    private async Task<CursorCredential?> ReadWithBusyRetryAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var connectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Pooling = false
                }.ToString();
                await using var connection = new SqliteConnection(connectionString);
                await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT value FROM ItemTable WHERE key = $key LIMIT 1;";
                command.Parameters.AddWithValue("$key", AccessTokenKey);
                object? storedValue = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                string? accessToken = NormalizeStoredToken(DecodeSqliteValue(storedValue));
                return TryParse(accessToken, clock.UtcNow, out CursorCredential? credential) ? credential : null;
            }
            catch (SqliteException ex) when (IsBusyOrLocked(ex) && attempt < 1)
            {
                _logger.LogWarning(ex, "Cursor database was busy; retrying once after a short delay.");
                await Task.Delay(200, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static bool IsBusyOrLocked(SqliteException ex) =>
        ex.SqliteErrorCode == 5 || ex.SqliteErrorCode == 6;

    internal static bool TryParse(string? accessToken, DateTimeOffset now, out CursorCredential? credential)
    {
        credential = null;
        if (string.IsNullOrWhiteSpace(accessToken)) return false;

        string[] segments = accessToken.Split('.');
        if (segments.Length < 2) return false;

        try
        {
            string payload = segments[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using JsonDocument document = JsonDocument.Parse(Convert.FromBase64String(payload));
            JsonElement root = document.RootElement;
            string? subject = root.TryGetProperty("sub", out JsonElement subjectElement)
                && subjectElement.ValueKind == JsonValueKind.String
                ? subjectElement.GetString()?.Trim()
                : null;
            if (string.IsNullOrWhiteSpace(subject)
                || !root.TryGetProperty("exp", out JsonElement expiryElement)
                || !expiryElement.TryGetInt64(out long expirySeconds)
                || expirySeconds <= now.Add(MinimumLifetime).ToUnixTimeSeconds())
            {
                return false;
            }

            string cookieUserId = subject.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault() ?? string.Empty;
            if (cookieUserId.Length == 0 || cookieUserId.Any(character =>
                    !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
            {
                return false;
            }

            credential = new CursorCredential(accessToken, subject, cookieUserId);
            return true;
        }
        catch (Exception error) when (error is FormatException or JsonException)
        {
            return false;
        }
    }

    private static string? NormalizeStoredToken(string? value)
    {
        string? trimmed = value?.Trim().TrimEnd('\0');
        if (string.IsNullOrEmpty(trimmed)) return null;
        if (trimmed[0] == '"' && trimmed[^1] == '"')
        {
            try
            {
                return JsonSerializer.Deserialize<string>(trimmed)?.Trim();
            }
            catch (JsonException)
            {
                return null;
            }
        }
        return trimmed;
    }

    private static string? DecodeSqliteValue(object? value)
    {
        if (value is string text) return text;
        if (value is not byte[] bytes || bytes.Length == 0) return null;
        if (bytes.Length >= 2 && bytes[1] == 0)
        {
            return Encoding.Unicode.GetString(bytes).TrimEnd('\0');
        }
        return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
    }
}

public sealed class CursorProviderAdapter : IProviderAdapter
{
    private readonly HttpClient httpClient;
    private readonly IClock clock;
    private readonly CursorCredentialSource credentialSource;

    public CursorProviderAdapter(HttpClient httpClient, IClock clock, string? databasePath = null, ILogger<CursorProviderAdapter>? logger = null)
        : this(httpClient, clock, new CursorCredentialSource(databasePath, clock), logger)
    {
    }

    public CursorProviderAdapter(HttpClient httpClient, IClock clock, CursorCredentialSource credentialSource, ILogger<CursorProviderAdapter>? logger = null)
    {
        this.httpClient = httpClient;
        this.clock = clock;
        this.credentialSource = credentialSource;
    }

    public ProviderDescriptor Descriptor => BuiltInProviderDescriptors.Cursor;

    public async Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        try
        {
            CursorCredential? credential = await credentialSource.ReadAsync(cancellationToken).ConfigureAwait(false);
            return credential is null
                ? []
                : [new ProviderAccount(
                    new AccountKey(Descriptor.Id, credential.Subject),
                    "Cursor",
                    null,
                    "Cursor app session",
                    1,
                    IsConnected: true)];
        }
        catch (IOException)
        {
            // Database is locked by the Cursor app; no accounts can be discovered.
            return [];
        }
    }

    public IReadOnlyList<ILimitFetchStrategy> CreateLimitStrategies(ProviderAccount account) =>
        [new CursorWebLimitStrategy(httpClient, clock, credentialSource)];

    public IReadOnlyList<ITokenUsageSource> CreateTokenSources(ProviderAccount account) =>
        [new CursorUsageEventsTokenSource(httpClient, clock, credentialSource)];
}

internal sealed class CursorWebLimitStrategy(
    HttpClient httpClient,
    IClock clock,
    CursorCredentialSource credentialSource) : ILimitFetchStrategy
{
    private static readonly Uri UsageSummaryUri = new("https://cursor.com/api/usage-summary");
    private static readonly Uri IdentityUri = new("https://cursor.com/api/auth/me");

    public string Id => "cursor.app-session";
    public int Order => 10;

    public async Task<StrategyAvailabilityResult> CheckAvailabilityAsync(
        ProviderAccount account,
        CancellationToken cancellationToken)
    {
        try
        {
            CursorCredential? credential = await credentialSource.ReadAsync(cancellationToken).ConfigureAwait(false);
            return credential is null
                ? new StrategyAvailabilityResult(StrategyAvailability.NotConfigured, "Cursor app session was not found.")
                : StrategyAvailabilityResult.Ready();
        }
        catch (IOException)
        {
            return new StrategyAvailabilityResult(StrategyAvailability.TemporarilyUnavailable, "Cursor app is holding the database.");
        }
    }

    public async Task<FetchResult> FetchAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        long started = Stopwatch.GetTimestamp();
        CursorCredential? credential;
        try
        {
            credential = await credentialSource.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return Failure(FetchFailureKind.Network, "Cursor app is holding the database; please close it and try again.", started);
        }
        if (credential is null)
        {
            return Failure(FetchFailureKind.Authentication, "Cursor app session is unavailable.", started);
        }
        if (!string.Equals(credential.Subject, account.Key.Value, StringComparison.Ordinal))
        {
            return Failure(FetchFailureKind.AccountMismatch, "Cursor app session belongs to a different account.", started);
        }

        string cookieHeader = BuildCookieHeader(credential);
        using var summaryRequest = BuildRequest(UsageSummaryUri, cookieHeader);
        using ProviderJsonResult summaryExchange = await ProviderHttp.GetJsonAsync(
            httpClient, summaryRequest, Id, "Cursor", started, cancellationToken).ConfigureAwait(false);
        if (!summaryExchange.IsSuccess)
        {
            return summaryExchange.Failure!;
        }

        JsonDocument summary = summaryExchange.Document!;
        using JsonDocument? identity = await TryFetchJsonAsync(IdentityUri, cookieHeader, cancellationToken)
            .ConfigureAwait(false);
        string? authenticatedSubject = CursorUsageMapper.ReadString(identity?.RootElement, "sub");
        if (!string.IsNullOrWhiteSpace(authenticatedSubject)
            && !string.Equals(authenticatedSubject, credential.Subject, StringComparison.Ordinal)
            && !string.Equals(authenticatedSubject, credential.CookieUserId, StringComparison.Ordinal))
        {
            return Failure(FetchFailureKind.AccountMismatch, "Cursor returned usage for a different account.", started);
        }

        string legacyUserId = authenticatedSubject ?? credential.CookieUserId;
        var legacyUri = new Uri("https://cursor.com/api/usage?user=" + Uri.EscapeDataString(legacyUserId));
        using JsonDocument? legacy = await TryFetchJsonAsync(legacyUri, cookieHeader, cancellationToken)
            .ConfigureAwait(false);
        ProviderSnapshot snapshot = CursorUsageMapper.Map(
            account.Key,
            summary.RootElement,
            legacy?.RootElement,
            identity?.RootElement,
            clock.UtcNow,
            Id);
        return FetchResult.Success(snapshot, Id, Stopwatch.GetElapsedTime(started));
    }

    internal static string BuildCookieHeader(CursorCredential credential) =>
        $"WorkosCursorSessionToken={credential.CookieUserId}%3A%3A{credential.AccessToken}";

    internal static HttpRequestMessage BuildRequest(Uri uri, string cookieHeader)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        return request;
    }

    private async Task<JsonDocument?> TryFetchJsonAsync(
        Uri uri,
        string cookieHeader,
        CancellationToken cancellationToken)
    {
        using var request = BuildRequest(uri, cookieHeader);
        ProviderJsonResult exchange = await ProviderHttp.GetJsonAsync(
            httpClient, request, Id, "Cursor", Stopwatch.GetTimestamp(), cancellationToken).ConfigureAwait(false);
        if (!exchange.IsSuccess)
        {
            // Best-effort enrichment: swallow the classified failure.
            return null;
        }
        // Ownership of the document transfers to the caller; nothing else to dispose.
        return exchange.Document;
    }

    private FetchResult Failure(FetchFailureKind kind, string message, long started) =>
        FetchResult.Failure(kind, message, FallbackPolicy.TryNextStrategy, Id, Stopwatch.GetElapsedTime(started));
}
