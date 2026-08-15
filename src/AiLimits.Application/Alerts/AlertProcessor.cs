// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiLimits.Application.Alerts;

public interface IAlertStateStore
{
    Task<bool> TryClaimAsync(AlertCandidate candidate, DateTimeOffset claimedAt, CancellationToken cancellationToken);

    Task ReleaseAsync(AlertCandidate candidate, CancellationToken cancellationToken);
}

public interface IAlertNotificationSink
{
    /// <summary>Returns true only when the notification was handed to the OS.</summary>
    Task<bool> TryShowAsync(AlertCandidate candidate, CancellationToken cancellationToken);
}

/// <summary>
/// Atomically claims alert events before delivery. Failed deliveries release
/// their claims so a later refresh can retry, while successful deliveries stay
/// deduplicated in durable storage.
/// </summary>
public sealed class AlertProcessor(
    AlertEvaluator evaluator,
    IAlertStateStore stateStore,
    IAlertNotificationSink notificationSink,
    ILogger<AlertProcessor>? logger = null
)
{
    private readonly ILogger<AlertProcessor> _logger = logger ?? NullLogger<AlertProcessor>.Instance;

    public async Task<int> ProcessAsync(
        IEnumerable<ProviderSnapshot> snapshots,
        AlertPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken
    )
    {
        if (!policy.Enabled)
        {
            return 0;
        }

        AlertCandidate[] candidates = snapshots
            .SelectMany(snapshot => evaluator.Evaluate(snapshot, policy, now))
            .DistinctBy(candidate => candidate.DeduplicationKey, StringComparer.Ordinal)
            .ToArray();

        int delivered = 0;
        foreach (AlertCandidate candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool claimed;
            try
            {
                claimed = await stateStore.TryClaimAsync(candidate, now, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(
                    ex,
                    "Alert state claim failed for {DeduplicationKey}; skipping.",
                    candidate.DeduplicationKey
                );
                continue;
            }

            if (!claimed)
            {
                continue;
            }

            bool shown = false;
            try
            {
                shown = await notificationSink.TryShowAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await TryReleaseAsync(candidate, CancellationToken.None).ConfigureAwait(false);
                throw;
            }
            catch (Exception ex)
            {
                // The claim is released below so an unavailable notification
                // platform does not permanently swallow the event.
                _logger.LogWarning(
                    ex,
                    "Alert notification delivery failed for {DeduplicationKey}.",
                    candidate.DeduplicationKey
                );
            }

            if (shown)
            {
                delivered++;
            }
            else
            {
                await TryReleaseAsync(candidate, cancellationToken).ConfigureAwait(false);
            }
        }

        return delivered;
    }

    private async Task TryReleaseAsync(AlertCandidate candidate, CancellationToken cancellationToken)
    {
        try
        {
            await stateStore.ReleaseAsync(candidate, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Alert state release failed for {DeduplicationKey}.", candidate.DeduplicationKey);
        }
    }
}
