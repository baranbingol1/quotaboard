// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using AiLimits.Application.Abstractions;

namespace AiLimits.Infrastructure.Providers.Cline;

/// <summary>A ClinePass session the app refreshed itself.</summary>
internal sealed record ClineSession(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset ExpiresAt,
    string? AccountFingerprint
);

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
    private const string AccountFingerprintKey = "session.account-fingerprint";

    // Staging keys: written first, promoted to the live keys above only after
    // every field succeeds. LoadAsync never reads these.
    private const string StagingAccessTokenKey = "session.access-token.staging";
    private const string StagingExpiresAtKey = "session.expires-at.staging";
    private const string StagingRefreshTokenKey = "session.refresh-token.staging";
    private const string StagingAccountFingerprintKey = "session.account-fingerprint.staging";

    // "1" = the live refresh key should be deleted during promotion;
    // "0" = the staging refresh value should be copied to the live key.
    private const string StagingClearRefreshKey = "session.clear-refresh.staging";

    // Present only between "all staging writes succeeded" and "promotion to
    // live keys finished." Its presence tells LoadAsync to finish the
    // promotion before reading.
    private const string CommitKey = "session.commit";

    private bool _legacyCleanupAttempted;

    public async Task<ClineSession?> LoadAsync(string? expectedAccountFingerprint, CancellationToken cancellationToken)
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
                if (
                    expectedAccountFingerprint is null
                    || !string.Equals(live.AccountFingerprint, expectedAccountFingerprint, StringComparison.Ordinal)
                )
                {
                    await DeleteLiveSessionBestEffortAsync(cancellationToken).ConfigureAwait(false);
                    CleanupLegacyFilesBestEffort();
                    return null;
                }
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

        // Legacy plaintext has no trustworthy account identity. Delete it, but
        // never promote it into a reusable credential.
        CleanupLegacyFilesBestEffort();
        return null;
    }

    private async Task<ClineSession?> ReadLiveSessionAsync(CancellationToken cancellationToken)
    {
        string? accessToken = await secrets.GetAsync(Scope, AccessTokenKey, cancellationToken).ConfigureAwait(false);
        string? expires = await secrets.GetAsync(Scope, ExpiresAtKey, cancellationToken).ConfigureAwait(false);
        string? fingerprint = await secrets
            .GetAsync(Scope, AccountFingerprintKey, cancellationToken)
            .ConfigureAwait(false);
        if (
            string.IsNullOrWhiteSpace(fingerprint)
            && (!string.IsNullOrWhiteSpace(accessToken) || !string.IsNullOrWhiteSpace(expires))
        )
        {
            await DeleteLiveSessionBestEffortAsync(cancellationToken).ConfigureAwait(false);
            return null;
        }
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
        if (string.IsNullOrWhiteSpace(fingerprint))
        {
            return null;
        }
        return new ClineSession(
            accessToken,
            string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken,
            expiresAt,
            fingerprint
        );
    }

    public Task SaveAsync(ClineSession session, CancellationToken cancellationToken) =>
        TrySaveAsync(session, cancellationToken);

    /// <summary>
    /// Same write path as <see cref="SaveAsync"/>, but reports recoverable
    /// failures to the caller instead of swallowing them.
    /// </summary>
    public async Task<bool> TrySaveAsync(ClineSession session, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(session.AccountFingerprint))
        {
            return false;
        }
        // Phase 1: stage every field. If any write fails, clean up whatever
        // staging was written and leave the live keys untouched.
        try
        {
            await secrets
                .SetAsync(Scope, StagingAccessTokenKey, session.AccessToken, cancellationToken)
                .ConfigureAwait(false);
            await secrets
                .SetAsync(Scope, StagingAccountFingerprintKey, session.AccountFingerprint, cancellationToken)
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
            // the next LoadAsync will finish it.
            return false;
        }
        return true;
    }

    private void CleanupLegacyFilesBestEffort()
    {
        if (_legacyCleanupAttempted || legacyCachePath is null)
        {
            return;
        }
        _legacyCleanupAttempted = true;
        try
        {
            File.Delete(legacyCachePath);
            File.Delete(legacyCachePath + ".tmp");
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            _legacyCleanupAttempted = false;
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
        string? stagingFingerprint = await secrets
            .GetAsync(Scope, StagingAccountFingerprintKey, cancellationToken)
            .ConfigureAwait(false);
        string? clearFlag = await secrets
            .GetAsync(Scope, StagingClearRefreshKey, cancellationToken)
            .ConfigureAwait(false);

        if (
            stagingAccess is null
            || stagingExpires is null
            || string.IsNullOrWhiteSpace(stagingFingerprint)
            || clearFlag is not ("0" or "1")
            || (clearFlag == "0" && stagingRefresh is null)
        )
        {
            return false;
        }

        await secrets.SetAsync(Scope, AccessTokenKey, stagingAccess, cancellationToken).ConfigureAwait(false);
        await secrets.SetAsync(Scope, ExpiresAtKey, stagingExpires, cancellationToken).ConfigureAwait(false);
        await secrets
            .SetAsync(Scope, AccountFingerprintKey, stagingFingerprint, cancellationToken)
            .ConfigureAwait(false);
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
        await TryDeleteAsync(StagingAccountFingerprintKey, cancellationToken).ConfigureAwait(false);
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

    private async Task DeleteLiveSessionBestEffortAsync(CancellationToken cancellationToken)
    {
        await TryDeleteAsync(AccessTokenKey, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(ExpiresAtKey, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(RefreshTokenKey, cancellationToken).ConfigureAwait(false);
        await TryDeleteAsync(AccountFingerprintKey, cancellationToken).ConfigureAwait(false);
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
                or ArgumentOutOfRangeException
                or System.ComponentModel.Win32Exception;
}
