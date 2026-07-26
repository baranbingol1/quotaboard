# Provider release checklist

Run before every release, using the local CLI sessions on the test machine. This
is a manual step: real-provider credentials are never exercised in CI. Record
only pass/fail, the app version, the provider/CLI version, the date, and
sanitized notes. **Never commit credentials or raw provider responses.**

For each provider below, verify:

1. The account is detected correctly (right login/email, right auth source).
2. Quota refresh succeeds and meter values match the provider's own UI within
   expected timing.
3. Reset times are correct.
4. Going offline preserves the last-known cached data (card shows a stale/offline
   state rather than disappearing).
5. Signing out is represented correctly (sign-in-required state, no stale "fresh").
6. Other providers keep working while this one fails.
7. Diagnostics contain no credentials, tokens, or cookies.

## Providers

- [ ] **Codex** — CLI OAuth session; usage window meters.
- [ ] **Claude Code** — CLI OAuth session; 5-hour and weekly meters; exact local JSONL tokens.
- [ ] **Factory (Droid)** — existing Droid CLI session (no API key needed); billing/usage meters; local Droid log tokens.
- [ ] **GitHub Copilot** — device authorization; quota snapshots.
- [ ] **Amp** — Amp CLI / access token; thread tokens + credit balances.
- [ ] **Google Antigravity** — existing agy session; subscription quota.
- [ ] **OpenCode** — local history attributed per authorizing provider; no Go/Zen plan inferred.
- [ ] **Cursor** — local app session; quota limits **and** usage-event token ingestion. Additionally confirm the Usage page totals for Cursor are within expectation of the Cursor dashboard, and that the session cookie never appears in diagnostics (redaction canary).

## Runtime acceptance

- [ ] Windows 10 22H2 x64 — manual install / start / uninstall.
- [ ] Windows 11 x64 — manual install / start / uninstall.
- [ ] Windows 11 ARM64 — manual install / start / uninstall (when hardware is available).
