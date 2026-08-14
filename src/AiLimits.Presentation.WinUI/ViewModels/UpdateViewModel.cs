// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Updates;
using AiLimits.Presentation.WinUI.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AiLimits.Presentation.WinUI.ViewModels;

public sealed partial class UpdateViewModel : ObservableObject, IDisposable
{
    private readonly IApplicationUpdateService _service;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly CancellationTokenSource _shutdown = new();
    private AvailableApplicationUpdate? _availableUpdate;

    private ApplicationUpdateState _state;
    private int _downloadPercentage;
    private string _statusMessage = string.Empty;

    public UpdateViewModel(IApplicationUpdateService service)
    {
        _service = service;
        CurrentVersion = service.CurrentVersion;
        _availableUpdate = service.PendingUpdate;
        State =
            !service.IsSupported ? ApplicationUpdateState.Unsupported
            : _availableUpdate is not null ? ApplicationUpdateState.ReadyToRestart
            : ApplicationUpdateState.Idle;
        StatusMessage = GetStatusMessage(State);
        CheckCommand = new AsyncRelayCommand(CheckAsync, CanCheck);
        DownloadCommand = new AsyncRelayCommand(DownloadAsync, CanDownload);
        RestartCommand = new AsyncRelayCommand(RestartAsync, CanRestart);
    }

    public string CurrentVersion { get; }

    public ApplicationUpdateState State
    {
        get => _state;
        private set => SetProperty(ref _state, value);
    }

    public int DownloadPercentage
    {
        get => _downloadPercentage;
        private set
        {
            if (SetProperty(ref _downloadPercentage, value) && State == ApplicationUpdateState.Downloading)
            {
                StatusMessage = GetStatusMessage(State);
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string? AvailableVersion => _availableUpdate?.Version;

    public Uri? ReleaseNotesUrl => _availableUpdate?.ReleaseNotesUrl;

    public bool IsChecking => State == ApplicationUpdateState.Checking;

    public bool IsDownloading => State == ApplicationUpdateState.Downloading;

    public bool IsBusy => IsChecking || IsDownloading;

    public bool HasFailure => State == ApplicationUpdateState.Failed;

    public bool ShowCheckAction =>
        State is ApplicationUpdateState.Idle or ApplicationUpdateState.UpToDate or ApplicationUpdateState.Failed;

    public bool ShowDownloadAction => State == ApplicationUpdateState.Available;

    public bool ShowRestartAction => State == ApplicationUpdateState.ReadyToRestart;

    public bool HasReleaseNotes => ReleaseNotesUrl is not null;

    public IAsyncRelayCommand CheckCommand { get; }

    public IAsyncRelayCommand DownloadCommand { get; }

    public IAsyncRelayCommand RestartCommand { get; }

    private bool CanCheck() => _service.IsSupported && !IsBusy && State is not ApplicationUpdateState.ReadyToRestart;

    private bool CanDownload() => State == ApplicationUpdateState.Available && !IsBusy;

    private bool CanRestart() => State == ApplicationUpdateState.ReadyToRestart && !IsBusy;

    private async Task CheckAsync()
    {
        if (!await _operationLock.WaitAsync(0, _shutdown.Token))
        {
            return;
        }

        try
        {
            SetState(ApplicationUpdateState.Checking);
            ApplicationUpdateCheckResult result = await _service.CheckAsync(_shutdown.Token);
            if (result.Failure != ApplicationUpdateFailureKind.None)
            {
                SetState(ApplicationUpdateState.Failed);
                return;
            }

            _availableUpdate = result.Update;
            OnPropertyChanged(nameof(AvailableVersion));
            OnPropertyChanged(nameof(ReleaseNotesUrl));
            OnPropertyChanged(nameof(HasReleaseNotes));
            SetState(_availableUpdate is null ? ApplicationUpdateState.UpToDate : ApplicationUpdateState.Available);
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _operationLock.Release();
            NotifyCommands();
        }
    }

    private async Task DownloadAsync()
    {
        if (!await _operationLock.WaitAsync(0, _shutdown.Token))
        {
            return;
        }

        try
        {
            DownloadPercentage = 0;
            SetState(ApplicationUpdateState.Downloading);
            var progress = new Progress<int>(value => DownloadPercentage = Math.Clamp(value, 0, 100));
            ApplicationUpdateFailureKind failure = await _service.DownloadAsync(progress, _shutdown.Token);
            SetState(
                failure == ApplicationUpdateFailureKind.None
                    ? ApplicationUpdateState.ReadyToRestart
                    : ApplicationUpdateState.Failed
            );
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _operationLock.Release();
            NotifyCommands();
        }
    }

    private async Task RestartAsync()
    {
        if (!await _operationLock.WaitAsync(0, _shutdown.Token))
        {
            return;
        }

        try
        {
            ApplicationUpdateFailureKind failure = await _service.RestartToApplyAsync(_shutdown.Token);
            if (failure != ApplicationUpdateFailureKind.None)
            {
                SetState(ApplicationUpdateState.Failed);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        finally
        {
            _operationLock.Release();
            NotifyCommands();
        }
    }

    private void SetState(ApplicationUpdateState state)
    {
        State = state;
        StatusMessage = GetStatusMessage(state);
        OnPropertyChanged(nameof(IsChecking));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(HasFailure));
        OnPropertyChanged(nameof(ShowCheckAction));
        OnPropertyChanged(nameof(ShowDownloadAction));
        OnPropertyChanged(nameof(ShowRestartAction));
        NotifyCommands();
    }

    private string GetStatusMessage(ApplicationUpdateState state) =>
        state switch
        {
            ApplicationUpdateState.Unsupported => L("Update_Development"),
            ApplicationUpdateState.Checking => L("Update_Checking"),
            ApplicationUpdateState.UpToDate => L("Update_Current"),
            ApplicationUpdateState.Available => string.Format(L("Update_Available"), AvailableVersion),
            ApplicationUpdateState.Downloading => string.Format(L("Update_Downloading"), DownloadPercentage),
            ApplicationUpdateState.ReadyToRestart => L("Update_Ready"),
            ApplicationUpdateState.Failed => L("Update_Failure"),
            _ => L("Update_Idle"),
        };

    private void NotifyCommands()
    {
        CheckCommand.NotifyCanExecuteChanged();
        DownloadCommand.NotifyCanExecuteChanged();
        RestartCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        CancelOperations();
        _shutdown.Dispose();
        _operationLock.Dispose();
    }

    public void CancelOperations() => _shutdown.Cancel();

    private static string L(string key) => LocalizationService.GetString(key);
}

public enum ApplicationUpdateState
{
    Unsupported,
    Idle,
    Checking,
    UpToDate,
    Available,
    Downloading,
    ReadyToRestart,
    Failed,
}
