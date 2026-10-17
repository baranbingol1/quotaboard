// SPDX-License-Identifier: Apache-2.0
using AiLimits.Domain;

namespace AiLimits.Application.Abstractions;

public interface IAccountRepository
{
    Task<IReadOnlyList<ProviderAccount>> ListAsync(CancellationToken cancellationToken);

    Task<ProviderAccount?> GetAsync(AccountKey key, CancellationToken cancellationToken);

    Task UpsertAsync(ProviderAccount account, CancellationToken cancellationToken);

    Task DeleteAsync(AccountKey key, CancellationToken cancellationToken);
}

public interface ISnapshotRepository
{
    Task<ProviderSnapshot?> GetLatestAsync(
        AccountKey account,
        long configurationRevision,
        CancellationToken cancellationToken
    );

    Task SaveAsync(
        ProviderSnapshot snapshot,
        long generation,
        long configurationRevision,
        CancellationToken cancellationToken
    );

    Task<IReadOnlyList<ProviderSnapshot>> GetHistoryAsync(
        AccountKey account,
        long configurationRevision,
        DateTimeOffset from,
        CancellationToken cancellationToken
    );

    Task RecordAttemptAsync(FetchAttempt attempt, CancellationToken cancellationToken);
}

public interface IUsageAggregateRepository
{
    Task AddEventsAsync(IEnumerable<TokenUsageEvent> events, CancellationToken cancellationToken);

    Task<IReadOnlyList<DailyUsageAggregate>> QueryAsync(
        DateOnly from,
        DateOnly through,
        IReadOnlyCollection<AccountKey>? accounts,
        CancellationToken cancellationToken
    );

    Task<ScannerCursor?> GetCursorAsync(AccountKey account, string sourceId, CancellationToken cancellationToken);

    Task SaveCursorAsync(AccountKey account, ScannerCursor cursor, CancellationToken cancellationToken);
}

public sealed record ScannerCursor(
    string SourceId,
    string? Position,
    DateTimeOffset? LastObservedAt,
    string? Fingerprint
);
