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
/// <para>
/// <b>Atomicity.</b> Because every field is a separate entry, a save that
/// writes access then expiry then refresh can fail halfway, leaving the live
/// keys holding a half-written session (new access + stale or missing
/// refresh). To prevent that, <see cref="TrySaveAsync"/> writes every field
/// to <i>staging</i> keys first. Only when all staging writes succeed does it
/// write a commit marker and promote staging into the live keys. If any
/// staging write fails, the live keys are untouched and the staging keys are
/// cleaned up. <see cref="LoadAsync"/> only ever reads live keys; if it finds
/// a commit marker (a promotion was interrupted), it finishes the promotion
/// before reading. The staging keys intentionally assume one writer; callers
/// that can refresh must hold the cross-process lock in
/// <see cref="ClinePassLimitStrategy"/> across load, refresh, and save.
/// </para>
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

    // Staging keys: written first, promoted to the live keys above only after
    // every field succeeds. LoadAsync never reads these.
    private const string StagingAccessTokenKey = "session.access-token.staging";
    private const string StagingExpiresAtKey = "session.expires-at.staging";
    private const string StagingRefreshTokenKey = "session.refresh-token.staging";

    // "1" = the live refresh key should be deleted during promotion;
    // "0" = the staging refresh value should be copied to the live key.
    private const string StagingClearRefreshKey = "session.clear-refresh.staging";

    // Present only between "all staging writes succeeded" and "promotion to
    // live keys finished." Its presence tells LoadAsync to finish the
    // promotion before reading.
    private const string CommitKey = "session.commit";

    private bool _legacyMigrationAttempted;

    public async Task<ClineSession?> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A commit marker means a previous save was interrupted between
            // staging and promotion. Finish the promotion so the live keys are
            // consistent before we read them.
            if (await secrets.GetAsync(Scope, CommitKey, cancellationToken).ConfigureAwait(false) is not null)
            {
                if (!await TryRecoverInterruptedPromotionAsync(cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
            }

            ClineSession? live = await ReadLiveSessionAsync(cancellationToken).ConfigureAwait(false);
            if (live is not null)
            {
                // A committed, structurally valid vault session is authoritative.
                // In particular, never write a retained legacy file back over a
                // newer session merely because an earlier deletion was denied.
                CleanupLegacyFilesBestEffort();
                return live;
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // The vault's state is unknown, not empty. Retain any plaintext
            // fallback and retry once the vault is available again.
            return null;
        }

        // The vault was available but had no structurally valid live session.
        // Only this state is allowed to migrate legacy plaintext into it.
        await MigrateLegacyCacheAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadLiveSessionAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            return null;
        }
    }

    private async Task<ClineSession?> ReadLiveSessionAsync(CancellationToken cancellationToken)
    {
        string? accessToken = await secrets.GetAsync(Scope, AccessTokenKey, cancellationToken).ConfigureAwait(false);
        string? expires = await secrets.GetAsync(Scope, ExpiresAtKey, cancellationToken).ConfigureAwait(false);
        if (
            string.IsNullOrWhiteSpace(accessToken)
            || expires is null
            || !DateTimeOffset.TryParse(
                expires,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset expiresAt
            )
        )
        {
            return null;
        }
        string? refreshToken = await secrets.GetAsync(Scope, RefreshTokenKey, cancellationToken).ConfigureAwait(false);
        return new ClineSession(accessToken, string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken, expiresAt);
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
        // Phase 1: stage every field. If any write fails, clean up whatever
        // staging was written and leave the live keys untouched.
        try
        {
            await secrets
                .SetAsync(Scope, StagingAccessTokenKey, session.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            await secrets
                .SetAsync(
                    Scope,
                    StagingExpiresAtKey,
                    session.ExpiresAt.ToString("o", CultureInfo.InvariantCulture),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (session.RefreshToken is { } refreshToken)
            {
                await secrets
                    .SetAsync(Scope, StagingRefreshTokenKey, refreshToken, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                // Clear any leftover refresh staging from a prior save so the
                // promotion step does not re-stage a stale value.
                await secrets.DeleteAsync(Scope, StagingRefreshTokenKey, cancellationToken).ConfigureAwait(false);
            }
            await secrets
                .SetAsync(Scope, StagingClearRefreshKey, session.RefreshToken is null ? "1" : "0", cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            await CleanupStagingAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        // Phase 2: commit, then promote staging into the live keys. If the
        // process is interrupted, the commit marker tells the next LoadAsync
        // to finish the promotion.
        try
        {
            await secrets.SetAsync(Scope, CommitKey, "1", cancellationToken).ConfigureAwait(false);
            bool promoted = await PromoteStagingAsync(cancellationToken).ConfigureAwait(false);
            await secrets.DeleteAsync(Scope, CommitKey, cancellationToken).ConfigureAwait(false);
            await CleanupStagingAsync(cancellationToken).ConfigureAwait(false);
            if (!promoted)
            {
                return false;
            }
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // Promotion was interrupted. The commit marker is still set, so
            // the next LoadAsync will finish it. Report failure so a caller
            // holding the plaintext cache keeps it for a retry.
            return false;
        }
        return true;
    }

    /// <summary>
    /// Moves a session left behind by an older build's plaintext
    /// <c>cline-session.json</c> into the secret store, then deletes it. Runs
    /// at most once per instance, and is a no-op once the files are gone.
    ///
    /// <para>
    /// Older builds wrote a sibling <c>.tmp</c> and renamed it over the real
    /// path, so a crash mid-write can leave either file — or only the
    /// <c>.tmp</c>, when the real path had never been written — holding a
    /// complete token pair. Both are candidates, and both have to end up gone.
    /// </para>
    ///
    /// <para>
    /// A candidate that cannot be parsed is deleted rather than kept. It holds
    /// no session anyone can recover, but it does hold credential material in
    /// plaintext under the user's profile, and every later run reaches the same
    /// verdict — so keeping it only means keeping tokens at rest forever. That
    /// is the opposite trade from a vault that is merely unavailable, where the
    /// file is still the only copy of a usable session and is kept for retry.
    /// </para>
    /// </summary>
    private async Task MigrateLegacyCacheAsync(CancellationToken cancellationToken)
    {
        if (_legacyMigrationAttempted || legacyCachePath is null)
        {
            return;
        }
        _legacyMigrationAttempted = true;
        string tempPath = legacyCachePath + ".tmp";
        try
        {
            ClineSession? session = null;
            bool anyPresent = false;
            foreach (string path in new[] { legacyCachePath, tempPath })
            {
                if (!File.Exists(path))
                {
                    continue;
                }
                anyPresent = true;
                session ??= TryReadLegacyFile(path);
            }
            if (!anyPresent)
            {
                return;
            }
            if (session is not null && !await TrySaveAsync(session, cancellationToken).ConfigureAwait(false))
            {
                // The vault is unavailable right now; keep the plaintext copy
                // and retry on the next load instead of deleting the only
                // remaining copy of the tokens.
                _legacyMigrationAttempted = false;
                return;
            }
            File.Delete(legacyCachePath);
            File.Delete(tempPath);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _legacyMigrationAttempted = false;
            throw;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // A locked or unreadable file, or a vault fault. The migration is
            // idempotent — re-reading and re-saving a candidate that is still
            // there costs nothing — so let the next load retry it rather than
            // latching a half-finished cleanup.
            _legacyMigrationAttempted = false;
        }
    }

    private void CleanupLegacyFilesBestEffort()
    {
        if (_legacyMigrationAttempted || legacyCachePath is null)
        {
            return;
        }
        _legacyMigrationAttempted = true;
        try
        {
            File.Delete(legacyCachePath);
            File.Delete(legacyCachePath + ".tmp");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            _legacyMigrationAttempted = false;
        }
    }

    /// <summary>
    /// Reads a legacy candidate, returning null when the file is present but
    /// holds no usable session. Genuine I/O faults propagate so the caller can
    /// retry instead of deleting a file it never managed to read.
    /// </summary>
    private static ClineSession? TryReadLegacyFile(string path)
    {
        try
        {
            return ReadLegacyFile(path);
        }
        catch (JsonException)
        {
            // Truncated or corrupt. The old writer emitted accessToken and
            // refreshToken before expiresAt, so a truncated file can still
            // hold both tokens intact — which is exactly why it must not stay
            // on disk.
            return null;
        }
    }

    private async Task<bool> TryRecoverInterruptedPromotionAsync(CancellationToken cancellationToken)
    {
        try
        {
            bool promoted = await PromoteStagingAsync(cancellationToken).ConfigureAwait(false);
            if (!promoted)
            {
                return false;
            }
            await secrets.DeleteAsync(Scope, CommitKey, cancellationToken).ConfigureAwait(false);
            await CleanupStagingAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            // The vault is still unavailable; the next LoadAsync will retry.
            return false;
        }
    }

    private async Task<bool> PromoteStagingAsync(CancellationToken cancellationToken)
    {
        string? stagingAccess = await secrets
            .GetAsync(Scope, StagingAccessTokenKey, cancellationToken)
            .ConfigureAwait(false);
        string? stagingExpires = await secrets
            .GetAsync(Scope, StagingExpiresAtKey, cancellationToken)
            .ConfigureAwait(false);
        string? stagingRefresh = await secrets
            .GetAsync(Scope, StagingRefreshTokenKey, cancellationToken)
            .ConfigureAwait(false);
        string? clearFlag = await secrets
            .GetAsync(Scope, StagingClearRefreshKey, cancellationToken)
            .ConfigureAwait(false);

        if (
            stagingAccess is null
            || stagingExpires is null
            || clearFlag is not ("0" or "1")
            || (clearFlag == "0" && stagingRefresh is null)
        )
        {
            return false;
        }

        await secrets.SetAsync(Scope, AccessTokenKey, stagingAccess, cancellationToken).ConfigureAwait(false);
        await secrets.SetAsync(Scope, ExpiresAtKey, stagingExpires, cancellationToken).ConfigureAwait(false);
        if (clearFlag == "1")
        {
            await secrets.DeleteAsync(Scope, RefreshTokenKey, cancellationToken).ConfigureAwait(false);
        }
        else if (stagingRefresh is not null)
        {
            await secrets.SetAsync(Scope, RefreshTokenKey, stagingRefresh, cancellationToken).ConfigureAwait(false);
        }
        return true;
    }

    private async Task CleanupStagingAsync(CancellationToken cancellationToken)
    {
        await TryDeleteAsync(StagingAccessTokenKey, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(StagingExpiresAtKey, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(StagingRefreshTokenKey, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(StagingClearRefreshKey, cancellationToken).ConfigureAwait(false);
    }

    private async Task TryDeleteAsync(string key, CancellationToken cancellationToken)
    {
        try
        {
            await secrets.DeleteAsync(Scope, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsRecoverable(ex)) { }
    }

    private static ClineSession? ReadLegacyFile(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;
        if (
            root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("accessToken", out JsonElement token)
            || token.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(token.GetString())
            || !root.TryGetProperty("expiresAt", out JsonElement expires)
            || expires.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                expires.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTimeOffset expiresAt
            )
        )
        {
            return null;
        }
        string? refreshToken =
            root.TryGetProperty("refreshToken", out JsonElement refresh)
            && refresh.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(refresh.GetString())
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
        ex
            is IOException
                or UnauthorizedAccessException
                or JsonException
                or ArgumentOutOfRangeException
                or System.ComponentModel.Win32Exception;
}
