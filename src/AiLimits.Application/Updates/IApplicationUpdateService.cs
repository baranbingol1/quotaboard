// SPDX-License-Identifier: Apache-2.0

namespace AiLimits.Application.Updates;

public interface IApplicationUpdateService
{
    bool IsSupported { get; }

    string CurrentVersion { get; }

    AvailableApplicationUpdate? PendingUpdate { get; }

    Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default);

    Task<ApplicationUpdateFailureKind> DownloadAsync(
        IProgress<int> progress,
        CancellationToken cancellationToken = default
    );

    Task<ApplicationUpdateFailureKind> RestartToApplyAsync(CancellationToken cancellationToken = default);
}

public sealed record AvailableApplicationUpdate(string Version, Uri ReleaseNotesUrl, string? ReleaseSummary = null);

public sealed record ApplicationUpdateCheckResult(
    AvailableApplicationUpdate? Update,
    ApplicationUpdateFailureKind Failure = ApplicationUpdateFailureKind.None
);

public enum ApplicationUpdateFailureKind
{
    None,
    Offline,
    RateLimited,
    InvalidMetadata,
    IntegrityFailure,
    Interrupted,
    Busy,
    NotWritable,
    Unsupported,
    Unknown,
}
