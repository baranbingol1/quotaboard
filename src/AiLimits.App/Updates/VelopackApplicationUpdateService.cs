// SPDX-License-Identifier: Apache-2.0
using System.Net;
using AiLimits.Application.Updates;
using Velopack;
using Velopack.Sources;

namespace AiLimits.App.Updates;

internal sealed class VelopackApplicationUpdateService : IApplicationUpdateService
{
    private const string RepositoryUrl = "https://github.com/baranbingol1/quotaboard";
    private readonly UpdateManager _manager;
    private readonly Func<Task> _shutdown;
    private UpdateInfo? _available;

    public VelopackApplicationUpdateService(Func<Task> shutdown)
    {
        _shutdown = shutdown;
        var source = new GithubSource(RepositoryUrl, string.Empty, prerelease: false, downloader: null);
        _manager = new UpdateManager(source);
    }

    public bool IsSupported => _manager.IsInstalled;

    public string CurrentVersion => _manager.CurrentVersion?.ToString() ?? ThisAssemblyVersion();

    public AvailableApplicationUpdate? PendingUpdate =>
        _manager.UpdatePendingRestart is { } pending ? ToApplicationUpdate(pending) : null;

    public async Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return new(null, ApplicationUpdateFailureKind.Unsupported);
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _available = await _manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            return new(_available is null ? null : ToApplicationUpdate(_available.TargetFullRelease));
        }
        catch (Exception exception)
        {
            return new(null, MapFailure(exception, cancellationToken));
        }
    }

    public async Task<ApplicationUpdateFailureKind> DownloadAsync(
        IProgress<int> progress,
        CancellationToken cancellationToken = default
    )
    {
        if (!IsSupported)
        {
            return ApplicationUpdateFailureKind.Unsupported;
        }
        if (_available is null)
        {
            return ApplicationUpdateFailureKind.InvalidMetadata;
        }

        try
        {
            await _manager.DownloadUpdatesAsync(_available, progress.Report, cancellationToken);
            return ApplicationUpdateFailureKind.None;
        }
        catch (Exception exception)
        {
            return MapFailure(exception, cancellationToken);
        }
    }

    public async Task<ApplicationUpdateFailureKind> RestartToApplyAsync(CancellationToken cancellationToken = default)
    {
        if (!IsSupported)
        {
            return ApplicationUpdateFailureKind.Unsupported;
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            VelopackAsset? pending = _manager.UpdatePendingRestart;
            if (pending is null)
            {
                return ApplicationUpdateFailureKind.InvalidMetadata;
            }

            _manager.WaitExitThenApplyUpdates(pending, silent: false, restart: true, restartArgs: null);
            await _shutdown();
            return ApplicationUpdateFailureKind.None;
        }
        catch (Exception exception)
        {
            return MapFailure(exception, cancellationToken);
        }
    }

    private static AvailableApplicationUpdate ToApplicationUpdate(VelopackAsset asset) =>
        new(asset.Version.ToString(), new Uri($"{RepositoryUrl}/releases/tag/v{asset.Version}"), asset.NotesMarkdown);

    private static ApplicationUpdateFailureKind MapFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException)
        {
            return cancellationToken.IsCancellationRequested
                ? ApplicationUpdateFailureKind.Interrupted
                : ApplicationUpdateFailureKind.Unknown;
        }
        if (exception is UnauthorizedAccessException)
        {
            return ApplicationUpdateFailureKind.NotWritable;
        }
        if (exception is IOException)
        {
            return ApplicationUpdateFailureKind.IntegrityFailure;
        }
        if (exception is HttpRequestException requestException)
        {
            return
                requestException.StatusCode == HttpStatusCode.Forbidden
                || requestException.StatusCode == HttpStatusCode.TooManyRequests
                ? ApplicationUpdateFailureKind.RateLimited
                : ApplicationUpdateFailureKind.Offline;
        }

        string name = exception.GetType().Name;
        if (name.Contains("Lock", StringComparison.OrdinalIgnoreCase))
        {
            return ApplicationUpdateFailureKind.Busy;
        }
        if (
            name.Contains("Checksum", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Integrity", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ApplicationUpdateFailureKind.IntegrityFailure;
        }
        if (
            name.Contains("Feed", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Json", StringComparison.OrdinalIgnoreCase)
        )
        {
            return ApplicationUpdateFailureKind.InvalidMetadata;
        }
        return ApplicationUpdateFailureKind.Unknown;
    }

    private static string ThisAssemblyVersion() =>
        typeof(VelopackApplicationUpdateService).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}
