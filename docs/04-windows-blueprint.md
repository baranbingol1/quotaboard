# Windows application blueprint

## 1. Decision

Build a native C# desktop application using:

- .NET 10;
- WinUI 3 and the Windows App SDK stable channel;
- CommunityToolkit.Mvvm;
- MSIX packaging for normal distribution;
- SQLite for settings metadata, snapshots, and history;
- a small Win32 interop layer for the notification-area icon;
- WebView2 only for providers that require an authenticated web session.

WinUI 3 is Microsoft's native Windows desktop UI framework, supports C#, and runs as a desktop process. Current guidance supports Windows 10 version 1809 and later, with Windows 11 recommended ([WinUI overview](https://learn.microsoft.com/en-us/windows/apps/get-started/winui-get-started-overview)). The Windows App SDK supplies lifecycle, windowing, notifications, deployment, and modern Windows APIs independently of OS servicing ([Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/)).

CommunityToolkit.Mvvm is Microsoft-maintained, UI-framework agnostic, modular, and supplies observable models and asynchronous commands without imposing a large application framework ([MVVM Toolkit](https://learn.microsoft.com/en-us/dotnet/communitytoolkit/mvvm/)).

### Why not copy the Swift app

The Swift core contains useful protocol knowledge, but a Windows-native implementation avoids:

- bridging Swift concurrency into .NET UI;
- replacing AppKit/WebKit/Keychain behind Swift-only abstractions;
- an unfamiliar deployment and debugging toolchain for a Windows application;
- losing first-class WinUI, WebView2, Windows notifications, and accessibility.

Port behavior and tests. Do not mechanically translate files.

## 2. Target architecture

~~~mermaid
flowchart TB
    subgraph Presentation["AiLimits.Presentation.WinUI"]
        Pages["Overview · Providers · History · Diagnostics · Settings"]
        VM["MVVM view models"]
        Tray["Tray host + compact flyout"]
    end

    subgraph Application["AiLimits.Application"]
        Refresh["RefreshCoordinator"]
        Pipeline["FetchPipeline"]
        Registry["ProviderRegistry"]
        Notify["ThresholdEvaluator"]
    end

    subgraph Domain["AiLimits.Domain"]
        Models["UsageSnapshot · RateWindow · Credits · Identity"]
        Contracts["ProviderDescriptor · FetchStrategy · FetchOutcome"]
    end

    subgraph Infrastructure["AiLimits.Infrastructure"]
        Http["Hardened HTTP"]
        Codex["Codex OAuth + app-server"]
        Claude["Claude strategies"]
        Local["Local file/history probes"]
        DB["SQLite repositories"]
    end

    subgraph Platform["AiLimits.Platform.Windows"]
        Secrets["Credential/DPAPI adapter"]
        Web["WebView2 session profiles"]
        Shell["Shell_NotifyIcon"]
        Power["Power + session signals"]
    end

    Pages --> VM
    Tray --> VM
    VM --> Refresh
    Refresh --> Registry
    Registry --> Pipeline
    Pipeline --> Contracts
    Contracts --> Models
    Pipeline --> Http
    Pipeline --> Codex
    Pipeline --> Claude
    Pipeline --> Local
    Refresh --> DB
    Infrastructure --> Platform
~~~

### Solution layout

    src/
      AiLimits.Domain/
        Usage/
        Providers/
      AiLimits.Application/
        Refresh/
        Notifications/
        Diagnostics/
      AiLimits.Infrastructure/
        Http/
        Persistence/
        Providers/
          Codex/
          Claude/
      AiLimits.Platform.Windows/
        Credentials/
        Processes/
        WebView/
        Tray/
        Power/
      AiLimits.Presentation.WinUI/
        Views/
        ViewModels/
        Controls/
      AiLimits.App/
    tests/
      AiLimits.Domain.Tests/
      AiLimits.Application.Tests/
      AiLimits.Infrastructure.Tests/
      AiLimits.WinUI.Tests/

Keep <code>Domain</code> free of WinUI, process, filesystem, HTTP, SQLite, and WebView types. Keep provider parsers in Infrastructure but return only Domain contracts.

## 3. Core contracts

The minimum contracts keep the seams that matter — descriptor, ordered strategies, normalized result — while keeping provider-specific fields out of the common model.

~~~csharp
public enum FetchKind { OAuth, ApiToken, Cli, LocalProbe, WebApi, WebDashboard }
public enum SourceMode { Auto, OAuth, Api, Cli, Local, Web }
public enum DataConfidence { Exact, Estimated, PercentOnly, Unknown }

public sealed record RateWindow(
    double UsedPercent,
    TimeSpan? Duration,
    DateTimeOffset? ResetsAt,
    string? ResetDescription = null,
    bool IsSyntheticPlaceholder = false);

public sealed record UsageSnapshot(
    string ProviderId,
    RateWindow? Primary,
    RateWindow? Secondary,
    RateWindow? Tertiary,
    IReadOnlyList<NamedRateWindow> ExtraWindows,
    CreditSnapshot? Credits,
    ProviderIdentity? Identity,
    DataConfidence Confidence,
    DateTimeOffset UpdatedAt,
    IReadOnlyDictionary<string, JsonElement> Extensions);

public interface IFetchStrategy
{
    string Id { get; }
    FetchKind Kind { get; }
    Task<bool> IsAvailableAsync(FetchContext context, CancellationToken ct);
    Task<FetchResult> FetchAsync(FetchContext context, CancellationToken ct);
    bool ShouldFallback(Exception error, FetchContext context);
}
~~~

Add:

- <code>ProviderDescriptor</code>: ID, display metadata, supported source modes, strategy resolver;
- <code>FetchAttempt</code>: strategy, kind, availability, sanitized error;
- <code>FetchOutcome</code>: result or typed error plus attempts;
- <code>SnapshotState</code>: fresh/stale/unavailable/invalidated;
- <code>AccountAuthority</code>: expected and observed account identity;
- <code>ProviderError</code> hierarchy: unavailable, unauthorized, forbidden, contract, transient network, server, account mismatch, cancelled.

Do not express fallback as a single boolean on the provider. It belongs to each strategy/error combination.

## 4. Refresh coordinator

Implement one application service with:

- a global refresh cancellation token;
- one state slot and monotonic generation per provider/account;
- coalescing for equivalent requests;
- replacement/cancellation when settings or account change;
- a bounded concurrency semaphore, initially four providers;
- publication only when generation, account, source mode, and config revision still match;
- persistence and notification evaluation only after publication;
- jittered retry for startup/transient failures;
- manual and adaptive schedules.

Adaptive mode should schedule a one-shot delay, recompute after each tick, and react to user interaction and Windows power/session signals.

Start from the adaptive interval ladder in `AdaptiveRefreshPolicy`. Add battery-saver/session-lock signals later. Never run browser/WebView acquisition while the user session is locked unless explicitly required.

## 5. Codex provider implementation

### Automatic strategy order

1. <code>CodexOAuthStrategy</code>;
2. <code>CodexAppServerStrategy</code>.

Keep <code>CodexWebStrategy</code> an explicit, opt-in mode rather than part of the automatic ladder: a web-dashboard read is the most fragile source and must never silently stand in for OAuth.

### OAuth strategy

- Resolve <code>CODEX_HOME</code>; otherwise use <code>%USERPROFILE%\.codex</code>.
- Read <code>auth.json</code> without persisting it elsewhere.
- Preserve unknown fields if writing refreshed credentials.
- Resolve the usage URL from <code>config.toml</code> using an allowlisted URL policy.
- send the bearer token and optional <code>ChatGPT-Account-Id</code>;
- parse primary, secondary, credits, plan, spend control, and lossy additional limits;
- map 300 minutes to session and 10,080 minutes to weekly;
- derive account email/plan from the ID token only after validating JWT structure; do not treat decoded JWT claims as signature-authenticated authorization.

### CLI strategy

Start <code>codex.exe</code> through a direct <code>ProcessStartInfo</code> argument list:

    -s
    read-only
    -a
    untrusted
    app-server

Use redirected stdin/stdout/stderr, no shell, no visible window, UTF-8 line-delimited JSON, and a job object so the child process dies with the app. Serialize JSON-RPC reads, enforce initialization/request timeouts, discard unrelated notifications, and cap stderr diagnostic retention.

Request <code>account/rateLimits/read</code> followed by <code>account/read</code>. Return partial usage/credits when one is present. Never parse terminal-colored human text if the app-server protocol is available.

### Codex acceptance cases

- OAuth success prevents CLI launch.
- Missing/expired/revoked OAuth may use CLI in automatic mode.
- Explicit OAuth never changes sources.
- OAuth contract/server/network failures are visible unless a deliberately documented policy changes this.
- Weekly-only and session-only payloads render in the correct lane.
- Unknown plan strings display safely.
- One malformed additional limit does not erase siblings or primary limits.
- account switch during refresh discards the old result.
- credits from CLI never attach to OAuth data without matching identity.

## 6. GUI information architecture

Use an adaptive left <code>NavigationView</code>. Microsoft recommends it for top-level navigation, multiple categories, smaller-window adaptation, search, and an integrated settings entry ([NavigationView](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/navigationview)).

### Pages

| Page | Purpose |
|---|---|
| Overview | All enabled providers, most urgent limits first, next reset, credits, last refresh, global refresh |
| Providers | Searchable list; enable/connect/configure; source mode and account per provider |
| Provider detail | Full windows, history chart, credits, source attempts, account, refresh, disconnect |
| History | Cross-provider and provider-specific usage/reset history |
| Notifications | Threshold, reset-soon, stale-data, and failure rules |
| Diagnostics | Redacted strategy attempts, versions, scheduling, export |
| Settings | Startup, appearance, refresh cadence, privacy, data retention, updates |

### Overview card

Each provider card should show:

- icon and provider/account;
- primary and secondary progress bars with explicit duration labels;
- used/remaining toggle;
- reset time in relative and absolute tooltip form;
- extra model windows collapsed behind “more”;
- credits when available;
- fresh/stale/error badge;
- source label and last updated time;
- refresh and open-detail actions.

Avoid relying only on color. Provide text status and accessible names. Progress bars should announce provider, window duration, used percentage, and reset time.

### Tray behavior

The main UI remains a normal window. A user-enabled notification-area icon provides:

- left click: show/activate main window or compact overview;
- right click: Refresh all, open app, pause refresh, quit;
- tooltip: most urgent enabled limit and data age.

Windows notification-area icons are added/updated/removed with <code>Shell_NotifyIcon</code>; Microsoft recommends a GUID identity, accessible version 4 behavior, DPI-aware 16×16 and 32×32 resources, and a normal shortcut menu ([notification-area guidance](https://learn.microsoft.com/en-us/windows/win32/shell/notification-area)).

The tray icon is enabled by default and can be disabled in Settings.

### Notifications

Use <code>AppNotificationManager</code> for threshold/reset/failure notifications. It is the recommended API for new WinUI 3/Windows App SDK applications ([Windows notifications](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/)).

Deduplicate notifications by provider, account, window, threshold, and reset cycle. Do not notify again until utilization crosses a new threshold or the window resets.

## 7. Browser-session strategy on Windows

Do not import browser profile cookies in the MVP. Instead:

1. open a provider-specific sign-in window using WebView2;
2. assign an app-owned user-data folder per provider/profile;
3. let the user authenticate visibly;
4. query authenticated JSON endpoints through the WebView session or retrieve only required cookies via WebView2 APIs;
5. verify signed-in account identity;
6. clear the profile on disconnect.

WebView2 stores cookies, permissions, and cached resources in its user-data folder, and Microsoft recommends specifying a writable custom location for many desktop scenarios ([WebView2 user data folders](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/user-data-folder)). This gives the app an explicit, supportable session boundary.

Controls:

- one profile folder per provider/account;
- no navigation to non-allowlisted origins in the auth window;
- block downloads, popups, and external protocols unless required;
- no JavaScript injection when an authenticated JSON endpoint is sufficient;
- clear all site data on disconnect;
- display that web-based integrations may break when providers change private APIs;
- feature-flag each web provider for emergency disablement.

## 8. Secrets and persistence

### Secrets

Create <code>ISecretStore</code> and keep the implementation replaceable.

For a small first release, Windows Credential Locker is usable from WinUI and desktop apps, but it is limited to 20 credentials per app and is intended for passwords rather than large blobs ([Credential Locker](https://learn.microsoft.com/en-us/windows/apps/develop/security/credential-locker)). That limit is too low for a mature 59-provider, multi-account product.

Recommended long-term split:

- provider API keys/refresh tokens: Windows Credential Manager generic credentials or a current-user DPAPI-protected secret store;
- nonsecret metadata: SQLite;
- WebView cookies/storage: WebView2 user-data folders, not duplicated into SQLite;
- Codex/Claude CLI-owned credentials: read from their owned location on demand.

Never store plaintext credentials in app settings. Keep secret IDs in SQLite, not secret values.

### SQLite

Suggested tables:

- <code>providers</code>: enabled, source mode, config revision;
- <code>accounts</code>: provider, stable local ID, display identity, selected flag;
- <code>snapshots</code>: normalized current result and freshness;
- <code>rate_windows</code>: lane/name/percent/duration/reset;
- <code>credit_snapshots</code>;
- <code>usage_history</code>;
- <code>fetch_attempts</code>: sanitized and retention-limited;
- <code>notification_rules</code> and <code>notification_state</code>;
- <code>schema_info</code>.

Use WAL mode, migrations, parameterized commands, UTC timestamps, and one writer service. Encrypt only fields that are actually secret; blanket database encryption does not replace correct credential storage.

## 9. Delivery plan

### Phase 0 — foundation

- solution/layer structure;
- domain contracts and parser fixtures;
- registry/pipeline/typed errors;
- HTTP hardening;
- SQLite migrations;
- WinUI shell and navigation;
- diagnostics/redaction.

Exit: fake provider renders, refreshes, persists, and reports attempts end-to-end.

### Phase 1 — Codex vertical slice

- auth discovery;
- OAuth request/parser/normalization;
- app-server JSON-RPC fallback;
- Overview and Codex detail pages;
- history and thresholds;
- tray and local notifications.

Exit: all Codex acceptance cases in section 5 pass without live credentials in CI.

### Phase 2 — provider expansion

- Claude OAuth/CLI;
- API-token provider template;
- account management;
- settings import/export without secrets;
- updater and signed MSIX pipeline.

### Phase 3 — web providers

- isolated WebView2 sign-in/profile manager;
- identity authority contract;
- one web provider as a tracer bullet;
- remote feature kill switch and contract monitoring.

### Phase 4 — scale and polish

- multi-account switching;
- bounded bulk refresh tuning;
- retention controls and backup;
- localization, high contrast, screen-reader and keyboard audit;
- optional Windows widget after the main experience is stable.

## 10. Definition of done for the first public build

- No provider acquisition runs on the UI thread.
- No secret appears in logs, diagnostics export, crash metadata, or SQLite.
- Every displayed snapshot identifies source, age, confidence, and account when known.
- Account/config switches cannot publish stale in-flight results.
- Automatic fallback is covered by a typed error matrix.
- Codex OAuth and CLI fixtures cover partial and future-compatible payloads.
- The app works without a tray icon.
- All pages are keyboard navigable and usable at 200% scaling/high contrast.
- Web integrations are optional and isolated.
- MSIX is signed; installer/update rollback is tested.
