// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Application.Diagnostics;
using AiLimits.Domain;

namespace AiLimits.Application.Usage;

/// <summary>
/// What one telemetry sweep did, in numbers the caller can render. The counts
/// are kept apart from any message so the caller owns the wording: a scan that
/// reports 3 of 4 sources with a redacted reason reads differently in each
/// locale, but the arithmetic is the same everywhere.
/// </summary>
public readonly record struct TelemetryScanOutcome(
    int SourceCount,
    int SuccessfulSourceCount,
    int EmittedEventCount,
    string? FirstFailureMessage
)
{
    /// <summary>True when the sweep found usage that was not already recorded.</summary>
    public bool EmittedEvents => EmittedEventCount > 0;

    /// <summary>True when at least one source could not be drained.</summary>
    public bool HasFailure => FirstFailureMessage is not null;
}

/// <summary>
/// Drains every account's local telemetry sources into the usage store on a
/// fixed cadence.
///
/// Two invariants make this worth its own type. A source that fails must be
/// distinguishable from a source that simply had nothing new, or a 401 reads as
/// a healthy scan; and a cursor may only advance past events that actually
/// reached the database, or a failed write silently drops usage forever.
/// </summary>
public sealed class TelemetryScanService(IUsageAggregateRepository usage, IClock clock) : IDisposable
{
    /// <summary>Minimum spacing between sweeps; a refresh inside it is a no-op.</summary>
    public static readonly TimeSpan ScanInterval = TimeSpan.FromMinutes(1L);

    private const int MaxScanConcurrency = 4;

    private const int ScanBatchSize = 512;

    // SQLite takes one writer at a time. Reads scale fine under WAL, but the
    // parallel scan would otherwise have several 512-row insert transactions
    // racing for the write lock and burning the busy timeout.
    private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);

    private DateTimeOffset? _lastScanAt;

    private volatile bool _scanInFlight;

    /// <summary>
    /// True while a sweep is running. A projection taken now has real limits
    /// but incomplete usage totals, and has to say so.
    /// </summary>
    public bool IsScanning => _scanInFlight;

    /// <summary>
    /// Sweeps every source of every account, unless the last sweep is still
    /// inside <see cref="ScanInterval"/>. Returns null when nothing ran, so the
    /// caller can leave its last reported numbers untouched.
    /// </summary>
    /// <param name="onScanStarting">
    /// Invoked once the cadence check has passed and a sweep is really about to
    /// happen — the hook progress reporting hangs off.
    /// </param>
    public async Task<TelemetryScanOutcome?> ScanIfDueAsync(
        IReadOnlyDictionary<ProviderId, IProviderAdapter> adapters,
        IReadOnlyList<ProviderAccount> accounts,
        Action? onScanStarting,
        CancellationToken cancellationToken
    )
    {
        if (_lastScanAt is { } lastScanAt && clock.UtcNow - lastScanAt < ScanInterval)
        {
            return null;
        }
        onScanStarting?.Invoke();
        _scanInFlight = true;
        try
        {
            return await ScanAsync(adapters, accounts, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // A cancelled sweep would otherwise leave the flag set, and a stale
            // "still indexing" caption would outlive the scan.
            _scanInFlight = false;
        }
    }

    private async Task<TelemetryScanOutcome> ScanAsync(
        IReadOnlyDictionary<ProviderId, IProviderAdapter> adapters,
        IReadOnlyList<ProviderAccount> accounts,
        CancellationToken cancellationToken
    )
    {
        // Token sources only read local telemetry, so keep scanning known
        // accounts even when their live credentials are gone (a CLI logout
        // must not make recorded history vanish).
        (ProviderAccount Account, ITokenUsageSource Source)[] work = accounts
            .SelectMany(account =>
                adapters.TryGetValue(account.Key.Provider, out IProviderAdapter adapter)
                    ? adapter.CreateTokenSources(account).Select(source => (Account: account, Source: source))
                    : []
            )
            .ToArray();

        int successful = 0;
        int emitted = 0;
        string? firstFailure = null;
        object failureGate = new object();

        // The sources share nothing: each reads its own logs, database or CLI
        // and writes through the serialised gate below. Running them one after
        // another meant the slowest (Amp, minutes of subprocess launches) held
        // up six others that together take seconds.
        await Parallel
            .ForEachAsync(
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
                            ex is TokenScanException ? ex.Message : ex.GetType().Name
                        );
                        lock (failureGate)
                        {
                            firstFailure ??= message;
                        }
                    }
                }
            )
            .ConfigureAwait(false);

        _lastScanAt = clock.UtcNow;
        return new TelemetryScanOutcome(work.Length, successful, emitted, firstFailure);
    }

    /// <summary>
    /// Drains one telemetry source into the usage store and advances its cursor.
    /// Returns the number of events it emitted.
    /// </summary>
    private async Task<int> ScanSourceAsync(
        ProviderAccount account,
        ITokenUsageSource source,
        CancellationToken cancellationToken
    )
    {
        ScannerCursor? cursor = await usage
            .GetCursorAsync(account.Key, source.Id, cancellationToken)
            .ConfigureAwait(false);
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
        string? SourcePosition() =>
            source is IScanPositionSource stateful ? stateful.Position ?? cursor?.Position : "incremental";

        // What the scan started from, for the failure path below.
        string? StartingPosition() => source is IScanPositionSource ? cursor?.Position : "incremental";

        ScannerCursor Checkpoint(string? position) => new ScannerCursor(source.Id, position, committed, null);

        try
        {
            await foreach (
                TokenUsageEvent item in source.ReadAsync(account, cursor, cancellationToken).ConfigureAwait(false)
            )
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
                ScannerCursor failureCheckpoint = ScanFailureCheckpoint.ResolveCursor(
                    source,
                    source.Id,
                    StartingPosition(),
                    committed
                );
                await WriteCursorAsync(account.Key, failureCheckpoint, cancellationToken).ConfigureAwait(false);
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

    private async Task WriteEventsAsync(IReadOnlyList<TokenUsageEvent> batch, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await usage.AddEventsAsync(batch, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task WriteCursorAsync(AccountKey account, ScannerCursor cursor, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await usage.SaveCursorAsync(account, cursor, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public void Dispose() => _writeGate.Dispose();
}
