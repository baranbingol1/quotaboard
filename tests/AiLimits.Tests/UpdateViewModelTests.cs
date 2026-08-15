// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Updates;
using AiLimits.Presentation.WinUI.ViewModels;

namespace AiLimits.Tests;

public sealed class UpdateViewModelTests
{
    private static readonly AvailableApplicationUpdate Update = new(
        "0.2.0",
        new Uri("https://github.com/baranbingol1/quotaboard/releases/tag/v0.2.0")
    );

    [Fact]
    public void Construction_does_not_check_the_network()
    {
        var service = new FakeUpdateService();

        using var viewModel = new UpdateViewModel(service);

        Assert.Equal(ApplicationUpdateState.Idle, viewModel.State);
        Assert.Equal(0, service.CheckCalls);
    }

    [Fact]
    public void Unsupported_service_starts_unsupported()
    {
        using var viewModel = new UpdateViewModel(new FakeUpdateService { IsSupported = false });

        Assert.Equal(ApplicationUpdateState.Unsupported, viewModel.State);
        Assert.False(viewModel.CheckCommand.CanExecute(null));
    }

    [Fact]
    public void Pending_update_starts_ready_to_restart()
    {
        using var viewModel = new UpdateViewModel(new FakeUpdateService { PendingUpdate = Update });

        Assert.Equal(ApplicationUpdateState.ReadyToRestart, viewModel.State);
        Assert.True(viewModel.RestartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Check_without_update_moves_to_up_to_date()
    {
        using var viewModel = new UpdateViewModel(new FakeUpdateService());

        await viewModel.CheckCommand.ExecuteAsync(null);

        Assert.Equal(ApplicationUpdateState.UpToDate, viewModel.State);
    }

    [Fact]
    public async Task Check_with_update_moves_to_available()
    {
        using var viewModel = new UpdateViewModel(
            new FakeUpdateService { CheckResult = new ApplicationUpdateCheckResult(Update) }
        );

        await viewModel.CheckCommand.ExecuteAsync(null);

        Assert.Equal(ApplicationUpdateState.Available, viewModel.State);
        Assert.Equal("0.2.0", viewModel.AvailableVersion);
        Assert.True(viewModel.DownloadCommand.CanExecute(null));
        Assert.False(viewModel.RestartCommand.CanExecute(null));
    }

    [Fact]
    public async Task Download_reports_clamped_progress_and_moves_to_ready()
    {
        var service = new FakeUpdateService
        {
            CheckResult = new ApplicationUpdateCheckResult(Update),
            DownloadProgress = 140,
        };
        using var viewModel = new UpdateViewModel(service);
        await viewModel.CheckCommand.ExecuteAsync(null);

        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Equal(ApplicationUpdateState.ReadyToRestart, viewModel.State);
        Assert.Equal(100, viewModel.DownloadPercentage);
        Assert.True(viewModel.RestartCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Operation_failure_is_safe_and_can_retry(bool checkFailure)
    {
        var service = new FakeUpdateService
        {
            CheckResult = checkFailure ? new(null, ApplicationUpdateFailureKind.Offline) : new(Update),
            DownloadFailure = ApplicationUpdateFailureKind.IntegrityFailure,
        };
        using var viewModel = new UpdateViewModel(service);

        await viewModel.CheckCommand.ExecuteAsync(null);
        if (!checkFailure)
        {
            await viewModel.DownloadCommand.ExecuteAsync(null);
        }

        Assert.Equal(ApplicationUpdateState.Failed, viewModel.State);
        Assert.DoesNotContain("Exception", viewModel.StatusMessage, StringComparison.OrdinalIgnoreCase);
        Assert.True(viewModel.CheckCommand.CanExecute(null));
    }

    [Fact]
    public async Task State_changes_notify_derived_properties()
    {
        var service = new FakeUpdateService
        {
            CheckResult = new ApplicationUpdateCheckResult(Update),
            DownloadFailure = ApplicationUpdateFailureKind.IntegrityFailure,
        };
        using var viewModel = new UpdateViewModel(service);
        var changes = new List<(string? Property, ApplicationUpdateState State)>();
        viewModel.PropertyChanged += (_, args) => changes.Add((args.PropertyName, viewModel.State));

        await viewModel.CheckCommand.ExecuteAsync(null);
        await viewModel.DownloadCommand.ExecuteAsync(null);

        Assert.Contains((nameof(UpdateViewModel.IsChecking), ApplicationUpdateState.Checking), changes);
        Assert.Contains((nameof(UpdateViewModel.IsChecking), ApplicationUpdateState.Available), changes);
        Assert.Contains((nameof(UpdateViewModel.IsDownloading), ApplicationUpdateState.Downloading), changes);
        Assert.Contains((nameof(UpdateViewModel.IsDownloading), ApplicationUpdateState.Failed), changes);
        Assert.Contains((nameof(UpdateViewModel.HasFailure), ApplicationUpdateState.Failed), changes);
    }

    [Fact]
    public async Task Duplicate_check_is_blocked()
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new FakeUpdateService { CheckGate = gate.Task };
        using var viewModel = new UpdateViewModel(service);

        Task first = viewModel.CheckCommand.ExecuteAsync(null);
        await Task.Yield();
        Task second = viewModel.CheckCommand.ExecuteAsync(null);
        gate.SetResult();
        await Task.WhenAll(first, second);

        Assert.Equal(1, service.CheckCalls);
    }

    [Fact]
    public async Task Shutdown_cancels_active_check()
    {
        var service = new FakeUpdateService { WaitForCancellation = true };
        using var viewModel = new UpdateViewModel(service);

        Task check = viewModel.CheckCommand.ExecuteAsync(null);
        await Task.Yield();
        viewModel.CancelOperations();
        await check;

        Assert.Equal(1, service.CheckCalls);
    }

    private sealed class FakeUpdateService : IApplicationUpdateService
    {
        public bool IsSupported { get; init; } = true;
        public string CurrentVersion => "1.0.0";
        public AvailableApplicationUpdate? PendingUpdate { get; init; }
        public ApplicationUpdateCheckResult CheckResult { get; init; } = new(null);
        public ApplicationUpdateFailureKind DownloadFailure { get; init; }
        public int DownloadProgress { get; init; } = 50;
        public Task? CheckGate { get; init; }
        public bool WaitForCancellation { get; init; }
        public int CheckCalls { get; private set; }

        public async Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            if (WaitForCancellation)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            if (CheckGate is not null)
            {
                await CheckGate.WaitAsync(cancellationToken);
            }
            return CheckResult;
        }

        public Task<ApplicationUpdateFailureKind> DownloadAsync(
            IProgress<int> progress,
            CancellationToken cancellationToken = default
        )
        {
            progress.Report(DownloadProgress);
            return Task.FromResult(DownloadFailure);
        }

        public Task<ApplicationUpdateFailureKind> RestartToApplyAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(ApplicationUpdateFailureKind.None);
    }
}
