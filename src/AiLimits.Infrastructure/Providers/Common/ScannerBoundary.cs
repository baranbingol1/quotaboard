// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;

namespace AiLimits.Infrastructure.Providers.Common;

/// <summary>
/// Shared incremental-scan boundary for local-history token sources. A bare
/// timestamp is not a unique cursor: an event written after a scan finished
/// but stamped with (or before) that scan's newest observed timestamp would be
/// skipped forever by a plain <c>occurredAt &lt;= last</c> filter. Rescanning a
/// short overlap window behind the cursor closes that race; re-emitted overlap
/// events carry the same stable source ids and are dropped by the fingerprint
/// ledger, so the overlap costs a few duplicate upsert attempts per scan and
/// buys exactly-once ingestion. (The Amp source uses its own coarser one-day
/// overlap because thread update times are its only signal.)
/// </summary>
internal static class ScannerBoundary
{
    private static readonly TimeSpan RescanOverlap = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The oldest timestamp a new scan must still read: the previous cursor
    /// minus the overlap margin. Null when there is no prior cursor (read all).
    /// Lets a source push an incremental filter into its own query instead of
    /// reading every historical row and discarding old ones in memory.
    /// </summary>
    internal static DateTimeOffset? RescanFloor(ScannerCursor? cursor) =>
        cursor?.LastObservedAt is { } last ? last - RescanOverlap : null;

    /// <summary>
    /// True when the event is old enough that the previous scan is guaranteed
    /// to have covered it, including the overlap safety margin.
    /// </summary>
    internal static bool AlreadyCovered(ScannerCursor? cursor, DateTimeOffset occurredAt) =>
        RescanFloor(cursor) is { } floor && occurredAt <= floor;
}
