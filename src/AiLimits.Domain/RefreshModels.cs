// SPDX-License-Identifier: Apache-2.0
namespace AiLimits.Domain;

public enum FetchFailureKind
{
    None,
    Authentication,
    Authorization,
    AccountMismatch,
    RateLimited,
    Network,
    Timeout,
    MalformedResponse,
    ProviderChanged,
    Cancelled,
    Unsupported,
    Unknown,
    // Appended after Unknown so persisted numeric values keep their meaning.
    OversizedResponse,
    // A strategy that could not run right now (e.g. a locked local database)
    // but is expected to recover on its own; rendered as retrying, never as
    // sign-in.
    TemporarilyUnavailable
}

public enum FallbackPolicy
{
    Stop,
    TryNextStrategy,
    TryNextOnlyOnAvailabilityFailure
}

public sealed record FetchResult(ProviderSnapshot? Snapshot, FetchFailureKind FailureKind, string SafeMessage, FallbackPolicy FallbackPolicy, TimeSpan Duration, string StrategyId, TimeSpan? RetryAfter = null)
{
    public bool IsSuccess
    {
        get
        {
            if (Snapshot is not null)
            {
                return FailureKind == FetchFailureKind.None;
            }
            return false;
        }
    }

    public static FetchResult Success(ProviderSnapshot snapshot, string strategyId, TimeSpan duration)
    {
        return new FetchResult(snapshot, FetchFailureKind.None, "Success", FallbackPolicy.Stop, duration, strategyId);
    }

    public static FetchResult Failure(FetchFailureKind kind, string safeMessage, FallbackPolicy fallback, string strategyId, TimeSpan duration, TimeSpan? retryAfter = null)
    {
        return new FetchResult(null, kind, safeMessage, fallback, duration, strategyId, retryAfter);
    }
}

public sealed record FetchAttempt(string Id, AccountKey Account, string StrategyId, DateTimeOffset StartedAt, TimeSpan Duration, FetchFailureKind FailureKind, string SafeMessage);

public enum StrategyAvailability
{
    Available,
    NotConfigured,
    TemporarilyUnavailable,
    Unsupported
}

public sealed record StrategyAvailabilityResult(StrategyAvailability Availability, string SafeReason)
{
    public static StrategyAvailabilityResult Ready()
    {
        return new StrategyAvailabilityResult(StrategyAvailability.Available, "Available");
    }
}

public sealed record RefreshRequest(AccountKey Account, long ConfigurationRevision, bool Force = false, string Reason = "scheduled");

public enum RefreshPublicationStatus
{
    Published,
    StaleResultRejected,
    NoStrategyAvailable,
    FailedWithCachedData,
    FailedWithoutData
}

public sealed record RefreshPublication(RefreshPublicationStatus Status, ProviderSnapshot? Snapshot, IReadOnlyList<FetchAttempt> Attempts, long Generation, string SafeMessage, TimeSpan? RetryAfterHint = null);
