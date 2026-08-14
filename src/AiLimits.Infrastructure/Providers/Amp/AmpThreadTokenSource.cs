// SPDX-License-Identifier: Apache-2.0
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AiLimits.Application.Abstractions;
using AiLimits.Domain;
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Infrastructure.Providers.Amp;

public sealed class AmpThreadTokenSource : ITokenUsageSource, IScanPositionSource, IScanFailureCheckpointSource
{
    private const int PageSize = 20;

    // Safety valve for pathological accounts; a scan visiting this many
    // threads has almost certainly paged far past any real activity window.
    private const int MaxThreadsPerScan = 200;

    /// <summary>
    /// Export attempts a thread gets at one revision before it is written off.
    /// Some threads can never be parsed: Amp wrote `usage` without a `model`
    /// before mid-2026, and this machine still has such a thread from June.
    /// Retrying one of those forever would pin <c>retriesOutstanding</c> true,
    /// which permanently suspends both listing shortcuts and puts every refresh
    /// back on the full ten-page traversal those shortcuts exist to avoid.
    /// </summary>
    private const int MaxRetryAttempts = 3;

    /// <summary>
    /// Direct exports of pending threads that the listing no longer offers.
    /// Unlike the listed path this one has no <see cref="MaxThreadsPerScan"/>
    /// to fall back on, so it needs its own ceiling; the remainder keeps its
    /// marker and is picked up by the next scan.
    /// </summary>
    private const int MaxDirectRetriesPerScan = 20;

    /// <summary>
    /// A thread export is a full conversation transcript, and real ones reach
    /// several MB — one of this machine's is 6.25 MB. The default 4 MB stream
    /// cap flagged those as truncated, which used to abort the entire scan.
    /// This bound still stops a runaway CLI from growing the heap without
    /// limit; it just sits above the range of a legitimate transcript.
    /// </summary>
    internal const int MaxExportChars = 64 * 1024 * 1024;

    private static readonly TimeSpan RescanOverlap = TimeSpan.FromDays(1);
    private readonly IProcessRunner runner;
    private readonly Func<string?> executableResolver;

    private string? _position;
    private bool _hasYieldedEvent;

    public AmpThreadTokenSource(IProcessRunner runner)
        : this(runner, AmpCliStrategy.FindExecutable) { }

    internal AmpThreadTokenSource(IProcessRunner runner, Func<string?> executableResolver)
    {
        this.runner = runner;
        this.executableResolver = executableResolver;
    }

    public string Id => "amp.thread-exports";

    /// <inheritdoc />
    public string? Position => _position;

    public string? FailureCheckpoint => _hasYieldedEvent ? null : _position;

    public async IAsyncEnumerable<TokenUsageEvent> ReadAsync(
        ProviderAccount account,
        ScannerCursor? cursor,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        _hasYieldedEvent = false;
        string? executable = executableResolver();
        if (executable is null)
            yield break;

        DateTimeOffset? cutoff = cursor?.LastObservedAt?.Subtract(RescanOverlap);
        // What we exported last time, keyed by thread. A thread whose `updated`
        // still matches its recorded revision holds nothing we have not already
        // ingested, so it costs one dictionary lookup instead of a 2.4 s
        // subprocess. Without this the cutoff alone re-exported a rolling day
        // of threads — 35 of them on this machine — on every single scan.
        AmpScanState previous = AmpScanState.Parse(cursor?.Position);
        var current = new Dictionary<string, string>(StringComparer.Ordinal);

        // Page through the listing (newest-updated first). Earlier builds
        // fetched only the first 20 threads, permanently skipping history for
        // accounts with more.
        var summaries = new List<AmpThreadSummary>();
        var seenThreadIds = new HashSet<string>(StringComparer.Ordinal);
        bool retriesOutstanding = previous.HasPendingRetries;
        for (int offset = 0; summaries.Count < MaxThreadsPerScan; offset += PageSize)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult listResult = await runner
                .RunAsync(
                    executable,
                    [
                        "threads",
                        "list",
                        "--include-archived",
                        "--limit",
                        PageSize.ToString(CultureInfo.InvariantCulture),
                        "--offset",
                        offset.ToString(CultureInfo.InvariantCulture),
                        "--json",
                    ],
                    TimeSpan.FromSeconds(20),
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (listResult.OutputTruncated)
                throw new TokenScanException("Amp thread listing exceeded the capture limit and was truncated.");
            if (
                listResult.ExitCode != 0
                || !AmpThreadParser.TryParseThreadList(
                    listResult.StandardOutput,
                    out IReadOnlyList<AmpThreadSummary> page
                )
            )
                throw new TokenScanException("Amp thread listing failed.");
            if (page.Count == 0)
            {
                break;
            }

            // New threads created mid-scan shift offset pages; ids dedupe the
            // re-served entries.
            summaries.AddRange(page.Where(thread => seenThreadIds.Add(thread.Id)));
            if (page.Count < PageSize)
            {
                break;
            }
            // Once an entire page is older than the rescan cutoff, deeper pages
            // cannot contain activity the cursor has not already covered.
            if (
                !retriesOutstanding
                && cutoff.HasValue
                && page.All(thread => thread.UpdatedAt.HasValue && thread.UpdatedAt < cutoff)
            )
                break;
            // Likewise once an entire page is already at its recorded revision:
            // the listing is ordered newest-updated first, so everything deeper
            // is older still and equally covered. This is what keeps a steady
            // state to a single page instead of six. It is suspended while any
            // thread still owes a retry, because that thread may sit on a page
            // this shortcut would never reach.
            if (!retriesOutstanding && page.All(previous.IsUnchanged))
                break;
        }

        // Carry forward the recorded revision of every thread still in the
        // listing, not just the ones inside the cutoff window. Dropping the
        // older ones would make the page-break above fail on the next scan —
        // a page of old threads would no longer read as fully covered — and
        // the listing would go back to costing every page.
        foreach (AmpThreadSummary thread in summaries)
        {
            if (!previous.TryGetRecorded(thread.Id, out string? recorded))
            {
                continue;
            }
            current[thread.Id] = recorded;
        }

        // Offset pages are not a stable snapshot: insertion and deletion can
        // move a thread across an already-read boundary. Never interpret an
        // unseen pending id as proof that it disappeared.
        foreach (string pendingThreadId in previous.PendingRetryThreadIds)
        {
            if (previous.TryGetRecorded(pendingThreadId, out string? retryMarker) && retryMarker is not null)
            {
                current.TryAdd(pendingThreadId, retryMarker);
            }
        }

        var allUsage = new List<AmpThreadUsage>();
        int exported = 0;
        // Failures are counted per path. Only the listed path speaks to whether
        // this scan learned anything: the direct path below re-attempts threads
        // that were already known to be failing, so its failures must not be
        // able to turn a quiet scan into a reported provider failure.
        int listedFailures = 0;
        int directFailures = 0;
        int unchanged = 0;
        var attemptedThreadIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (
            AmpThreadSummary thread in summaries
                .Where(thread =>
                    previous.IsPendingRetry(thread.Id)
                    || !cutoff.HasValue
                    || !thread.UpdatedAt.HasValue
                    || thread.UpdatedAt >= cutoff
                )
                .Take(MaxThreadsPerScan)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            attemptedThreadIds.Add(thread.Id);
            if (previous.IsUnchanged(thread))
            {
                // Preserve whatever proved this thread covered. For a real
                // revision that is the same string either way; for a thread
                // written off after spending its retry budget, overwriting it
                // with the revision would quietly promote an export we could
                // never read into a successful ingestion watermark.
                current[thread.Id] =
                    previous.TryGetRecorded(thread.Id, out string? covered) && covered is not null
                        ? covered
                        : AmpScanState.Revision(thread);
                unchanged++;
                continue;
            }

            ProcessResult exportResult = await runner
                .RunAsync(
                    executable,
                    ["threads", "export", thread.Id],
                    TimeSpan.FromSeconds(30),
                    MaxExportChars,
                    cancellationToken
                )
                .ConfigureAwait(false);

            // One unreadable thread must not discard the whole scan. Before
            // this, a single oversized export threw, the scan loop caught it,
            // the cursor was never saved, and the same ~77 s of subprocess work
            // was repeated and thrown away on every refresh — permanently.
            //
            // Exit code is tested first, and deliberately: a CLI that fails
            // prints a diagnostic rather than JSON, so it would also fail the
            // parse below and be written off as permanent. Only output that a
            // *successful* command produced can be called undecodable.
            if (exportResult.ExitCode != 0)
            {
                // Process-level failure (CLI hiccup, transient auth). Mark it as
                // owing a retry rather than leaving it unrecorded: an unrecorded
                // thread stops the *page* it sits on from reading as covered,
                // but a page ahead of it can still cut the listing short, and
                // the thread would then never be offered again until Amp
                // happened to touch it.
                listedFailures++;
                current[thread.Id] = NextRetryValue(previous, thread.Id, AmpScanState.Revision(thread));
                continue;
            }
            if (
                exportResult.OutputTruncated
                || !AmpThreadParser.TryParseUsage(
                    thread.Id,
                    exportResult.StandardOutput,
                    previous.IsPendingRetry(thread.Id) ? null : cutoff,
                    out IReadOnlyList<AmpThreadUsage> usage
                )
            )
            {
                // A later QuotaBoard parser may understand this output, and a
                // truncated or temporarily malformed export may recover, so this
                // is retried rather than recorded as a revision. It is still
                // bounded: after MaxRetryAttempts the thread is written off with
                // a marker that is distinct from a real revision, so it never
                // reads as successfully ingested and a future parser can find it.
                listedFailures++;
                current[thread.Id] = NextRetryValue(previous, thread.Id, AmpScanState.Revision(thread));
                continue;
            }
            allUsage.AddRange(usage);
            current[thread.Id] = AmpScanState.Revision(thread);
            exported++;
        }

        // A retry may sit beyond the normal 200-thread traversal, or shift
        // behind an offset boundary while pages are being read. Export those
        // ids directly so the safety cap cannot starve recovery indefinitely.
        // Without a listing revision, a successful retry is left unrecorded;
        // its stable event ids make replay safe, and a future thread update
        // brings it back into the normal overlap window.
        foreach (
            string pendingThreadId in previous
                .PendingRetryThreadIds.Where(id => !attemptedThreadIds.Contains(id))
                .Take(MaxDirectRetriesPerScan)
        )
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProcessResult retryResult = await runner
                .RunAsync(
                    executable,
                    ["threads", "export", pendingThreadId],
                    TimeSpan.FromSeconds(30),
                    MaxExportChars,
                    cancellationToken
                )
                .ConfigureAwait(false);
            if (
                retryResult.ExitCode != 0
                || retryResult.OutputTruncated
                || !AmpThreadParser.TryParseUsage(
                    pendingThreadId,
                    retryResult.StandardOutput,
                    null,
                    out IReadOnlyList<AmpThreadUsage> retryUsage
                )
            )
            {
                directFailures++;
                int attempts = previous.RetryAttempts(pendingThreadId, revision: null) + 1;
                if (attempts >= MaxRetryAttempts)
                {
                    // Unlisted and still unexportable after a full budget: most
                    // likely deleted server-side. There is no revision to write
                    // off against, so drop it. If it ever returns to the listing
                    // it is treated as new and exported like any other thread.
                    current.Remove(pendingThreadId);
                }
                else
                {
                    current[pendingThreadId] = AmpScanState.Retry(attempts, previous.RetryRevisionOf(pendingThreadId));
                }
                continue;
            }

            allUsage.AddRange(retryUsage);
            current.Remove(pendingThreadId);
            exported++;
        }

        // Successful revisions are retained only for listed threads. Pending
        // markers are deliberately retained until their direct export succeeds.
        // Publish before the failure check so a scan with no yielded events can
        // safely preserve its retry progress.
        _position = AmpScanState.Serialize(current);

        if (exported == 0 && unchanged == 0 && listedFailures > 0)
        {
            // Every thread we actually attempted failed and none were already
            // covered: nothing was learned, which is a real failure worth
            // surfacing. A partial failure is not — that is the whole point of
            // the per-thread handling above. Direct-path failures are excluded
            // deliberately: those threads were already known to be failing, so
            // on a scan whose listed window happens to be empty they would
            // otherwise report the whole provider as broken on every refresh.
            throw new TokenScanException($"Amp could not export any of {listedFailures + directFailures} thread(s).");
        }

        foreach (AmpThreadUsage usage in allUsage.OrderBy(item => item.OccurredAt))
        {
            // From this point the source position can cover events that the
            // caller has not committed yet. A downstream persistence failure
            // must therefore fall back to the scan's starting position.
            _hasYieldedEvent = true;
            yield return new TokenUsageEvent(
                account.Key,
                new ServiceProviderId("amp"),
                usage.Model,
                usage.OccurredAt,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheWriteTokens,
                0,
                usage.SourceEventId,
                ProjectIdentity.Unknown
            );
        }
    }

    /// <summary>
    /// Advances a thread's retry budget by one failed attempt, writing it off
    /// once the budget is spent so that a thread which can never be exported
    /// cannot keep the listing shortcuts suspended for good.
    /// </summary>
    private static string NextRetryValue(AmpScanState previous, string threadId, string revision)
    {
        int attempts = previous.RetryAttempts(threadId, revision) + 1;
        return attempts >= MaxRetryAttempts ? AmpScanState.GaveUp(revision) : AmpScanState.Retry(attempts, revision);
    }
}

/// <summary>
/// Per-thread export watermark, persisted opaquely in
/// <see cref="ScannerCursor.Position"/>: thread id → the <c>updated</c> stamp
/// at which we last processed it. A thread still sitting at its recorded
/// revision is skipped without spawning the CLI.
/// </summary>
internal sealed class AmpScanState
{
    private const string LegacyPosition = "incremental";

    /// <summary>
    /// Recorded in place of a revision for a thread whose export failed for a
    /// reason that may not recur. It never compares equal to a real revision,
    /// so the thread is re-attempted, and its presence keeps the listing paging
    /// far enough to reach it. Written as <c>!retry:attempts:revision</c>; the
    /// bare form is the legacy value and counts as one attempt already spent.
    /// </summary>
    public const string RetryMarker = "!retry";

    /// <summary>
    /// Recorded once a thread has spent its retry budget at one revision. It is
    /// deliberately not a revision — an export we could not read must never
    /// masquerade as successfully ingested, and a future parser can still find
    /// these — but it does read as covered, so it stops costing an export every
    /// scan and stops holding the listing shortcuts open. A new revision clears
    /// it and the thread gets a fresh budget.
    /// </summary>
    private const string GaveUpPrefix = "!gaveup:";

    public static string Retry(int attempts, string revision) =>
        RetryMarker + ":" + attempts.ToString(CultureInfo.InvariantCulture) + ":" + revision;

    public static string GaveUp(string revision) => GaveUpPrefix + revision;

    /// <summary>
    /// Splits a retry marker into the attempts already spent and the revision
    /// they were spent at. The revision is an ISO-8601 stamp and contains its
    /// own colons, so only the first two separators are significant.
    /// </summary>
    private static bool TryParseRetry(string value, out int attempts, out string revision)
    {
        attempts = 0;
        revision = string.Empty;
        if (!value.StartsWith(RetryMarker, StringComparison.Ordinal))
            return false;
        if (value.Length == RetryMarker.Length)
        {
            attempts = 1;
            return true;
        }
        if (value[RetryMarker.Length] != ':')
            return false;

        string rest = value[(RetryMarker.Length + 1)..];
        int separator = rest.IndexOf(':', StringComparison.Ordinal);
        string attemptsText = separator < 0 ? rest : rest[..separator];
        revision = separator < 0 ? string.Empty : rest[(separator + 1)..];
        if (
            !int.TryParse(attemptsText, NumberStyles.Integer, CultureInfo.InvariantCulture, out attempts)
            || attempts < 1
        )
        {
            attempts = 1;
        }
        return true;
    }

    private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions(
        JsonSerializerDefaults.Web
    );

    private readonly Dictionary<string, string> _threads;

    private AmpScanState(Dictionary<string, string> threads) => _threads = threads;

    /// <summary>
    /// A thread with no <c>updated</c> stamp has no revision to compare, so it
    /// is never considered unchanged — correctness beats the saved subprocess.
    /// </summary>
    public bool IsUnchanged(AmpThreadSummary thread)
    {
        if (!thread.UpdatedAt.HasValue || !_threads.TryGetValue(thread.Id, out string? recorded))
        {
            return false;
        }
        string revision = Revision(thread);
        // A thread written off at this exact revision is covered in the sense
        // that matters here: there is nothing further to try until Amp changes
        // it, so it must not keep costing an export or hold the shortcuts open.
        return string.Equals(recorded, revision, StringComparison.Ordinal)
            || string.Equals(recorded, GaveUp(revision), StringComparison.Ordinal);
    }

    /// <summary>True while some thread is recorded as owing a retry.</summary>
    public bool HasPendingRetries =>
        _threads.Values.Any(value => value.StartsWith(RetryMarker, StringComparison.Ordinal));

    public IEnumerable<string> PendingRetryThreadIds =>
        _threads.Where(pair => pair.Value.StartsWith(RetryMarker, StringComparison.Ordinal)).Select(pair => pair.Key);

    public bool IsPendingRetry(string threadId) =>
        _threads.TryGetValue(threadId, out string? recorded)
        && recorded.StartsWith(RetryMarker, StringComparison.Ordinal);

    /// <summary>
    /// Attempts already spent on <paramref name="threadId"/>. A marker recorded
    /// against a different revision has been superseded by new content, which
    /// earns a fresh budget; pass <c>null</c> to accept whatever was recorded.
    /// </summary>
    public int RetryAttempts(string threadId, string? revision)
    {
        if (
            !_threads.TryGetValue(threadId, out string? recorded)
            || !TryParseRetry(recorded, out int attempts, out string recordedRevision)
        )
        {
            return 0;
        }
        return revision is not null && !string.Equals(recordedRevision, revision, StringComparison.Ordinal)
            ? 0
            : attempts;
    }

    public string RetryRevisionOf(string threadId) =>
        _threads.TryGetValue(threadId, out string? recorded) && TryParseRetry(recorded, out _, out string revision)
            ? revision
            : string.Empty;

    public bool TryGetRecorded(string threadId, out string? revision) => _threads.TryGetValue(threadId, out revision);

    public static string Revision(AmpThreadSummary thread) =>
        thread.UpdatedAt?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    /// <summary>
    /// Unparseable or legacy state ("incremental", written before per-thread
    /// tracking existed) degrades to an empty watermark: one full rescan, then
    /// the state is written in the new shape.
    /// </summary>
    public static AmpScanState Parse(string? position)
    {
        if (string.IsNullOrWhiteSpace(position) || string.Equals(position, LegacyPosition, StringComparison.Ordinal))
        {
            return new AmpScanState([]);
        }
        try
        {
            Dictionary<string, string>? threads = JsonSerializer.Deserialize<Dictionary<string, string>>(
                position,
                SerializerOptions
            );
            return new AmpScanState(
                threads is null ? [] : new Dictionary<string, string>(threads, StringComparer.Ordinal)
            );
        }
        catch (JsonException)
        {
            return new AmpScanState([]);
        }
    }

    public static string Serialize(Dictionary<string, string> threads) =>
        JsonSerializer.Serialize(threads, SerializerOptions);
}

internal sealed record AmpThreadSummary(string Id, DateTimeOffset? UpdatedAt);

internal sealed record AmpThreadUsage(
    string Model,
    DateTimeOffset OccurredAt,
    long InputTokens,
    long OutputTokens,
    long CacheReadTokens,
    long CacheWriteTokens,
    string SourceEventId
);

internal static class AmpThreadParser
{
    internal static bool TryParseThreadList(string? raw, out IReadOnlyList<AmpThreadSummary> threads)
    {
        threads = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw ?? "");
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return false;

            var parsed = new List<AmpThreadSummary>();
            foreach (JsonElement item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || ReadString(item, "id") is not { } id)
                    continue;
                DateTimeOffset? updatedAt =
                    ReadString(item, "updated") is { } updated
                    && DateTimeOffset.TryParse(
                        updated,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset timestamp
                    )
                        ? timestamp
                        : null;
                parsed.Add(new AmpThreadSummary(id, updatedAt));
            }
            threads = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static bool TryParseUsage(
        string threadId,
        string? raw,
        DateTimeOffset? cutoff,
        out IReadOnlyList<AmpThreadUsage> parsedUsage
    )
    {
        parsedUsage = [];
        try
        {
            using JsonDocument document = JsonDocument.Parse(raw ?? "");
            if (
                !document.RootElement.TryGetProperty("messages", out JsonElement messages)
                || messages.ValueKind != JsonValueKind.Array
            )
                return false;

            var parsed = new List<AmpThreadUsage>();
            foreach (JsonElement message in messages.EnumerateArray())
            {
                if (
                    message.ValueKind != JsonValueKind.Object
                    || !message.TryGetProperty("usage", out JsonElement usage)
                    || usage.ValueKind != JsonValueKind.Object
                )
                    continue;

                long input = ReadLong(usage, "inputTokens");
                long output = ReadLong(usage, "outputTokens");
                long cacheRead = ReadLong(usage, "cacheReadInputTokens");
                long cacheWrite = ReadLong(usage, "cacheCreationInputTokens");
                if (input + output + cacheRead + cacheWrite == 0)
                    continue;

                if (
                    ReadString(usage, "model") is not { } model
                    || ReadString(usage, "timestamp") is not { } timestampText
                    || !DateTimeOffset.TryParse(
                        timestampText,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                        out DateTimeOffset occurredAt
                    )
                )
                    return false;
                if (cutoff.HasValue && occurredAt < cutoff.Value)
                    continue;

                string? messageId = ReadString(message, "protocolMessageID") ?? ReadIdentifier(message, "messageId");
                if (string.IsNullOrWhiteSpace(messageId))
                    return false;
                parsed.Add(
                    new AmpThreadUsage(
                        model,
                        occurredAt,
                        input,
                        output,
                        cacheRead,
                        cacheWrite,
                        $"amp:{threadId}:{messageId}"
                    )
                );
            }
            parsedUsage = parsed.OrderBy(item => item.OccurredAt).ToArray();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()
            : null;

    private static string? ReadIdentifier(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            return null;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
    }

    private static long ReadLong(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement value) && value.TryGetInt64(out long result)
            ? Math.Max(0, result)
            : 0;
}
