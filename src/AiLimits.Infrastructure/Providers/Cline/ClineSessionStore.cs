// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Text.Json;
using AiLimits.Application.Abstractions;

namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>A ClinePass session the app refreshed itself.</summary>
internal sealed record ClineSession(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt);

/// <summary>
/// Persists app-refreshed Cline sessions through <see cref="ISecretStore"/> —
/// on Windows that is Credential Manager, so the bearer and refresh tokens are
/// encrypted at rest under the user's account rather than sitting in a
/// world-readable JSON file. The CLI's secrets.json is never written; the app
/// stays read-only on provider credential stores. On load, the caller compares
/// expiries and uses whichever session (CLI-stored or app-refreshed) is
/// fresher, so a CLI re-login or CLI-side refresh always wins over a stale
/// cache.
///
/// Each field is a separate credential entry because Credential Manager caps a
/// single blob at 2560 UTF-16 characters, which a bearer and a refresh JWT can
/// jointly exceed.
///
/// Every operation is best-effort: if the secret store is unavailable the
/// strategy simply refreshes again on the next fetch, exactly as it did when
/// the cache file was missing.
/// </summary>
internal sealed class ClineSessionStore(ISecretStore secrets, string? legacyCachePath = null)
{
    private const string Scope = "cline";
    private const string AccessTokenKey = "session.access-token";
    private const string RefreshTokenKey = "session.refresh-token";
    private const string ExpiresAtKey = "session.expires-at";

    private bool _legacyMigrationAttempted;

    public async Task<ClineSession?> LoadAsync(CancellationToken cancellationToken)
    {
        await MigrateLegacyCacheAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string? accessToken = await secrets.GetAsync(Scope, AccessTokenKey, cancellationToken).ConfigureAwait(false);
            string? expires = await secrets.GetAsync(Scope, ExpiresAtKey, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(accessToken) || expires is null ||
                !DateTimeOffset.TryParse(expires, CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiresAt))
            {
                return null;
            }
            string? refreshToken = await secrets.GetAsync(Scope, RefreshTokenKey, cancellationToken).ConfigureAwait(false);
            return new ClineSession(accessToken, string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken, expiresAt);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    public Task SaveAsync(ClineSession session, CancellationToken cancellationToken) =>
        TrySaveAsync(session, cancellationToken);

    /// <summary>
    /// Same write path as <see cref="SaveAsync"/>, but reports recoverable
    /// failures to the caller instead of swallowing them, so a caller holding
    /// the only other copy of the session (the legacy plaintext cache) can
    /// keep it for a later retry rather than losing the tokens outright.
    /// </summary>
    public async Task<bool> TrySaveAsync(ClineSession session, CancellationToken cancellationToken)
    {
        try
        {
            await secrets.SetAsync(Scope, AccessTokenKey, session.AccessToken, cancellationToken).ConfigureAwait(false);
            await secrets.SetAsync(Scope, ExpiresAtKey,
                session.ExpiresAt.ToString("o", CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false);
            if (session.RefreshToken is { } refreshToken)
            {
                await secrets.SetAsync(Scope, RefreshTokenKey, refreshToken, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await secrets.DeleteAsync(Scope, RefreshTokenKey, cancellationToken).ConfigureAwait(false);
            }
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Best effort: without the cache the strategy just refreshes again
            // next fetch.
            return false;
        }
    }

    /// <summary>
    /// Moves a session left behind by an older build's plaintext
    /// <c>cline-session.json</c> into the secret store, then deletes the file.
    /// Runs at most once per instance, and is a no-op once the file is gone.
    /// </summary>
    private async Task MigrateLegacyCacheAsync(CancellationToken cancellationToken)
    {
        if (_legacyMigrationAttempted || legacyCachePath is null)
        {
            return;
        }
        _legacyMigrationAttempted = true;
        try
        {
            if (!File.Exists(legacyCachePath))
            {
                return;
            }
            if (ReadLegacyFile(legacyCachePath) is not { } session)
            {
                // Unparseable: there is nothing to migrate, so there is no
                // safe point at which deleting the file loses usable tokens.
                // Keep it rather than destroy data we could not read.
                return;
            }
            if (!await TrySaveAsync(session, cancellationToken).ConfigureAwait(false))
            {
                // The vault is unavailable right now; keep the plaintext copy
                // and retry on the next load instead of deleting the only
                // remaining copy of the tokens.
                _legacyMigrationAttempted = false;
                return;
            }
            File.Delete(legacyCachePath);
            // Older builds wrote through a sibling temp file; a crash between
            // write and rename could have left one holding the same tokens.
            File.Delete(legacyCachePath + ".tmp");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
        }
    }

    private static ClineSession? ReadLegacyFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("accessToken", out JsonElement token) ||
            token.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(token.GetString()) ||
            !root.TryGetProperty("expiresAt", out JsonElement expires) ||
            expires.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(expires.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTimeOffset expiresAt))
        {
            return null;
        }
        string? refreshToken =
            root.TryGetProperty("refreshToken", out JsonElement refresh) &&
            refresh.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(refresh.GetString())
                ? refresh.GetString()
                : null;
        return new ClineSession(token.GetString()!, refreshToken, expiresAt);
    }

    /// <summary>
    /// A missing, locked or oversized credential must never fail a refresh.
    /// <see cref="ArgumentOutOfRangeException"/> is what the Windows store
    /// raises for a token past Credential Manager's per-entry size cap.
    /// </summary>
    private static bool IsRecoverable(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or JsonException
            or ArgumentOutOfRangeException or System.ComponentModel.Win32Exception;
}
