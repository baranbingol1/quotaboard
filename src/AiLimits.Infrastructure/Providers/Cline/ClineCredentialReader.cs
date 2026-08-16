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
    string? AccountFingerprint = null
);

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
    internal static ClineCredential? Resolve() => ResolveEnvironment() ?? ResolveSecrets(DefaultSecretsPath());

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

    internal static ClineCredential? ResolveSecrets(string path)
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
                return ExtractCredential(value.GetString()!.Trim());
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
    private static ClineCredential? ExtractCredential(string stored)
    {
        string candidate = stored;
        string? refreshToken = null;
        string? email = null;
        string? accountFingerprint = null;
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
                    accountFingerprint = ReadAccountFingerprint(blob.RootElement, email);
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
                "Cline CLI account",
                expiresAt,
                refreshToken,
                workOsSession || LooksLikeJwt(candidate),
                email,
                accountFingerprint
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

    private static string? ReadAccountFingerprint(JsonElement root, string? email)
    {
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
        return identity is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
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
