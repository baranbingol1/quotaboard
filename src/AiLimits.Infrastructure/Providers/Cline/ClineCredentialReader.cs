// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>
/// A resolved Cline credential plus a log-safe label for its origin. CLI
/// credentials also carry the session's expiry and refresh token so the
/// strategy can mint a fresh bearer itself — the CLI's idToken only lives
/// about an hour, so without refreshing, the card would only be live within
/// an hour of the user's last Cline session.
/// <para><paramref name="IsWorkOsSession"/> distinguishes a WorkOS session
/// token (what Cline sign-in produces) from a dashboard API key: api.cline.bot
/// only accepts the former as <c>Bearer workos:&lt;token&gt;</c> and rejects
/// the bare token with 401 on every endpoint.</para>
/// </summary>
public sealed record ClineCredential(
    string Token,
    string SourceLabel,
    DateTimeOffset? ExpiresAt = null,
    string? RefreshToken = null,
    bool IsWorkOsSession = false,
    string? Email = null,
    string? AccountFingerprint = null,
    ClineBillingScope? BillingScope = null
);

/// <summary>
/// The Cline billing account the local session is currently using. A user can
/// switch personal and organization billing without changing <c>userInfo.id</c>
/// or email, so identity alone is not enough to reuse a cached session.
/// </summary>
public abstract record ClineBillingScope
{
    private ClineBillingScope() { }

    public sealed record Personal : ClineBillingScope
    {
        public static readonly Personal Instance = new();
    }

    public sealed record Organization(string OrganizationId) : ClineBillingScope;
}

/// <summary>
/// Resolves the ClinePass bearer token. Precedence: CLINE_API_KEY, then
/// CLINEPASS_API_KEY, then the credential the Cline CLI stores at
/// ~/.cline/data/secrets.json ("cline:clineAccountId"). The CLI value is a JSON
/// session blob whose idToken is the API bearer; a bare token is used as-is.
/// The token is never logged and never appears in failure messages; a rejected
/// token surfaces as a 401 from the API and simply marks the strategy
/// unavailable.
/// </summary>
internal static class ClineCredentialReader
{
    internal static ClineCredential? Resolve() =>
        ResolveEnvironment() ?? ResolveSecrets(DefaultSecretsPath(), DefaultProvidersPath());

    internal static ClineCredential? ResolveEnvironment()
    {
        string? apiKey = ReadEnvironmentVariable("CLINE_API_KEY");
        if (apiKey is not null)
        {
            return new ClineCredential(apiKey, "API key (CLINE_API_KEY)");
        }
        string? passKey = ReadEnvironmentVariable("CLINEPASS_API_KEY");
        if (passKey is not null)
        {
            return new ClineCredential(passKey, "API key (CLINEPASS_API_KEY)");
        }
        return null;
    }

    internal static ClineCredential? ResolveSecrets(string path, string? providersPath = null)
    {
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("cline:clineAccountId", out JsonElement value)
                && value.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(value.GetString())
            )
            {
                return ExtractCredential(value.GetString()!.Trim(), providersPath);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return null;
    }

    // The CLI stores a session blob {"idToken": <jwt>, "refreshToken": …,
    // "expiresAt": <unix s>, "userInfo": {…}}; the idToken is the api.cline.bot
    // bearer. Newer CLI builds may store the bare token — then the value is
    // used as-is. Anything that cannot ride in an ASCII-only Authorization
    // header (the blob itself can hold a non-ASCII displayName) is not a
    // usable credential.
    private static ClineCredential? ExtractCredential(string stored, string? providersPath)
    {
        string candidate = stored;
        string? refreshToken = null;
        string? email = null;
        string? accountFingerprint = null;
        ClineBillingScope? billingScope = null;
        DateTimeOffset? expiresAt = null;
        bool workOsSession = false;
        if (stored.StartsWith('{'))
        {
            try
            {
                using JsonDocument blob = JsonDocument.Parse(stored);
                if (blob.RootElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (string field in new[] { "idToken", "accessToken" })
                    {
                        if (
                            blob.RootElement.TryGetProperty(field, out JsonElement nested)
                            && nested.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(nested.GetString())
                        )
                        {
                            candidate = nested.GetString()!.Trim();
                            workOsSession = true;
                            break;
                        }
                    }
                    if (
                        blob.RootElement.TryGetProperty("refreshToken", out JsonElement refresh)
                        && refresh.ValueKind == JsonValueKind.String
                        && !string.IsNullOrWhiteSpace(refresh.GetString())
                    )
                    {
                        refreshToken = refresh.GetString()!.Trim();
                    }
                    expiresAt = ParseExpiry(blob.RootElement);
                    email = ReadEmail(blob.RootElement);
                    billingScope = ReadBillingScope(blob.RootElement, providersPath);
                    accountFingerprint = ReadAccountFingerprint(blob.RootElement, email, billingScope);
                }
            }
            catch (JsonException)
            {
                // Not a blob after all; the stored value is the token.
            }
        }
        // A bare stored value can be either a pasted dashboard API key or a raw
        // session token; only the JWT shape gets the WorkOS scheme.
        return IsHeaderSafe(candidate)
            ? new ClineCredential(
                candidate,
                SourceLabelFor(billingScope),
                expiresAt,
                refreshToken,
                workOsSession || LooksLikeJwt(candidate),
                email,
                accountFingerprint,
                billingScope
            )
            : null;
    }

    private static string? ReadEmail(JsonElement root) =>
        root.TryGetProperty("userInfo", out JsonElement userInfo)
        && userInfo.ValueKind == JsonValueKind.Object
        && userInfo.TryGetProperty("email", out JsonElement email)
        && email.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(email.GetString())
            ? email.GetString()!.Trim()
            : null;

    internal static string SourceLabelFor(ClineBillingScope? billingScope) =>
        billingScope switch
        {
            ClineBillingScope.Organization => "Cline CLI account (organization)",
            ClineBillingScope.Personal => "Cline CLI account (personal)",
            _ => "Cline CLI account",
        };

    private static string? ReadAccountFingerprint(JsonElement root, string? email, ClineBillingScope? billingScope)
    {
        // User id/email stay the same when Cline switches personal and
        // organization billing. The fingerprint must include that scope, or
        // the session store will reuse the previous billing account's token.
        if (billingScope is null)
        {
            return null;
        }

        string? identity = null;
        if (
            root.TryGetProperty("userInfo", out JsonElement userInfo)
            && userInfo.ValueKind == JsonValueKind.Object
            && userInfo.TryGetProperty("id", out JsonElement id)
            && id.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(id.GetString())
        )
        {
            identity = "id:" + id.GetString()!.Trim().ToLowerInvariant();
        }
        else if (!string.IsNullOrWhiteSpace(email))
        {
            identity = "email:" + email.Trim().ToLowerInvariant();
        }
        if (identity is null)
        {
            return null;
        }

        string scopedIdentity = billingScope switch
        {
            ClineBillingScope.Organization organization => identity
                + "|org:"
                + organization.OrganizationId.Trim().ToLowerInvariant(),
            _ => identity + "|personal",
        };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(scopedIdentity)));
    }

    internal static ClineBillingScope? ReadBillingScope(JsonElement sessionRoot, string? providersPath)
    {
        if (TryReadPersistedOrganizationId(providersPath) is { } persistedOrganizationId)
        {
            return new ClineBillingScope.Organization(persistedOrganizationId);
        }

        if (
            sessionRoot.TryGetProperty("userInfo", out JsonElement userInfo)
            && userInfo.ValueKind == JsonValueKind.Object
            && userInfo.TryGetProperty("organizations", out JsonElement organizations)
            && organizations.ValueKind == JsonValueKind.Array
        )
        {
            string? activeOrganizationId = null;
            foreach (JsonElement organization in organizations.EnumerateArray())
            {
                if (organization.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                if (
                    !organization.TryGetProperty("active", out JsonElement active)
                    || active.ValueKind != JsonValueKind.True
                )
                {
                    continue;
                }
                if (
                    organization.TryGetProperty("organizationId", out JsonElement organizationId)
                    && organizationId.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(organizationId.GetString())
                )
                {
                    activeOrganizationId = organizationId.GetString()!.Trim();
                    break;
                }
                return null;
            }
            return activeOrganizationId is null
                ? ClineBillingScope.Personal.Instance
                : new ClineBillingScope.Organization(activeOrganizationId);
        }

        return providersPath is not null && File.Exists(providersPath) ? ClineBillingScope.Personal.Instance : null;
    }

    private static string? TryReadPersistedOrganizationId(string? providersPath)
    {
        if (string.IsNullOrWhiteSpace(providersPath) || !File.Exists(providersPath))
        {
            return null;
        }
        try
        {
            using FileStream stream = new(providersPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using JsonDocument document = JsonDocument.Parse(stream);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }
            foreach (string providerId in new[] { "cline", "cline-pass" })
            {
                if (ReadProviderOrganizationId(document.RootElement, providerId) is { } organizationId)
                {
                    return organizationId;
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        catch (JsonException) { }
        return null;
    }

    private static string? ReadProviderOrganizationId(JsonElement root, string providerId)
    {
        if (
            !root.TryGetProperty("providers", out JsonElement providers)
            || providers.ValueKind != JsonValueKind.Object
            || !providers.TryGetProperty(providerId, out JsonElement provider)
            || provider.ValueKind != JsonValueKind.Object
        )
        {
            return null;
        }
        JsonElement settings = provider;
        if (
            provider.TryGetProperty("settings", out JsonElement nestedSettings)
            && nestedSettings.ValueKind == JsonValueKind.Object
        )
        {
            settings = nestedSettings;
        }
        if (
            !settings.TryGetProperty("auth", out JsonElement auth)
            || auth.ValueKind != JsonValueKind.Object
            || !auth.TryGetProperty("organizationId", out JsonElement organizationId)
            || organizationId.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(organizationId.GetString())
        )
        {
            return null;
        }
        return organizationId.GetString()!.Trim();
    }

    private static bool LooksLikeJwt(string value) =>
        value.Count(c => c == '.') == 2 && !value.StartsWith('.') && !value.EndsWith('.');

    // The CLI writes expiresAt as unix seconds; tolerate milliseconds and ISO
    // strings so a CLI format change degrades to "expiry unknown", not a
    // broken card.
    internal static DateTimeOffset? ParseExpiry(JsonElement element)
    {
        if (!element.TryGetProperty("expiresAt", out JsonElement raw))
        {
            return null;
        }
        if (raw.ValueKind == JsonValueKind.Number && raw.TryGetInt64(out long stamp))
        {
            try
            {
                return stamp > 10_000_000_000L
                    ? DateTimeOffset.FromUnixTimeMilliseconds(stamp)
                    : DateTimeOffset.FromUnixTimeSeconds(stamp);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }
        if (
            raw.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(
                raw.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset parsed
            )
        )
        {
            return parsed;
        }
        return null;
    }

    private static bool IsHeaderSafe(string value) =>
        value.Length > 0 && value.All(c => c is >= (char)33 and <= (char)126);

    private static string DefaultSecretsPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cline",
            "data",
            "secrets.json"
        );

    private static string DefaultProvidersPath() =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".cline",
            "data",
            "settings",
            "providers.json"
        );

    private static string? ReadEnvironmentVariable(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        value = value.Trim().Trim('"', '\'').Trim();
        return value.Length == 0 ? null : value;
    }
}
