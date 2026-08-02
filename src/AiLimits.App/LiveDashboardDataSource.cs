// SPDX-License-Identifier: Apache-2.0
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Diagnostics;
using AiLimits.Application.Discovery;
using AiLimits.Application.Preferences;
using AiLimits.Application.Presentation;
using AiLimits.Application.Pricing;
using AiLimits.Application.Refresh;
using AiLimits.Application.Snapshots;
using AiLimits.Application.Usage;
using AiLimits.Domain;
using AiLimits.Infrastructure.Persistence;
using AiLimits.Infrastructure.Pricing;
using AiLimits.Infrastructure.Providers;
using AiLimits.Infrastructure.Providers.Antigravity;
using AiLimits.Infrastructure.Providers.Claude;
using AiLimits.Infrastructure.Providers.Cline;
using AiLimits.Infrastructure.Providers.Codex;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Infrastructure.Providers.Copilot;
using AiLimits.Infrastructure.Providers.Cursor;
using AiLimits.Infrastructure.Providers.Droid;
using AiLimits.Infrastructure.Providers.Amp;
using AiLimits.Infrastructure.Providers.OpenCode;
using AiLimits.Infrastructure.Providers.Statuspage;
using AiLimits.Platform.Windows.Security;
using AiLimits.Presentation.WinUI;
using AiLimits.Presentation.WinUI.Localization;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLimits.App;

internal sealed class LiveDashboardDataSource : IDashboardDataSource, IDisposable
{
    private sealed record PricedModelRow(ModelUsageRowViewModel ViewModel, decimal? CostUsd, long RawTokens, long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, long ReasoningTokens, decimal CacheSavedUsd = 0m);

    private static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1L);

    /// <summary>
    /// How many telemetry sources are drained at once. They are I/O bound
    /// (subprocesses, log files, SQLite reads), so this is about overlapping
    /// waits, not CPU; it matches the provider fetch batch.
    /// </summary>
    private const int MaxScanConcurrency = 4;

    private const int ScanBatchSize = 512;

    private readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(25L)
    };

    // Cursor's app-session cookie must never follow a redirect to another host.
    // Keep this client isolated from the shared provider client and its cookie jar.
    private readonly HttpClient _cursorHttpClient = new HttpClient(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(25L)
    };

    private readonly SystemClock _clock = new SystemClock();

    private readonly SqliteDatabase _database;

    private readonly SqliteAccountRepository _accounts;

    private readonly SqliteSnapshotRepository _snapshots;

    private readonly SqliteUsageAggregateRepository _usage;

    private readonly ProviderStatusClient _providerStatusClient;

    private readonly QuotaAlertMonitor _quotaAlerts;

    // TTL-capped: a dead statuspage feed cannot leave a banner up forever.
    private readonly ProviderStatusCache _providerStatuses;

    private readonly IReadOnlyList<IProviderAdapter> _adapters;

    private readonly IReadOnlyDictionary<ProviderId, IProviderAdapter> _adapterById;

    private readonly AccountDiscoveryService _discovery;

    private readonly RefreshCoordinator _refresh;

    private readonly ModelsDevPricingCatalog _catalog;

    private readonly ExplicitModelResolver _modelResolver = new ExplicitModelResolver(DefaultModelAliases.All);
    private readonly CatalogModelResolver _catalogModelResolver;


    private readonly PricingEngine _pricing = new PricingEngine();

    // User-entered per-1M pricing for models the catalog cannot price; reloaded
    // each projection so Settings edits take effect on the next paint.
    private ManualPriceOverrideSet _pricingOverrides = ManualPriceOverrideSet.Empty;

    private readonly SemaphoreSlim _loadGate = new SemaphoreSlim(1, 1);

    private readonly SemaphoreSlim _usageWriteGate = new SemaphoreSlim(1, 1);

    private readonly string _databasePath;

    private bool _databaseInitialized;

    private DateTimeOffset? _lastScanAt;

    private static readonly TimeSpan ResetCreditsTtl = TimeSpan.FromMinutes(15L);

    private readonly Dictionary<AccountKey, ResetCreditInventory?> _resetCredits = new Dictionary<AccountKey, ResetCreditInventory?>();

    private DateTimeOffset? _resetCreditsFetchedAt;

    private bool _scanInFlight;
    private int _scannerCount;

    private int _successfulScannerCount;

    private string _scannerDetail = L("Data_ScannerNotRun");

    private volatile bool _lastRefreshHadTransientFailure;

    public bool LastRefreshHadTransientFailure => _lastRefreshHadTransientFailure;

    private TimeSpan? _lastRetryAfterHint;

    /// <summary>Largest Retry-After any provider reported during the last refresh, if any.</summary>
    public TimeSpan? LastRetryAfterHint => _lastRetryAfterHint;

    public LiveDashboardDataSource()
    {
        string text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuotaBoard");
        Directory.CreateDirectory(text);
        _databasePath = Path.Combine(text, "ai-limits.db");
        _database = new SqliteDatabase(_databasePath);
        _accounts = new SqliteAccountRepository(_database);
        _snapshots = new SqliteSnapshotRepository(_database);
        _usage = new SqliteUsageAggregateRepository(_database);
        _providerStatusClient = new ProviderStatusClient(_httpClient);
        _providerStatuses = new ProviderStatusCache(_clock);
        _quotaAlerts = new QuotaAlertMonitor(_database);
        ProcessRunner processRunner = new ProcessRunner();
        OpenCodePathDiscovery pathDiscovery = new OpenCodePathDiscovery(processRunner);
        _catalogModelResolver = new CatalogModelResolver(_modelResolver);
        _adapters = new IProviderAdapter[]
        {
            new CodexProviderAdapter(_httpClient, _clock),
            new ClaudeProviderAdapter(_httpClient, _clock, processRunner: processRunner),
            new OpenCodeProviderAdapter(pathDiscovery),
            new DroidProviderAdapter(_httpClient, _clock),
            new AmpProviderAdapter(_httpClient, _clock, processRunner),
            new CopilotProviderAdapter(_httpClient, _clock),
            new AgyProviderAdapter(_clock),
            new CursorProviderAdapter(_cursorHttpClient, _clock, logger: NullLogger<CursorProviderAdapter>.Instance),
            new ClineProviderAdapter(_httpClient, _clock,
                secrets: new WindowsCredentialSecretStore(),
                legacySessionCachePath: Path.Combine(text, "cline-session.json"))
        };
        _adapterById = _adapters.ToDictionary((IProviderAdapter adapter) => adapter.Descriptor.Id);
        _discovery = new AccountDiscoveryService(_adapters, _accounts, NullLogger<AccountDiscoveryService>.Instance);
        _refresh = new RefreshCoordinator(_adapters, _accounts, _snapshots, new SnapshotMerger(), _clock, NullLogger<RefreshCoordinator>.Instance);
        _catalog = new ModelsDevPricingCatalog(_httpClient, Path.Combine(text, "pricing"), _clock);
    }

    public async Task<DashboardData> LoadAsync(
        bool forceRefresh,
        IProgress<RefreshProgress>? progress,
        IProgress<DashboardData>? interim,
        CancellationToken cancellationToken)
    {
        await _loadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await EnsureDatabaseAsync(cancellationToken).ConfigureAwait(false);
            IReadOnlyList<ProviderAccount> accounts;
            PricingCatalogSnapshot? catalog;
            if (forceRefresh)
            {
                _lastRefreshHadTransientFailure = false;
                progress?.Report(new RefreshProgress(0, 0, string.Empty, RefreshStage.DiscoveringAccounts));
                accounts = await DiscoverAccountsAsync(cancellationToken).ConfigureAwait(false);
                // The history scan and the limit fetch share nothing but this
                // account list: one reads local logs off disk, the other calls
                // provider APIs. Awaiting the scan first made every number on
                // screen wait behind it, and a first run over a large history
                // takes minutes — so the plan limits, which are two seconds of
                // network, did not appear until the disk work was done.
                // The four parallel tasks share a linked token. If any of them
                // fails, the finally below cancels the siblings and observes
                // their exceptions before the failure propagates: no work
                // outlives the refresh (or runs into disposed services during
                // shutdown) and nothing faults unobserved.
                using CancellationTokenSource refreshCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                CancellationToken refreshToken = refreshCts.Token;
                Task scanTask = ScanTelemetryIfDueAsync(accounts, progress, refreshToken);
                Task limitsTask = RefreshLimitsAsync(accounts, progress, refreshToken);
                Task<PricingCatalogSnapshot?> catalogTask = _catalog.RefreshIfStaleAsync(refreshToken);
                Task<IReadOnlyDictionary<string, ProviderServiceStatus>> providerStatusTask =
                    _providerStatusClient.PollAsync(refreshToken);
                try
                {
                    await limitsTask.ConfigureAwait(false);
                    // Fetching finished first on a cold start. Relabel the bar so it
                    // stops claiming "8 / 8 providers" while the scan still runs,
                    // and put the limits on screen now instead of holding them
                    // hostage to the history scan.
                    if (!scanTask.IsCompleted)
                    {
                        progress?.Report(new RefreshProgress(0, 0, string.Empty, RefreshStage.ScanningHistory));
                        if (interim is not null)
                        {
                            interim.Report(await ProjectAsync(
                                await _accounts.ListAsync(refreshToken).ConfigureAwait(false),
                                await _catalog.GetCurrentAsync(refreshToken).ConfigureAwait(false),
                                fromCache: false,
                                refreshToken).ConfigureAwait(false));
                        }
                    }
                    await scanTask.ConfigureAwait(false);
                    catalog = await catalogTask.ConfigureAwait(false);
                    IReadOnlyDictionary<string, ProviderServiceStatus> statuses =
                        await providerStatusTask.ConfigureAwait(false);
                    _providerStatuses.Merge(statuses);
                }
                finally
                {
                    // Stop whatever is still running, then wait for all four and
                    // swallow here: the failure (if any) is already propagating
                    // from the try block.
                    refreshCts.Cancel();
                    try
                    {
                        await Task.WhenAll(limitsTask, scanTask, catalogTask, providerStatusTask).ConfigureAwait(false);
                    }
                    catch
                    {
                    }
                }
                accounts = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
                await RefreshResetCreditsIfDueAsync(accounts, forceRefresh, cancellationToken).ConfigureAwait(false);
                await PruneStorageOncePerRunAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Cached pass: paint whatever the previous run left in the
                // local database without touching adapters, telemetry files,
                // or the network. Keeping this path pure-local is what lets
                // the window show last-known data the instant it opens.
                accounts = await _accounts.ListAsync(cancellationToken).ConfigureAwait(false);
                catalog = await _catalog.GetCurrentAsync(cancellationToken).ConfigureAwait(false);
            }
            return await ProjectAsync(accounts, catalog, !forceRefresh, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // Belt and braces: a cancelled scan leaves the flag set otherwise,
            // and a stale "still indexing" caption would outlive the scan.
            _scanInFlight = false;
            _loadGate.Release();
        }
    }

    public async Task<ModelCatalogStatus> GetModelCatalogStatusAsync(CancellationToken cancellationToken)
    {
        PricingCatalogSnapshot? snapshot = await _catalog.GetCurrentAsync(cancellationToken)
            .ConfigureAwait(false);
        return Describe(snapshot);
    }

    public async Task<PricingCatalogRefresh> RefreshModelCatalogAsync(CancellationToken cancellationToken) =>
        await _catalog.RefreshAsync(force: true, cancellationToken).ConfigureAwait(false);

    private ModelCatalogStatus Describe(PricingCatalogSnapshot? snapshot) =>
        snapshot is null
            ? ModelCatalogStatus.Unavailable(_catalog.LastError, _catalog.LastAttemptAt)
            : new ModelCatalogStatus(
                true,
                snapshot.ExactIndex.Count,
                snapshot.Hash,
                snapshot.FetchedAt,
                PricingCatalogSchedule.NextDue(snapshot.FetchedAt.ToLocalTime()),
                _catalog.LastAttemptAt,
                _catalog.LastError);

    private async Task EnsureDatabaseAsync(CancellationToken cancellationToken)
    {
        if (!_databaseInitialized)
        {
            await _database.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _databaseInitialized = true;
        }
    }

    private bool _prunedThisRun;

    // Age out append-only bookkeeping (old snapshots, fetch attempts,
    // fingerprints, alert cycles) once per app run, after the first forced
    // refresh so it never delays the startup paint.
    private async Task PruneStorageOncePerRunAsync(CancellationToken cancellationToken)
    {
        if (_prunedThisRun)
        {
            return;
        }
        _prunedThisRun = true;
        try
        {
            await new SqliteRetention(_database).PruneAsync(_clock.UtcNow, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Retention is best-effort housekeeping; it must never fail a refresh.
        }
    }

    private Task<IReadOnlyList<ProviderAccount>> DiscoverAccountsAsync(CancellationToken cancellationToken)
    {
        return _discovery.DiscoverAsync(cancellationToken);
    }

    private async Task RefreshLimitsAsync(IReadOnlyList<ProviderAccount> accounts, IProgress<RefreshProgress>? progress, CancellationToken cancellationToken)
    {
        // Only providers with a known adapter contribute to the parallel batch;
        // the reported total mirrors that selection so the UI does not show a
        // bar stuck below 100% when one of the accounts is unknown.
        ProviderAccount[] refreshable = accounts.Where((ProviderAccount account) => _adapterById.ContainsKey(account.Key.Provider)).ToArray();
        int total = refreshable.Length;
        int completed = 0;
        progress?.Report(new RefreshProgress(0, total, string.Empty));
        Task<RefreshPublication>[] tasks = refreshable
            .Select(async (ProviderAccount account) =>
            {
                progress?.Report(new RefreshProgress(Volatile.Read(ref completed), total, account.DisplayName));
                RefreshPublication publication = await RefreshAccountAsync(account, cancellationToken).ConfigureAwait(false);
                int done = Interlocked.Increment(ref completed);
                progress?.Report(new RefreshProgress(done, total, string.Empty));
                return publication;
            })
            .ToArray();
        RefreshPublication[] publications = await Task.WhenAll(tasks).ConfigureAwait(false);
        _lastRefreshHadTransientFailure = publications.Any(AdaptiveRefreshPolicy.IsTransientFailure);
        _lastRetryAfterHint = publications
            .Select(publication => publication.RetryAfterHint)
            .Where(hint => hint.HasValue)
            .DefaultIfEmpty(null)
            .Max();
        progress?.Report(new RefreshProgress(total, total, string.Empty));
    }

    private async Task<RefreshPublication> RefreshAccountAsync(ProviderAccount account, CancellationToken cancellationToken)
    {
        RefreshPublication refreshPublication = await _refresh.RefreshAsync(new RefreshRequest(account.Key, account.ConfigurationRevision, Force: true, "manual"), cancellationToken).ConfigureAwait(false);
        if (refreshPublication.Status == RefreshPublicationStatus.Published)
        {
            await _accounts.UpsertAsync(account with
            {
                IsConnected = true,
                LastSuccessfulRefreshAt = (refreshPublication.Snapshot?.ObservedAt ?? _clock.UtcNow)
            }, cancellationToken).ConfigureAwait(false);
        }
        return refreshPublication;
    }

    private async Task ScanTelemetryIfDueAsync(IReadOnlyList<ProviderAccount> accounts, IProgress<RefreshProgress>? progress, CancellationToken cancellationToken)
    {
        DateTimeOffset? lastScanAt = _lastScanAt;
        if (lastScanAt.HasValue)
        {
            DateTimeOffset valueOrDefault = lastScanAt.GetValueOrDefault();
            if (_clock.UtcNow - valueOrDefault < ScanInterval)
            {
                return;
            }
        }
        progress?.Report(new RefreshProgress(0, 0, string.Empty, RefreshStage.ScanningHistory));
        // Read by ProjectAsync: a projection taken while this runs has real
        // limits but incomplete usage totals, and has to say so.
        _scanInFlight = true;
        // Token sources only read local telemetry, so keep scanning known
        // accounts even when their live credentials are gone (a CLI logout
        // must not make recorded history vanish).
        (ProviderAccount Account, ITokenUsageSource Source)[] work = accounts
            .SelectMany(account => _adapterById.TryGetValue(account.Key.Provider, out IProviderAdapter adapter)
                ? adapter.CreateTokenSources(account).Select(source => (Account: account, Source: source))
                : [])
            .ToArray();

        int total = work.Length;
        int successful = 0;
        int emitted = 0;
        string? firstFailure = null;
        object failureGate = new object();

        // The sources share nothing: each reads its own logs, database or CLI
        // and writes through the serialised gate below. Running them one after
        // another meant the slowest (Amp, minutes of subprocess launches) held
        // up six others that together take seconds.
        await Parallel.ForEachAsync(
            work,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxScanConcurrency,
                CancellationToken = cancellationToken,
            },
            async (item, token) =>
            {
                try
                {
                    int scanned = await ScanSourceAsync(item.Account, item.Source, token).ConfigureAwait(false);
                    Interlocked.Add(ref emitted, scanned);
                    Interlocked.Increment(ref successful);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // A scan failure used to vanish here, so a source that
                    // returned nothing because it got a 401 was indistinguishable
                    // from one that simply had no new events. Keep scanning the
                    // remaining sources, but remember that this one did not
                    // succeed and say so in diagnostics.
                    string message = DiagnosticRedactor.Redact(
                        ex is TokenScanException ? ex.Message : ex.GetType().Name);
                    lock (failureGate)
                    {
                        firstFailure ??= message;
                    }
                }
            }).ConfigureAwait(false);

        _scanInFlight = false;
        _lastScanAt = _clock.UtcNow;
        _scannerCount = total;
        _successfulScannerCount = successful;
        _scannerDetail = total == 0
            ? L("Data_NoTelemetrySource")
            : firstFailure is not null
                ? F("Data_ScannerFailed", total - successful, total, firstFailure)
                : F("Data_EventsInspected", emitted);
    }

    /// <summary>
    /// Drains one telemetry source into the usage store and advances its cursor.
    /// Returns the number of events it emitted.
    /// </summary>
    private async Task<int> ScanSourceAsync(ProviderAccount account, ITokenUsageSource source, CancellationToken cancellationToken)
    {
        ScannerCursor? cursor = await _usage.GetCursorAsync(account.Key, source.Id, cancellationToken).ConfigureAwait(false);
        List<TokenUsageEvent> batch = new List<TokenUsageEvent>(ScanBatchSize);
        // Only events that reached the database may advance the stored cursor;
        // `pending` runs ahead of `committed` for whatever is still in the batch,
        // so a mid-stream failure cannot skip past events that were never saved.
        DateTimeOffset? committed = cursor?.LastObservedAt;
        DateTimeOffset? pending = committed;
        int emitted = 0;

        // A source that tracks its own per-item state hands it back here; a null
        // means "nothing to record", which must preserve what is already stored
        // rather than erase it. Everything else keeps the historical marker.
        string? SourcePosition() => source is IScanPositionSource stateful
            ? stateful.Position ?? cursor?.Position
            : "incremental";

        // What the scan started from, for the failure path below.
        string? StartingPosition() => source is IScanPositionSource ? cursor?.Position : "incremental";

        ScannerCursor Checkpoint(string? position) => new ScannerCursor(source.Id, position, committed, null);

        try
        {
            await foreach (TokenUsageEvent item in source.ReadAsync(account, cursor, cancellationToken).ConfigureAwait(false))
            {
                batch.Add(item);
                emitted++;
                if (!pending.HasValue || item.OccurredAt > pending.Value)
                {
                    pending = item.OccurredAt;
                }
                if (batch.Count >= ScanBatchSize)
                {
                    await WriteEventsAsync(batch, cancellationToken).ConfigureAwait(false);
                    batch.Clear();
                    committed = pending;
                }
            }
            if (batch.Count > 0)
            {
                await WriteEventsAsync(batch, cancellationToken).ConfigureAwait(false);
            }
            committed = pending;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Keep the timestamp the writes actually reached, but NOT the
            // source's new position. That position asserts each of its items was
            // processed, and items it yielded may have died with the batch that
            // failed to write — a skipped item would then never be revisited,
            // because the position is consulted ahead of the timestamp window.
            // Only `committed` is known to be on disk. Repeating a scan is the
            // cheap mistake; silently dropping usage is not.
            try
            {
                await WriteCursorAsync(account.Key, Checkpoint(StartingPosition()), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception checkpointFailure) when (checkpointFailure is not OperationCanceledException)
            {
                // Bookkeeping must not replace the failure that actually matters.
            }
            throw;
        }

        await WriteCursorAsync(account.Key, Checkpoint(SourcePosition()), cancellationToken).ConfigureAwait(false);
        return emitted;
    }

    // SQLite takes one writer at a time. Reads scale fine under WAL, but the
    // parallel scan would otherwise have several 512-row insert transactions
    // racing for the write lock and burning the busy timeout.
    private async Task WriteEventsAsync(IReadOnlyList<TokenUsageEvent> batch, CancellationToken cancellationToken)
    {
        await _usageWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _usage.AddEventsAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _usageWriteGate.Release();
        }
    }

    private async Task WriteCursorAsync(AccountKey account, ScannerCursor cursor, CancellationToken cancellationToken)
    {
        await _usageWriteGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _usage.SaveCursorAsync(account, cursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _usageWriteGate.Release();
        }
    }

    private async Task<DashboardData> ProjectAsync(IReadOnlyList<ProviderAccount> accounts, PricingCatalogSnapshot? catalog, bool fromCache, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _clock.UtcNow;
        DateOnly today = DateOnly.FromDateTime(now.ToLocalTime().DateTime);
        _pricingOverrides = ModelPricingOverridePreference.LoadAll();
        // A 365-day custom window needs the immediately preceding 365 days for
        // period-over-period comparison.
        IReadOnlyList<DailyUsageAggregate> historyRows = await _usage.QueryAsync(today.AddDays(-729), today, null, cancellationToken).ConfigureAwait(false);
        DateOnly monthStart = today.AddDays(-29);
        IReadOnlyList<DailyUsageAggregate> rows = historyRows.Where((DailyUsageAggregate row) => row.Day >= monthStart).ToArray();
        Dictionary<AccountKey, Task<ProviderSnapshot>> snapshotTasks = accounts.ToDictionary((ProviderAccount account) => account.Key, (ProviderAccount account) => _snapshots.GetLatestAsync(account.Key, cancellationToken));
        await Task.WhenAll(snapshotTasks.Values).ConfigureAwait(false);
        Dictionary<AccountKey, ProviderSnapshot> latest = snapshotTasks.ToDictionary((KeyValuePair<AccountKey, Task<ProviderSnapshot>> pair) => pair.Key, (KeyValuePair<AccountKey, Task<ProviderSnapshot>> pair) => pair.Value.Result);
        if (!fromCache)
        {
            await _quotaAlerts.ProcessAsync(
                latest.Values.Where(snapshot => snapshot is not null),
                now,
                cancellationToken).ConfigureAwait(false);
        }
        long totalInput = rows.Sum((DailyUsageAggregate row) => row.InputTokens);
        long totalOutput = rows.Sum((DailyUsageAggregate row) => row.OutputTokens);
        long totalCacheRead = rows.Sum((DailyUsageAggregate row) => row.CacheReadTokens);
        long totalCacheWrite = rows.Sum((DailyUsageAggregate row) => row.CacheWriteTokens);
        long totalReasoning = rows.Sum((DailyUsageAggregate row) => row.ReasoningTokens);
        long totalTokens = ((IEnumerable<DailyUsageAggregate>)rows).Sum((Func<DailyUsageAggregate, long>)TotalTokens);
        // One cache for the whole projection: every aggregation below asks about
        // the same handful of (service, model) pairs over and over.
        ModelResolutionCache resolutions = new ModelResolutionCache(_modelResolver, _catalogModelResolver);
        IReadOnlyList<PricedModelRow> priced = BuildModelRows(rows, catalog, resolutions);
        IReadOnlyList<ProviderUsageViewModel> providerUsage = BuildProviderUsage(priced);
        IReadOnlyList<ProviderUsageViewModel> harnessUsage = BuildHarnessUsage(priced);
        decimal quotedCost = priced.Where((PricedModelRow item) => item.CostUsd.HasValue).Sum((PricedModelRow item) => item.CostUsd.GetValueOrDefault());
        bool hasPricedUsage = priced.Any((PricedModelRow item) => item.CostUsd.HasValue);
        decimal reportedCost = rows.Where((DailyUsageAggregate row) => row.ReportedServiceCostUsd.HasValue).Sum((DailyUsageAggregate row) => row.ReportedServiceCostUsd.GetValueOrDefault());
        IReadOnlyDictionary<AccountKey, FetchFailureKind> latestFailures = await _snapshots.ReadLatestFailureKindsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProviderCardViewModel> providerCards = BuildProviderCards(accounts, latest, rows, now, latestFailures);
        ResetHorizonItemViewModel[] resets = (from item in providerCards.SelectMany((ProviderCardViewModel provider) => from meter in provider.AllMeters
                where meter.ResetsAt.HasValue && meter.ResetsAt > now
                select new ResetHorizonItemViewModel(provider.Name, provider.Account, meter.DisplayName, Countdown(meter.ResetsAt.Value - now), meter.ResetsAt.Value.ToLocalTime().ToString("ddd HH:mm", CultureInfo.CurrentCulture), provider.Accent, meter.ResetsAt.Value))
            orderby item.ResetsAt
            select item).Take(8).ToArray();
        int exactProviders = rows.Select((DailyUsageAggregate row) => row.Account.Provider).Distinct().Count();
        IReadOnlyList<ProviderConnectionViewModel> connections = BuildConnections(accounts, latest, rows, now, latestFailures);
        IReadOnlyList<FetchAttemptViewModel> recentAttempts = await ReadRecentAttemptsAsync(cancellationToken).ConfigureAwait(false);
        IReadOnlyList<UsageDayViewModel> usageDays = BuildDailyRows(rows, today);
        IReadOnlyList<HeatmapCellViewModel> heatmapCells = BuildHeatmap(historyRows, today);
        IReadOnlyList<UsageHistoryDayViewModel> usageHistory = BuildUsageHistory(historyRows, catalog, resolutions);
        IReadOnlyList<UsageModelSliceViewModel> usageModelSlices = BuildUsageModelSlices(historyRows);
        IReadOnlyList<UsageAnalyticsRecord> usageAnalyticsRecords = BuildUsageAnalyticsRecords(historyRows, catalog, resolutions);
        IReadOnlyList<ProjectUsageSliceViewModel> projectUsageSlices = BuildProjectUsageSlices(historyRows, catalog, resolutions);
        string weekDeltaLabel = BuildWeekDelta(rows, today);
        decimal cacheSaved = priced.Sum((PricedModelRow item) => item.CacheSavedUsd);
        string cacheSavingsLabel = cacheSaved > 0m ? F("Data_CacheSavings", FormatUsd(cacheSaved)) : "";
        IReadOnlyDictionary<string, decimal> planCosts = PlanCostPreference.LoadAll();
        HashSet<string> connectedProviderIds = accounts
            .Where((ProviderAccount account) => account.IsConnected)
            .Select((ProviderAccount account) => account.Key.Provider.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        IReadOnlyDictionary<string, string> detectedPlans = DetectPlans(latest);
        // User-entered costs always win; a detected plan's standard price only
        // fills the gap when the user left that provider blank.
        Dictionary<string, decimal> effectivePlans = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (string providerId in connectedProviderIds)
        {
            if (planCosts.TryGetValue(providerId, out decimal userCost) && userCost > 0m)
            {
                effectivePlans[providerId] = userCost;
            }
            else if (detectedPlans.TryGetValue(providerId, out string? rawPlan))
            {
                double suggested = PlanInfo(providerId, rawPlan).SuggestedMonthlyUsd;
                if (!double.IsNaN(suggested))
                {
                    effectivePlans[providerId] = (decimal)suggested;
                }
            }
        }
        KeyValuePair<string, decimal>[] activePlans = effectivePlans.ToArray();
        decimal planTotal = activePlans.Sum((KeyValuePair<string, decimal> pair) => pair.Value);
        string planValueLabel;
        string planValueDetail;
        if (planTotal <= 0m)
        {
            planValueLabel = "-";
            planValueDetail = L("Data_SetPlanSpend");
        }
        else if (catalog is null || !hasPricedUsage || quotedCost <= 0m)
        {
            planValueLabel = "-";
            planValueDetail = L("Data_NoPricedUsage30");
        }
        else
        {
            // One multiplier per subscription: each plan is credited with the
            // usage its own OAuth authorized, regardless of which CLI recorded
            // it. Plans with no attributable priced usage are omitted entirely.
            (string Provider, decimal Cost, decimal Quoted)[] valuedPlans = activePlans
                .Select(pair => (Provider: pair.Key, Cost: pair.Value, Quoted: priced.Where(row => row.CostUsd.HasValue && CountsTowardPlan(pair.Key, row)).Sum(row => row.CostUsd.GetValueOrDefault())))
                .Where(plan => plan.Quoted > 0m)
                .OrderByDescending(plan => plan.Quoted / plan.Cost)
                .ToArray();
            if (valuedPlans.Length == 0)
            {
                planValueLabel = "-";
                planValueDetail = L("Data_NoPlanUsage30");
            }
            else
            {
                // TODO: If plan attribution expands beyond the current small provider set,
                // show only the top four multipliers here and expose the remainder as
                // "+N more" details so the Overview summary card cannot grow unbounded.
                planValueLabel = string.Join(Environment.NewLine, valuedPlans.Select(plan => $"{ProviderName(plan.Provider)} {plan.Quoted / plan.Cost:0.0}×"));
                planValueDetail = F("Data_PlanValueDetail", FormatUsd(valuedPlans.Sum(plan => plan.Cost)));
            }
        }
        IReadOnlyList<ResetCycleViewModel> resetCycles = BuildResetCycles(accounts, now);
        // The cached pass must say what it is: data as old as the last fetch,
        // with a fresh one already underway — never pretend it is live.
        DateTimeOffset? cachedAt = latest.Values.Where((ProviderSnapshot snapshot) => snapshot is not null).Select((ProviderSnapshot snapshot) => (DateTimeOffset?)snapshot.ObservedAt).DefaultIfEmpty(null).Max();
        // Nothing cached and nothing fetched yet is a first run, not a fault.
        // "Not loaded" over a row of zeros read as a broken app; say that the
        // history is being indexed, because that is what the wait is.
        bool firstRun = !cachedAt.HasValue && totalTokens == 0;
        string lastUpdated = fromCache
            ? (cachedAt.HasValue
                ? F("Data_CachedFetched", cachedAt.Value.ToLocalTime())
                : firstRun ? L("Data_FirstRun") : L("Dashboard_NotLoaded"))
            : F("Data_Updated", DateTimeOffset.Now);
        // The steady live state needs no caption (the account/snapshot counts
        // used to render as a cryptic "7 · 6" here); only the cached pass and
        // transient refresh states say anything.
        // "Showing saved data" is a lie on a first run: there is none to show.
        // And a live projection taken mid-scan has real limits but partial
        // usage totals, which needs saying or the numbers look wrong.
        string statusMessage = _scanInFlight
            ? L("Data_IndexingHistory")
            : fromCache
                ? (firstRun ? L("Data_FirstRunSaved") : L("Data_ShowingSaved"))
                : string.Empty;
        string pricingCatalogStatus = catalog is null ? L("Data_UnavailableUpper") : L("Data_ValidUpper");
        // The catalog's SHA-256 prefix used to sit here. It is still recorded
        // with every API-equivalent figure, but on screen it read as noise; the
        // card says where the prices came from and how old they are instead.
        string pricingCatalogDetail = catalog is null
            ? L("Data_NoPricingCache")
            : F("Data_CatalogAge", Age(catalog.Age(now)));
        string catalogCaption = catalog is null ? L("Data_CatalogUnavailable") : F("Data_ModelsDevAge", Age(catalog.Age(now)));
        return new DashboardData(FormatTokens(totalTokens), (catalog is null || (!hasPricedUsage && totalTokens > 0)) ? "-" : FormatUsd(quotedCost), F("Data_ExactSources", exactProviders), catalogCaption, lastUpdated, statusMessage, planValueLabel, planValueDetail, cacheSavingsLabel, weekDeltaLabel, resets, providerCards, providerUsage, harnessUsage, usageDays, heatmapCells, usageHistory, usageModelSlices, usageAnalyticsRecords, projectUsageSlices, resetCycles, priced.Select((PricedModelRow item) => item.ViewModel).ToArray(), connections, FormatTokens(totalTokens), FormatTokens(totalInput), FormatTokens(totalOutput), FormatTokens(totalCacheRead), FormatTokens(totalCacheWrite), FormatTokens(totalReasoning), (reportedCost == 0m) ? "-" : FormatUsd(reportedCost), pricingCatalogStatus, pricingCatalogDetail, $"{_successfulScannerCount} / {_scannerCount}", _scannerDetail, L("Data_HealthyUpper"), L("Data_SchemaHealthy"), recentAttempts);
    }

    private IReadOnlyList<UsageAnalyticsRecord> BuildUsageAnalyticsRecords(
        IReadOnlyList<DailyUsageAggregate> rows,
        PricingCatalogSnapshot? catalog,
        ModelResolutionCache resolutions)
    {
        return rows
            .GroupBy(row => new
            {
                row.Day,
                Harness = row.Account.Provider.Value,
                Provider = row.Service.Value,
                row.RawModelId,
                row.Project.ProjectKey,
                row.Project.ProjectPath,
                row.Project.RepositoryRootPath
            })
            .Select(group =>
            {
                DailyUsageAggregate[] values = group.ToArray();
                decimal? cost = QuoteCost(values, catalog, resolutions);
                string provider = RuntimeText.AuthorizationProvider(
                    UsageProviderClassifier.GetDisplayName(group.Key.Harness, group.Key.Provider));
                // Key off the classified label, never the raw service id: the
                // label folds in the recording harness, so distinct ids can
                // share one label and keying on the id listed that provider
                // twice in the usage facets (each filtering half its tokens).
                string providerKey = UsageProviderClassifier.GetKey(group.Key.Harness, group.Key.Provider);
                return new UsageAnalyticsRecord(
                    group.Key.Day,
                    providerKey,
                        provider,
                        group.Key.Harness,
                        ProviderName(group.Key.Harness),
                    group.Key.ProjectKey,
                    UsageProjectLabel(group.Key.ProjectPath, group.Key.RepositoryRootPath),
                    group.Key.RawModelId,
                    group.Key.RawModelId,
                    values.Sum(TotalTokens),
                        values.Sum(row => row.InputTokens),
                        values.Sum(row => row.OutputTokens),
                        values.Sum(row => row.CacheReadTokens),
                        values.Sum(row => row.CacheWriteTokens),
                        values.Sum(row => row.ReasoningTokens),
                    cost);
            })
            .Where(record => record.Tokens > 0)
            .OrderBy(record => record.Day)
            .ToArray();
    }

    private static string UsageProjectLabel(string projectPath, string? repositoryRootPath)
    {
        if (string.Equals(projectPath, "Unknown", StringComparison.OrdinalIgnoreCase))
        {
            return L("Common_Unknown");
        }
        string trimmed = projectPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string name = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(name)) name = projectPath;
        bool worktree = !string.IsNullOrWhiteSpace(repositoryRootPath)
            && !string.Equals(repositoryRootPath, projectPath, StringComparison.OrdinalIgnoreCase);
        return worktree ? name + L("UsageWindow_WorktreeSuffix") : name;
    }

    private static IReadOnlyList<UsageModelSliceViewModel> BuildUsageModelSlices(
        IReadOnlyList<DailyUsageAggregate> rows)
    {
        return rows
            .GroupBy(row => new { row.Day, row.RawModelId })
            .OrderBy(group => group.Key.Day)
            .ThenByDescending(group => group.Sum(TotalTokens))
            .Select(group => new UsageModelSliceViewModel(
                group.Key.Day,
                group.Key.RawModelId,
                group.Sum(TotalTokens)))
            .ToArray();
    }

    private IReadOnlyList<UsageHistoryDayViewModel> BuildUsageHistory(
        IReadOnlyList<DailyUsageAggregate> rows,
        PricingCatalogSnapshot? catalog,
        ModelResolutionCache resolutions)
    {
        return rows
            .GroupBy(row => row.Day)
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                DailyUsageAggregate[] values = group.ToArray();
                return new UsageHistoryDayViewModel(group.Key, values.Sum(TotalTokens),
                    QuoteCost(values, catalog, resolutions));
            })
            .ToArray();
    }

    private IReadOnlyList<ProjectUsageSliceViewModel> BuildProjectUsageSlices(
        IReadOnlyList<DailyUsageAggregate> rows,
        PricingCatalogSnapshot? catalog,
        ModelResolutionCache resolutions)
    {
        return rows
            .GroupBy(row => new
            {
                row.Day,
                row.Project.ProjectKey,
                row.Project.ProjectPath,
                row.Project.RepositoryRootPath
            })
            .OrderBy(group => group.Key.Day)
            .ThenBy(group => group.Key.ProjectPath, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                DailyUsageAggregate[] projectRows = group.ToArray();
                return new ProjectUsageSliceViewModel(
                    group.Key.Day,
                    group.Key.ProjectKey,
                    group.Key.ProjectPath,
                    group.Key.RepositoryRootPath,
                    projectRows.Sum(TotalTokens),
                    QuoteCost(projectRows, catalog, resolutions));
            })
            .ToArray();
    }

    /// <summary>
    /// Memoises model resolution for one projection. The catalog is fixed for
    /// the duration, so the same (service, model) pair always resolves the same
    /// way — and the history aggregations below ask about the same few dozen
    /// pairs hundreds of times each.
    /// </summary>
    private sealed class ModelResolutionCache(ExplicitModelResolver explicitResolver, CatalogModelResolver catalogResolver)
    {
        private readonly Dictionary<(string Service, string Model), ModelResolution?> _entries = [];

        public ModelResolution? Resolve(ServiceProviderId service, string rawModelId, PricingCatalogSnapshot? catalog)
        {
            (string, string) key = (service.Value, rawModelId);
            if (_entries.TryGetValue(key, out ModelResolution? cached))
            {
                return cached;
            }
            ModelResolution? resolved = catalog is null
                ? explicitResolver.Resolve(service, rawModelId)
                : catalogResolver.Resolve(service, rawModelId, catalog);
            _entries[key] = resolved;
            return resolved;
        }
    }

    /// <summary>
    /// API-equivalent cost for a set of rows, using the same grouping and the
    /// same all-or-nothing lane rules as <see cref="BuildModelRows"/>.
    ///
    /// The three history aggregations want only this number. They used to get
    /// it by calling <see cref="BuildModelRows"/> once per group, which builds a
    /// full <see cref="ModelUsageRowViewModel"/> — localized status string,
    /// formatted token count, formatted currency — for every group and then
    /// throws all of it away. That was ~1.6 s of the ~2 s projection, three
    /// times per startup.
    ///
    /// Pricing cannot simply be summed per row: <c>CanPrice</c> rejects a whole
    /// group when any lane carries tokens the catalog has no rate for, so the
    /// aggregate must be formed before it is quoted.
    /// </summary>
    private decimal? QuoteCost(
        IReadOnlyList<DailyUsageAggregate> rows,
        PricingCatalogSnapshot? catalog,
        ModelResolutionCache resolutions)
    {
        decimal total = 0m;
        bool anyPriced = false;
        foreach (IGrouping<(ProviderId Source, ServiceProviderId Service, string RawModelId), DailyUsageAggregate> group
            in rows.GroupBy(row => (Source: row.Account.Provider, Service: row.Service, RawModelId: row.RawModelId)))
        {
            long input = 0L, output = 0L, cacheRead = 0L, cacheWrite = 0L, reasoning = 0L;
            foreach (DailyUsageAggregate row in group)
            {
                input += row.InputTokens;
                output += row.OutputTokens;
                cacheRead += row.CacheReadTokens;
                cacheWrite += row.CacheWriteTokens;
                reasoning += row.ReasoningTokens;
            }
            if (input + output + cacheRead + cacheWrite + reasoning <= 0L)
            {
                continue;
            }

            decimal? cost = null;
            if (catalog is not null)
            {
                ModelResolution? resolution = resolutions.Resolve(group.Key.Service, group.Key.RawModelId, catalog);
                cost = _pricing.Quote(
                    new TokenUsageEvent(group.First().Account, group.Key.Service, group.Key.RawModelId, _clock.UtcNow,
                        input, output, cacheRead, cacheWrite, reasoning, "dashboard-aggregate"),
                    resolution,
                    catalog)?.CostUsd;
            }
            // Gap-fill only, exactly as in BuildModelRows: a manual rate applies
            // solely when the catalog produced no quote.
            if (!cost.HasValue
                && _pricingOverrides.TryGet(group.Key.Service.Value, group.Key.RawModelId, out ManualModelPrice manual))
            {
                cost = manual.QuoteUsd(input, output, cacheRead, cacheWrite, reasoning);
            }
            if (cost.HasValue)
            {
                total += cost.Value;
                anyPriced = true;
            }
        }
        return anyPriced ? total : null;
    }

    private IReadOnlyList<PricedModelRow> BuildModelRows(IReadOnlyList<DailyUsageAggregate> rows, PricingCatalogSnapshot? catalog, ModelResolutionCache resolutions)
    {
        return (from item in (from row in rows
                group row by (Source: row.Account.Provider, Service: row.Service, RawModelId: row.RawModelId)).Select(delegate(IGrouping<(ProviderId Source, ServiceProviderId Service, string RawModelId), DailyUsageAggregate> @group)
            {
                long num = @group.Sum((DailyUsageAggregate row) => row.InputTokens);
                long num2 = @group.Sum((DailyUsageAggregate row) => row.OutputTokens);
                long num3 = @group.Sum((DailyUsageAggregate row) => row.CacheReadTokens);
                long num4 = @group.Sum((DailyUsageAggregate row) => row.CacheWriteTokens);
                long num5 = @group.Sum((DailyUsageAggregate row) => row.ReasoningTokens);
                decimal? manualCostUsd = null;
                string? manualStatus = null;
                ModelResolution? modelResolution = resolutions.Resolve(@group.Key.Service, @group.Key.RawModelId, catalog);
                ApiEquivalentQuote apiEquivalentQuote = null;
                if (catalog is not null)
                {
                    apiEquivalentQuote = _pricing.Quote(new TokenUsageEvent(@group.First().Account, @group.Key.Service, @group.Key.RawModelId, _clock.UtcNow, num, num2, num3, num4, num5, "dashboard-aggregate"), modelResolution, catalog);
                }
                long value = num + num2 + num3 + num4 + num5;
                string pricingStatus = catalog is null
                    ? L("Data_CatalogUnavailable")
                    : apiEquivalentQuote is null
                        ? (modelResolution is null ? L("Data_NoCatalogMatch") : L("Data_RateIncomplete"))
                        : modelResolution?.Confidence switch
                {
                    ResolutionConfidence.ExplicitAlias => L("Data_PricedAlias"),
                    ResolutionConfidence.DerivedMultiplier => L("Data_DerivedMultiplier"),
                    _ => L("Data_PricedExact")
                };
                string source = ProviderName(@group.Key.Source.Value);
                string authProvider = RuntimeText.AuthorizationProvider(UsageProviderClassifier.GetDisplayName(@group.Key.Source.Value, @group.Key.Service.Value));
                decimal cacheSaved = 0m;
                if (modelResolution is not null && apiEquivalentQuote is not null && catalog is not null && num3 > 0
                    && catalog.ExactIndex.TryGetValue((modelResolution.PricingProviderId, modelResolution.CanonicalModelId), out ModelPrice price)
                    && price.InputPerMillion.HasValue && price.CacheReadPerMillion.HasValue)
                {
                    cacheSaved = modelResolution.RateMultiplier * (decimal)num3 * Math.Max(0m, price.InputPerMillion.Value - price.CacheReadPerMillion.Value) / 1000000m;
                }
                // Gap-fill only: a manual per-1M rate applies solely when the catalog
            // produced no quote; catalog pricing is never shadowed by an override.
            if (apiEquivalentQuote is null && _pricingOverrides.TryGet(@group.Key.Service.Value, @group.Key.RawModelId, out ManualModelPrice manualPrice))
            {
                manualCostUsd = manualPrice.QuoteUsd(num, num2, num3, num4, num5);
                manualStatus = L("Data_PricedManual");
            }
            decimal? finalCostUsd = apiEquivalentQuote?.CostUsd ?? manualCostUsd;
            return new PricedModelRow(new ModelUsageRowViewModel(source, @group.Key.RawModelId, authProvider, FormatTokens(value), (!finalCostUsd.HasValue) ? "-" : FormatUsd(finalCostUsd.Value), manualStatus ?? pricingStatus, @group.Key.Service.Value, finalCostUsd.HasValue), finalCostUsd, value, num, num2, num3, num4, num5, cacheSaved);
            })
            where item.RawTokens > 0
            orderby item.RawTokens descending
            select item).ToArray();
    }


    private static IReadOnlyList<ProviderUsageViewModel> BuildProviderUsage(IReadOnlyList<PricedModelRow> rows) =>
        BuildUsageGrouping(rows, row => row.ViewModel.AuthProvider);

    private static IReadOnlyList<ProviderUsageViewModel> BuildHarnessUsage(IReadOnlyList<PricedModelRow> rows) =>
        BuildUsageGrouping(rows, row => row.ViewModel.Source);

    private static IReadOnlyList<ProviderUsageViewModel> BuildUsageGrouping(
        IReadOnlyList<PricedModelRow> rows,
        Func<PricedModelRow, string> groupKey)
    {
        long grandTotal = Math.Max(1L, rows.Sum(row => row.RawTokens));
        return rows
            .GroupBy(row => groupKey(row), StringComparer.OrdinalIgnoreCase)
            // A group with zero observed tokens (e.g. a one-off failed try)
            // is just wasted space.
            .Where(group => group.Sum(row => row.RawTokens) > 0L)
            .OrderByDescending(group => group.Sum(row => row.RawTokens))
            .Select(group =>
            {
                int modelCount = group.Select(row => row.ViewModel.Model).Distinct(StringComparer.OrdinalIgnoreCase).Count();
                // One line per model, biggest first. The card is a fixed-height
                // cell in a uniform grid, so this stays capped at three.
                string topModels = string.Join("\n", group
                    .GroupBy(row => row.ViewModel.Model, StringComparer.OrdinalIgnoreCase)
                    .Select(models => new
                    {
                        Model = models.Key,
                        RawTokens = models.Sum(row => row.RawTokens),
                    })
                    .OrderByDescending(item => item.RawTokens)
                    .Take(3)
                    .Select(item => item.Model + " · " + FormatTokens(item.RawTokens)));
                int pricedCount = group.Count(row => row.CostUsd.HasValue);
                decimal cost = group.Where(row => row.CostUsd.HasValue).Sum(row => row.CostUsd.GetValueOrDefault());
                string harnesses = string.Join(", ", group.Select(row => row.ViewModel.Source).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(value => value));
                string accent = ProviderColors.Resolve(group.Key);
                long groupTokens = group.Sum(row => row.RawTokens);
                return new ProviderUsageViewModel(
                    group.Key,
                    FormatTokens(groupTokens),
                    pricedCount == 0 ? "-" : FormatUsdCompact(cost),
                    F("Data_ModelsPriced", pricedCount, modelCount),
                    topModels.Length == 0 ? F("Data_ModelCount", modelCount) : topModels,
                    harnesses,
                    accent,
                    100.0 * groupTokens / grandTotal);
            })
            .ToArray();
    }

    private static IReadOnlyList<UsageDayViewModel> BuildDailyRows(IReadOnlyList<DailyUsageAggregate> rows, DateOnly today)
    {
        Dictionary<DateOnly, long> dictionary = (from offset in Enumerable.Range(0, 7)
            select today.AddDays(offset - 6)).ToDictionary((DateOnly day) => day, (DateOnly _) => 0L);
        Dictionary<DateOnly, DailyUsageAggregate[]> byDay = rows
            .Where(row => dictionary.ContainsKey(row.Day))
            .GroupBy(row => row.Day)
            .ToDictionary(group => group.Key, group => group.ToArray());
        foreach (KeyValuePair<DateOnly, DailyUsageAggregate[]> item in byDay)
        {
            dictionary[item.Key] = item.Value.Sum((Func<DailyUsageAggregate, long>)TotalTokens);
        }
        long maximum = Math.Max(1L, dictionary.Values.Max());
        return dictionary.Select((KeyValuePair<DateOnly, long> pair) => new UsageDayViewModel(
            pair.Key.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant(),
            (pair.Value == 0L) ? 4.0 : (24.0 + 116.0 * (double)pair.Value / (double)maximum),
            FormatTokens(pair.Value),
            BuildDayBreakdown(pair.Key, pair.Value, byDay.GetValueOrDefault(pair.Key)))).ToArray();
    }

    // Hover content for a day bar: top three models plus the rest aggregated.
    private static string BuildDayBreakdown(DateOnly day, long total, DailyUsageAggregate[]? dayRows)
    {
        string header = day.ToString("ddd, dd MMM", CultureInfo.CurrentCulture);
        if (total == 0L || dayRows is null || dayRows.Length == 0)
        {
            return F("Data_DayNoUsage", header);
        }
        (string Model, long Tokens)[] models = dayRows
            .GroupBy(row => row.RawModelId, StringComparer.OrdinalIgnoreCase)
            .Select(group => (Model: group.Key, Tokens: group.Sum((Func<DailyUsageAggregate, long>)TotalTokens)))
            .Where(item => item.Tokens > 0)
            .OrderByDescending(item => item.Tokens)
            .ToArray();
        IEnumerable<string> lines = models.Take(3).Select(item => $"{item.Model} · {FormatTokens(item.Tokens)}");
        long others = models.Skip(3).Sum(item => item.Tokens);
        if (others > 0)
        {
            lines = lines.Append(F("Data_Others", FormatTokens(others)));
        }
        return F("Data_DayUsage", header, FormatTokens(total), string.Join("\n", lines));
    }

    private async Task RefreshResetCreditsIfDueAsync(IReadOnlyList<ProviderAccount> accounts, bool force, CancellationToken cancellationToken)
    {
        if (!force && _resetCreditsFetchedAt.HasValue && _clock.UtcNow - _resetCreditsFetchedAt.Value < ResetCreditsTtl)
        {
            return;
        }
        _resetCredits.Clear();
        foreach (ProviderAccount account in accounts.Where((ProviderAccount providerAccount) => providerAccount.IsConnected))
        {
            if (!_adapterById.TryGetValue(account.Key.Provider, out IProviderAdapter adapter) || adapter is not IResetCreditSource source)
            {
                continue;
            }
            try
            {
                _resetCredits[account.Key] = await source.FetchResetCreditsAsync(account, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                _resetCredits[account.Key] = null;
            }
        }
        _resetCreditsFetchedAt = _clock.UtcNow;
    }

    private IReadOnlyList<ResetCycleViewModel> BuildResetCycles(IReadOnlyList<ProviderAccount> accounts, DateTimeOffset now)
    {
        List<ResetCycleViewModel> list = new List<ResetCycleViewModel>();
        foreach (KeyValuePair<AccountKey, ResetCreditInventory?> pair in _resetCredits)
        {
            ResetCreditInventory inventory = pair.Value;
            if (inventory is null)
            {
                continue;
            }
            IProviderAdapter adapter = _adapterById.GetValueOrDefault(pair.Key.Provider);
            ProviderAccount account = accounts.FirstOrDefault((ProviderAccount candidate) => candidate.Key == pair.Key);
            IReadOnlyList<ResetCredit> available = inventory.Available(now);
            string title = available.Count == 1 ? L("Data_OneResetCredit") : F("Data_ResetCredits", available.Count);
            string detail;
            if (available.Count == 0)
            {
                detail = L("Data_NoResetCredits");
            }
            else
            {
                string[] expiring = available
                    .Where((ResetCredit credit) => credit.ExpiresAt.HasValue)
                    .Select((ResetCredit credit) => Countdown(credit.ExpiresAt.Value - now) + " (" + credit.ExpiresAt.Value.ToLocalTime().ToString("dd MMM", CultureInfo.CurrentCulture) + ")")
                    .ToArray();
                detail = expiring.Length == 0 ? L("Data_NoExpiry") : F("Data_Expires", string.Join(" | ", expiring));
                int withoutExpiry = available.Count - expiring.Length;
                if (withoutExpiry > 0 && expiring.Length > 0)
                {
                    detail += F("Data_WithoutExpiry", withoutExpiry);
                }
            }
            list.Add(new ResetCycleViewModel(
                adapter?.Descriptor.DisplayName ?? pair.Key.Provider.Value,
                account?.Login ?? (account is null ? string.Empty : RuntimeText.AccountFallback(pair.Key.Provider.Value)),
                title,
                detail,
                adapter?.Descriptor.AccentColor ?? "#7E878B"));
        }
        return list.OrderBy((ResetCycleViewModel item) => item.Provider, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private static string BuildWeekDelta(IReadOnlyList<DailyUsageAggregate> rows, DateOnly today)
    {
        DateOnly lastWeekStart = today.AddDays(-6);
        DateOnly prevWeekStart = today.AddDays(-13);
        long last7 = rows.Where((DailyUsageAggregate row) => row.Day >= lastWeekStart).Sum(TotalTokens);
        long prev7 = rows.Where((DailyUsageAggregate row) => row.Day >= prevWeekStart && row.Day < lastWeekStart).Sum(TotalTokens);
        if (prev7 == 0L)
        {
            return last7 > 0L ? L("Data_NewActivityWeek") : "";
        }
        double pct = 100.0 * ((double)last7 - (double)prev7) / (double)prev7;
        return F("Data_VsPriorWeek", pct >= 0.0 ? "↑" : "↓", Math.Abs(pct));
    }

    private static IReadOnlyList<HeatmapCellViewModel> BuildHeatmap(IReadOnlyList<DailyUsageAggregate> rows, DateOnly today)
    {
        Dictionary<DateOnly, long> byDay = (from row in rows
            group row by row.Day).ToDictionary((IGrouping<DateOnly, DailyUsageAggregate> group) => group.Key, (IGrouping<DateOnly, DailyUsageAggregate> group) => ((IEnumerable<DailyUsageAggregate>)group).Sum((Func<DailyUsageAggregate, long>)TotalTokens));
        DateOnly start = today.AddDays(-83);
        while (start.DayOfWeek != DayOfWeek.Monday)
        {
            start = start.AddDays(-1);
        }
        long maximum = Math.Max(1L, byDay.Values.DefaultIfEmpty(0L).Max());
        List<HeatmapCellViewModel> cells = new List<HeatmapCellViewModel>();
        for (DateOnly day = start; day <= today; day = day.AddDays(1))
        {
            long tokens = byDay.GetValueOrDefault(day);
            double intensity = (tokens == 0L) ? 0.0 : Math.Sqrt((double)tokens / (double)maximum);
            string usage = tokens == 0L ? L("Data_NoUsage") : F("Data_Tokens", FormatTokens(tokens));
            cells.Add(new HeatmapCellViewModel(day.ToString("ddd, dd MMM", CultureInfo.CurrentCulture) + " — " + usage, intensity, tokens > 0L));
        }
        return cells;
    }

    private IReadOnlyList<ProviderCardViewModel> BuildProviderCards(IReadOnlyList<ProviderAccount> accounts, IReadOnlyDictionary<AccountKey, ProviderSnapshot?> latest, IReadOnlyList<DailyUsageAggregate> usage, DateTimeOffset now, IReadOnlyDictionary<AccountKey, FetchFailureKind> latestFailures)
    {
        // Overview-only filter: hidden providers keep refreshing and scanning,
        // and stay listed in Connections and Usage.
        ProviderVisibilitySet visibility = ProviderVisibilityPreference.Load();
        List<ProviderCardViewModel> list = new List<ProviderCardViewModel>();
        foreach (IProviderAdapter adapter in _adapters)
        {
            if (!visibility.IsVisible(adapter.Descriptor.Id.Value))
            {
                continue;
            }
            foreach (ProviderAccount account in accounts.Where((ProviderAccount providerAccount) => providerAccount.Key.Provider == adapter.Descriptor.Id))
            {
                latest.TryGetValue(account.Key, out ProviderSnapshot? value);
                bool hasData = value is not null && (value.Meters.Count > 0 || value.Balances.Count > 0);
                // A detected provider keeps its card even while broken: an empty
                // card that says "Sign-in required" or "Offline" beats a silent
                // disappearance. Only never-connected accounts without any data
                // stay off the Overview.
                if (!hasData && !account.IsConnected)
                {
                    continue;
                }
                bool flag = usage.Any((DailyUsageAggregate row) => row.Account == account.Key);
                (string healthLabel, CardStatusKind statusKind) = HealthStatus(account, hasData ? value : null, now, latestFailures.GetValueOrDefault(account.Key));
                // The section promises "only providers that returned real quota or
                // balance data". A connected source that simply never yields quota
                // (e.g. OpenCode local history) would sit here as a permanently
                // empty NO QUOTA card — failure states stay visible, this doesn't.
                if (!hasData && statusKind == CardStatusKind.NoQuota)
                {
                    continue;
                }
                MeterViewModel[] allMeters = value?.Meters
                    .OrderBy(meter => meter, MeterDisplayOrderComparer.Instance)
                    .Select(ToMeterViewModel)
                    .ToArray() ?? Array.Empty<MeterViewModel>();
                BalanceMetric balanceMetric = value?.Balances.FirstOrDefault();
                ProviderServiceStatus? serviceStatus = _providerStatuses.Get(adapter.Descriptor.Id.Value);
                string incidentSummary = serviceStatus is { IsOperational: false }
                    ? RuntimeText.ProviderStatus(serviceStatus.Indicator, serviceStatus.Description)
                    : string.Empty;
                list.Add(new ProviderCardViewModel(
                    adapter.Descriptor.Id.Value,
                    adapter.Descriptor.DisplayName,
                    AccountLabel(account, value),
                    adapter.Descriptor.AccentColor,
                    healthLabel,
                    flag ? L("Data_ExactTokens") : (adapter.Descriptor.SupportsExactTokens ? L("Data_NoTokenData") : L("Data_LimitsOnly")),
                    allMeters,
                    (balanceMetric is null) ? string.Empty : FormatBalance(balanceMetric),
                    incidentSummary,
                    serviceStatus?.Indicator ?? string.Empty,
                    statusKind,
                    RuntimeText.AuthSource(account.Key.Provider.Value, account.AuthSource)));
            }
        }
        return list.OrderByDescending(card => card.AllMeters.Select(meter => meter.UsedPercent).DefaultIfEmpty(-1).Max()).ThenBy(card => card.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    private IReadOnlyList<ProviderConnectionViewModel> BuildConnections(IReadOnlyList<ProviderAccount> accounts, IReadOnlyDictionary<AccountKey, ProviderSnapshot?> latest, IReadOnlyList<DailyUsageAggregate> usage, DateTimeOffset now, IReadOnlyDictionary<AccountKey, FetchFailureKind> latestFailures)
    {
        List<ProviderConnectionViewModel> list = new List<ProviderConnectionViewModel>();
        foreach (ProviderDescriptor descriptor in BuiltInProviderDescriptors.All)
        {
            ProviderAccount[] array = accounts.Where((ProviderAccount providerAccount) => providerAccount.Key.Provider == descriptor.Id).ToArray();
            (string ActionLabel, string ActionKind, string ActionTarget) action = ConnectionAction(descriptor.Id.Value);
            if (array.Length == 0)
            {
                string accountLabel = descriptor.Id.Value == "opencode" ? L("Data_NoLocalHistory") : descriptor.Id.Value == "droid" ? L("Data_NoFactorySession") : L("Data_NoLocalAccount");
                string status = descriptor.Id.Value == "opencode" ? L("Data_HistoryNotDetected") : L("Data_NotConnected");
                list.Add(new ProviderConnectionViewModel(descriptor.DisplayName, accountLabel, LocalizedCapabilities(descriptor.Id.Value), status, LocalizedCoverage(descriptor.Id.Value), descriptor.AccentColor, action.ActionLabel, action.ActionKind, action.ActionTarget, descriptor.Id.Value, isConnected: false));
                continue;
            }
            ProviderAccount[] array2 = array;
            foreach (ProviderAccount account in array2)
            {
                latest.TryGetValue(account.Key, out ProviderSnapshot? value);
                FetchFailureKind latestFailure = latestFailures.GetValueOrDefault(account.Key);
                (string statusLabel, CardStatusKind statusKind) = HealthStatus(account, value, now, latestFailure);
                string health = descriptor.Id.Value == "opencode" ? L("Data_LocalHistoryDetected") : descriptor.Id.Value == "droid" && account.IsConnected ? L("Data_FactorySessionDetected") : statusLabel;
                // The local-history sources report Live on detection alone, but a
                // failed latest attempt must still drop them out of Live.
                if (descriptor.Id.Value is "opencode" or "droid" && account.IsConnected && latestFailure == FetchFailureKind.None)
                {
                    statusKind = CardStatusKind.Live;
                }
                string coverage = descriptor.Id.Value == "opencode"
                    ? L("Data_OpenCodeCoverage")
                    : usage.Any((DailyUsageAggregate row) => row.Account == account.Key) ? L("Data_ExactLocalTokens") : LocalizedCoverage(descriptor.Id.Value);
                ProviderConnectionViewModel connection = new ProviderConnectionViewModel(descriptor.DisplayName, AccountLabel(account, value), RuntimeText.AuthSource(descriptor.Id.Value, account.AuthSource), health, coverage, descriptor.AccentColor, action.ActionLabel, action.ActionKind, action.ActionTarget, descriptor.Id.Value, account.IsConnected)
                {
                    StatusKind = statusKind
                };
                if (value is { Extensions: { } extensions }
                    && extensions.TryGetValue("plan_type", out JsonElement planElement)
                    && planElement.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(planElement.GetString()))
                {
                    (string planLabel, double suggested) = PlanInfo(descriptor.Id.Value, planElement.GetString()!);
                    connection.DetectedPlan = planLabel;
                    connection.SuggestedMonthlyCost = suggested;
                }
                list.Add(connection);
            }
        }
        return list;
    }

    private static string AccountLabel(ProviderAccount account, ProviderSnapshot? snapshot)
    {
        if (!string.IsNullOrWhiteSpace(account.Login))
        {
            return account.Login;
        }
        if (snapshot is { Extensions: { } extensions })
        {
            foreach (string key in new[] { "email", "login", "username" })
            {
                if (extensions.TryGetValue(key, out JsonElement value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString()!.Trim();
                }
            }
        }
        return RuntimeText.AccountFallback(account.Key.Provider.Value);
    }


    // Detection is passive: we only ever link to the provider's own usage
    // dashboard in the browser — never launch or drive a CLI.
    private static (string ActionLabel, string ActionKind, string ActionTarget) ConnectionAction(string providerId)
    {
        return providerId switch
        {
            "codex" => (F("Data_ViewUsageOn", "ChatGPT"), "uri", "https://chatgpt.com/codex/settings/usage"),
            "claude" => (F("Data_ViewUsageOn", "Claude"), "uri", "https://claude.ai/settings/usage"),
            "droid" => (F("Data_ViewUsageOn", "Factory"), "uri", "https://app.factory.ai/settings/usage"),
            "amp" => (F("Data_ViewUsageOn", "Amp"), "uri", "https://ampcode.com/settings/usage"),
            "cline" => (F("Data_ViewUsageOn", "Cline"), "uri", "https://app.cline.bot/dashboard/subscription"),
            "copilot" => (L("Data_ViewCopilotSettings"), "uri", "https://github.com/settings/copilot"),
            "cursor" => (F("Data_ViewUsageOn", "Cursor"), "uri", "https://cursor.com/dashboard?tab=usage"),
            _ => ("", "", "")
        };
    }

    private static MeterViewModel ToMeterViewModel(UsageMeter meter)
    {
        double valueOrDefault = meter.UsedPercent.GetValueOrDefault();
        string usedLabel = !meter.Used.HasValue ? F("Meter_PercentUsed", valueOrDefault) : F("Meter_MeasureUsed", FormatMeasure(meter.Used.Value, meter.Unit));
        string remainingLabel = meter.Limit.HasValue && meter.Used.HasValue
            ? F("Meter_MeasureRemaining", FormatMeasure(Math.Max(0m, meter.Limit.Value - meter.Used.Value), meter.Unit))
            : F("Meter_PercentRemaining", Math.Max(0.0, 100.0 - valueOrDefault));
        string resetLabel = !meter.ResetsAt.HasValue ? L("Meter_NoScheduledReset") : F("Meter_ResetsIn", Countdown(meter.ResetsAt.Value - DateTimeOffset.UtcNow));
        return new MeterViewModel(meter.Key.Value, RuntimeText.MeterDisplayName(meter.Key.Value, meter.DisplayName), valueOrDefault, usedLabel, remainingLabel, resetLabel, meter.ResetsAt, meter.Status, meter.IsNew, meter.Status == MeterStatus.Stale, PaceLabel(meter, DateTimeOffset.UtcNow));
    }

    private static string PaceLabel(UsageMeter meter, DateTimeOffset now)
    {
        if (!meter.ResetsAt.HasValue || !meter.WindowDuration.HasValue || meter.Status is MeterStatus.Stale or MeterStatus.Exhausted)
        {
            return "";
        }
        TimeSpan window = meter.WindowDuration.Value;
        DateTimeOffset resetsAt = meter.ResetsAt.Value;
        if (window <= TimeSpan.Zero || resetsAt <= now)
        {
            return "";
        }
        DateTimeOffset windowStart = resetsAt - window;
        TimeSpan elapsed = now - windowStart;
        double used = meter.UsedPercent.GetValueOrDefault();
        if (elapsed <= TimeSpan.Zero || used <= 0.0 || used >= 100.0)
        {
            return "";
        }
        double elapsedFraction = Math.Min(1.0, elapsed.TotalSeconds / window.TotalSeconds);
        // Too little of the window has passed for the pace to mean anything.
        if (elapsedFraction < 0.05)
        {
            return "";
        }
        double projected = used / elapsedFraction;
        if (projected >= 100.0)
        {
            DateTimeOffset exhaustAt = windowStart + TimeSpan.FromSeconds(elapsed.TotalSeconds * (100.0 / used));
            return exhaustAt <= now ? L("Meter_PaceExhaustNow") : F("Meter_PaceExhaustBeforeReset", Countdown(exhaustAt - now));
        }
        return F("Meter_PaceByReset", projected);
    }

        /*
    private static string Health(ProviderAccount account, ProviderSnapshot? snapshot, DateTimeOffset now)
    {
        if (snapshot is not null)
        {
            TimeSpan timeSpan = now - snapshot.ObservedAt;
            if (!(timeSpan < TimeSpan.FromMinutes(2L)))
            {
                return "Cached ú " + Age(timeSpan);
            }
            return "Fresh ú " + account.AuthSource;
        }
        if (!account.IsConnected)
        {
            return "Not connected";
        }
        return "Connected ú no quota data";
    }

        */

    private static (string Label, CardStatusKind Kind) HealthStatus(ProviderAccount account, ProviderSnapshot? snapshot, DateTimeOffset now, FetchFailureKind latestFailure = FetchFailureKind.None)
    {
        TimeSpan? age = snapshot is null ? null : now - snapshot.ObservedAt;
        return AccountHealthPolicy.Decide(account.IsConnected, age, latestFailure) switch
        {
            AccountHealth.Live => (F("Data_FreshVia", RuntimeText.AuthSource(account.Key.Provider.Value, account.AuthSource)), CardStatusKind.Live),
            AccountHealth.SignInRequired => age is { } signInAge
                ? (F("Data_SignInRequiredAge", Age(signInAge)), CardStatusKind.SignInRequired)
                : (L("Data_SignInRequired"), CardStatusKind.SignInRequired),
            AccountHealth.RateLimited => age is { } limitedAge
                ? (F("Data_RateLimitedAge", Age(limitedAge)), CardStatusKind.RateLimited)
                : (L("Data_RateLimited"), CardStatusKind.RateLimited),
            AccountHealth.Offline => age is { } offlineAge
                ? (F("Data_OfflineAge", Age(offlineAge)), CardStatusKind.Offline)
                : (L("Data_Offline"), CardStatusKind.Offline),
            AccountHealth.UnsupportedResponse => age is { } unsupportedAge
                ? (F("Data_UnsupportedAge", Age(unsupportedAge)), CardStatusKind.Error)
                : (L("Data_Unsupported"), CardStatusKind.Error),
            AccountHealth.Retrying => age is { } retryAge
                ? (F("Data_RetryingAge", Age(retryAge)), CardStatusKind.Stale)
                : (L("Data_Retrying"), CardStatusKind.Stale),
            AccountHealth.FetchFailed => (L("Data_FetchFailed"), CardStatusKind.Error),
            AccountHealth.NotConnected => (L("Data_NotConnected"), CardStatusKind.Offline),
            // Cached always carries an age; NoQuota only occurs with no snapshot.
            _ => age is { } cachedAge
                ? (F("Data_CachedAge", Age(cachedAge)), CardStatusKind.Stale)
                : (L("Data_ConnectedNoQuota"), CardStatusKind.NoQuota)
        };
    }

    private async Task<IReadOnlyList<FetchAttemptViewModel>> ReadRecentAttemptsAsync(CancellationToken cancellationToken)
    {
        List<FetchAttemptViewModel> attempts = [];
        await using (SqliteConnection connection = await _database.OpenAsync(cancellationToken).ConfigureAwait(false))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT provider_id, strategy_id, duration_ms, failure_kind\nFROM fetch_attempts\nORDER BY started_at DESC\nLIMIT 8;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                long durationMs = reader.GetInt64(2);
                FetchFailureKind kind = (FetchFailureKind)reader.GetInt32(3);
                attempts.Add(new FetchAttemptViewModel(
                    ProviderName(reader.GetString(0)),
                    reader.GetString(1),
                    durationMs < 1000 ? $"{durationMs} ms" : $"{durationMs / 1000.0:0.0} s",
                    RuntimeText.FetchFailure(kind),
                    RuntimeText.FetchFailureMeaning(kind),
                    RuntimeText.FetchOutcome(kind)));
            }
        }
        return attempts;
    }

    private static long TotalTokens(DailyUsageAggregate row)
    {
        return row.InputTokens + row.OutputTokens + row.CacheReadTokens + row.CacheWriteTokens + row.ReasoningTokens;
    }

    private static string FormatTokens(long value)
    {
        if (value >= 1000000)
        {
            if (value >= 1000000000)
            {
                return $"{(double)value / 1000000000.0:0.##}B";
            }
            return $"{(double)value / 1000000.0:0.##}M";
        }
        if (value >= 1000)
        {
            return $"{(double)value / 1000.0:0.##}K";
        }
        return value.ToString("N0", CultureInfo.InvariantCulture);
    }

    private static string FormatUsd(decimal value)
    {
        return value.ToString("$0.00", CultureInfo.InvariantCulture);
    }

    // Card-sized slots: drop cents once the amount is wide enough to trim.
    private static string FormatUsdCompact(decimal value)
    {
        return (value >= 1000m) ? value.ToString("$#,##0", CultureInfo.InvariantCulture) : FormatUsd(value);
    }

    private static string FormatBalance(BalanceMetric balance)
    {
        return balance.FormattedValue ?? (RuntimeText.MeterDisplayName(balance.Key, balance.DisplayName) + ": " + FormatMeasure(balance.Value.GetValueOrDefault(), balance.Unit));
    }

    private static string FormatMeasure(decimal value, MeterUnit unit)
    {
        return unit switch
        {
            MeterUnit.Usd => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase)
                ? value.ToString("0.##", CultureInfo.CurrentCulture) + "$"
                : value.ToString("$0.##", CultureInfo.InvariantCulture),
            MeterUnit.Credits => F("Data_Credits", value),
            MeterUnit.Requests => $"{value:0.##}", 
            MeterUnit.Tokens => FormatTokens(decimal.ToInt64(value)), 
            MeterUnit.Percent => $"{value:0.#}%", 
            _ => value.ToString("0.##", CultureInfo.InvariantCulture), 
        };
    }

    private static IReadOnlyDictionary<string, string> DetectPlans(IReadOnlyDictionary<AccountKey, ProviderSnapshot> latest)
    {
        Dictionary<string, string> plans = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (KeyValuePair<AccountKey, ProviderSnapshot> pair in latest)
        {
            if (pair.Value is { Extensions: { } extensions }
                && extensions.TryGetValue("plan_type", out JsonElement element)
                && element.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(element.GetString()))
            {
                plans[pair.Key.Provider.Value] = element.GetString()!;
            }
        }
        return plans;
    }

    // Standard list prices for unambiguous plans only. Claude "max" is left
    // NaN on purpose: it can be the 5× ($100) or 20× ($200) tier and the
    // credentials do not say which.
    private static (string Label, double SuggestedMonthlyUsd) PlanInfo(string providerId, string rawPlan)
    {
        string normalized = rawPlan.Trim().ToLowerInvariant();
        string label = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(normalized.Replace('_', ' '));
        double suggested = (providerId.ToLowerInvariant(), normalized) switch
        {
            ("codex", "plus") => 20,
            ("codex", "pro") => 200,
            ("codex", "team") or ("codex", "business") => 25,
            ("claude", "pro") => 20,
            _ => double.NaN
        };
        return (label, suggested);
    }

    // Which recorded usage burned this subscription: match on the authorizing
    // provider, not the recording CLI (OpenCode traffic on ChatGPT OAuth still
    // consumes the Codex plan). Codex/Claude Code transcripts carry no auth
    // signal, so their own label is accepted here on the assumption that the
    // CLI is signed in rather than driven by an API key.
    private static bool CountsTowardPlan(string planProviderId, PricedModelRow row)
    {
        return planProviderId switch
        {
            "codex" => row.ViewModel.AuthProvider is "OpenAI (Codex)" or "OpenAI (ChatGPT OAuth)",
            "claude" => row.ViewModel.AuthProvider is "Anthropic (Claude Code)",
            "droid" => row.ViewModel.Source == "Droid",
            "copilot" => row.ViewModel.AuthProvider == "GitHub Copilot",
            _ => false
        };
    }

    private static string ProviderName(string id)
    {
        return id.ToLowerInvariant() switch
        {
            "codex" => "Codex", 
            "claude" => "Claude Code", 
            "opencode" => "OpenCode", 
            "copilot" => "GitHub Copilot",
            "amp" => "Amp",
            "droid" => "Droid",
            "cursor" => "Cursor",
            _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(id), 
        };
    }

    private static string Countdown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return L("Countdown_Now");
        }
        if (!(duration.TotalDays >= 1.0))
        {
            if (!(duration.TotalHours >= 1.0))
            {
                return F("Countdown_Minutes", Math.Max(1, duration.Minutes));
            }
            return F("Countdown_HoursMinutes", (int)duration.TotalHours, duration.Minutes);
        }
        return F("Countdown_DaysHours", (int)duration.TotalDays, duration.Hours);
    }

    private static string Age(TimeSpan age)
    {
        if (!(age.TotalDays >= 1.0))
        {
            if (!(age.TotalHours >= 1.0))
            {
                return $"{Math.Max(0, (int)age.TotalMinutes)}m";
            }
            return $"{(int)age.TotalHours}h";
        }
        return $"{(int)age.TotalDays}d";
    }

    private static string LocalizedCapabilities(string providerId) => providerId switch
    {
        "codex" => L("Data_AuthCodex"),
        "claude" => L("Data_AuthClaude"),
        "opencode" => L("Data_AuthOpenCode"),
        "droid" => L("Data_AuthDroid"),
        "copilot" => L("Data_AuthCopilot"),
        "amp" => L("Data_AuthAmp"),
        "cursor" => L("Data_AuthCursor"),
        _ => string.Empty
    };

    private static string LocalizedCoverage(string providerId) => providerId switch
    {
        "codex" => L("Data_CoverageCodex"),
        "claude" => L("Data_CoverageClaude"),
        "opencode" => L("Data_CoverageOpenCode"),
        "droid" => L("Data_CoverageDroid"),
        "copilot" => L("Data_CoverageCopilot"),
        "amp" => L("Data_CoverageAmp"),
        "cursor" => L("Data_CoverageCursor"),
        _ => string.Empty
    };

    private static string L(string key) => LocalizationService.GetString(key);

    private static string F(string key, params object?[] args) =>
        string.Format(CultureInfo.CurrentCulture, L(key), args);

    private static double ParseSortableTokens(string value)
    {
        if (value.EndsWith("B", StringComparison.Ordinal))
        {
            string text = value;
            if (double.TryParse(text.Substring(0, text.Length - 1), CultureInfo.InvariantCulture, out var result))
            {
                return result * 1000000000.0;
            }
        }
        if (value.EndsWith("M", StringComparison.Ordinal))
        {
            string text = value;
            if (double.TryParse(text.Substring(0, text.Length - 1), CultureInfo.InvariantCulture, out var result2))
            {
                return result2 * 1000000.0;
            }
        }
        if (value.EndsWith("K", StringComparison.Ordinal))
        {
            string text = value;
            if (double.TryParse(text.Substring(0, text.Length - 1), CultureInfo.InvariantCulture, out var result3))
            {
                return result3 * 1000.0;
            }
        }
        if (!double.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var result4))
        {
            return 0.0;
        }
        return result4;
    }

    public void Dispose()
    {
        // An in-flight LoadAsync holds the load gate for its entire run,
        // parallel sub-tasks included. Taking the gate first keeps the
        // HttpClients and semaphores below alive until that work has fully
        // unwound; the bounded wait only gives up when cancellation itself
        // is stuck, which disposing anyway cannot make worse.
        try
        {
            _loadGate.Wait(TimeSpan.FromSeconds(10));
        }
        catch (ObjectDisposedException)
        {
        }
        _quotaAlerts.Dispose();
        _refresh.Dispose();
        _catalog.Dispose();
        _cursorHttpClient.Dispose();
        _httpClient.Dispose();
        _usageWriteGate.Dispose();
        _loadGate.Dispose();
    }
}
