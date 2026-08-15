// SPDX-License-Identifier: Apache-2.0
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using AiLimits.App.Updates;
using AiLimits.Application.Abstractions;
using AiLimits.Application.Refresh;
using AiLimits.Infrastructure.Providers.Common;
using AiLimits.Presentation.WinUI;
using AiLimits.Presentation.WinUI.Localization;
using AiLimits.Presentation.WinUI.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace AiLimits.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private Window? _window;
    private LiveDashboardDataSource? _dataSource;
    private LiveDashboardViewModel? _dashboard;
    private RefreshScheduler? _scheduler;
    private TrayIconService? _trayIcon;
    private UpdateViewModel? _updateViewModel;
    private Task? _startup;
    private Task? _shutdownTask;
    private readonly Queue<Program.ActivationRequest> _pendingActivations = new();
    private readonly object _shutdownLock = new();
    private static readonly ILogger<App> _logger = NullLogger<App>.Instance;

    public App()
    {
        _ = StartupDiagnostics.Register();
        LocalizationService.ApplySavedOverride();
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        bool startMinimized = Program.InitialActivation.KeepHidden;
        MigrateLegacyDataDirectory();
        var dataSource = new LiveDashboardDataSource();
        var dashboard = new LiveDashboardViewModel(dataSource);
        var updateService = new VelopackApplicationUpdateService(() => BeginShutdownAsync(closeWindow: true));
        var updateViewModel = new UpdateViewModel(updateService);
        var startupRegistration = new WindowsStartupRegistration();
        _dataSource = dataSource;
        _dashboard = dashboard;
        _updateViewModel = updateViewModel;
        var window = new MainWindow(dashboard, updateViewModel, startupRegistration);
        _window = window;
        var trayIcon = new TrayIconService(
            window,
            dashboard,
            noteUserInteraction: () => _scheduler?.NoteUserInteraction(),
            requestQuit: () => BeginShutdownAsync(closeWindow: true)
        );
        _trayIcon = trayIcon;
        DispatcherQueue dispatcher = window.DispatcherQueue;
        RefreshScheduler scheduler = null!;
        scheduler = new RefreshScheduler(
            async _ =>
            {
                RefreshOutcome outcome = await RunScheduledRefreshAsync(dispatcher, dashboard, dataSource);
                // A rate-limited provider's Retry-After sets a floor under the
                // next scheduled tick; manual refreshes stay unaffected.
                if (dataSource.LastRetryAfterHint is TimeSpan retryAfterHint)
                {
                    scheduler.NoteRetryAfter(retryAfterHint);
                }
                return outcome;
            },
            IsEnergySaverOn,
            new SystemClock(),
            logger: NullLogger<RefreshScheduler>.Instance
        );
        _scheduler = scheduler;
        window.Activate();
        bool hidden = startMinimized && trayIcon.StartMinimized();
        // Subscribe only after the launch mode is known. Activating the window
        // before a successful hidden start is not a user interaction.
        window.Activated += (_, activation) =>
        {
            if (activation.WindowActivationState != WindowActivationState.Deactivated)
            {
                scheduler.NoteUserInteraction();
            }
        };
        if (!hidden)
        {
            scheduler.NoteUserInteraction();
        }
        Task startup = InitializeAndArmSchedulerAsync(dashboard, dataSource, scheduler);
        _startup = startup;
        window.Closed += (_, _) =>
        {
            _ = BeginShutdownAsync(closeWindow: false);
        };
        DrainPendingActivations();
    }

    internal void HandleRedirectedActivation(Program.ActivationRequest activation)
    {
        if (_trayIcon is null || _scheduler is null)
        {
            _pendingActivations.Enqueue(activation);
            return;
        }
        if (activation.KeepHidden)
        {
            return;
        }

        _trayIcon.OpenWindow();
    }

    private void DrainPendingActivations()
    {
        while (_pendingActivations.TryDequeue(out Program.ActivationRequest activation))
        {
            HandleRedirectedActivation(activation);
        }
    }

    // Ordered teardown: cancel in-flight work, wait (bounded) for the startup
    // pass and the scheduler loop to observe it, and only then dispose the
    // HttpClients/semaphores they were using. The old synchronous handler
    // disposed those immediately under a live refresh, producing
    // ObjectDisposedExceptions on close.
    private Task BeginShutdownAsync(bool closeWindow)
    {
        lock (_shutdownLock)
        {
            return _shutdownTask ??= ShutdownCoreAsync(closeWindow);
        }
    }

    private async Task ShutdownCoreAsync(bool closeWindow)
    {
        _trayIcon?.MarkQuitting();
        _updateViewModel?.CancelOperations();
        _dashboard?.Dispose();
        try
        {
            if (_startup is not null)
            {
                await _startup.WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        try
        {
            if (_scheduler is not null)
            {
                await _scheduler.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
        }
        catch { }
        _dataSource?.Dispose();
        _scheduler?.Dispose();
        _trayIcon?.Dispose();
        if (closeWindow)
        {
            _window?.Close();
        }
        Exit();
    }

    // The cached pass inside InitializeAsync paints last-known data instantly;
    // the scheduler is armed from the outcome of the startup refresh so an
    // offline launch enters the 15/45/120/300s connectivity retry ladder.
    private static async Task InitializeAndArmSchedulerAsync(
        LiveDashboardViewModel dashboard,
        LiveDashboardDataSource dataSource,
        RefreshScheduler scheduler
    )
    {
        scheduler.Start();
        await dashboard.InitializeAsync();
        scheduler.NoteExternalRefresh(dataSource.LastRefreshHadTransientFailure);
        if (dataSource.LastRetryAfterHint is TimeSpan retryAfter)
        {
            scheduler.NoteRetryAfter(retryAfter);
        }
    }

    private static async Task<RefreshOutcome> RunScheduledRefreshAsync(
        DispatcherQueue dispatcher,
        LiveDashboardViewModel dashboard,
        LiveDashboardDataSource dataSource
    )
    {
        var completion = new TaskCompletionSource<RefreshOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool enqueued = dispatcher.TryEnqueue(async () =>
        {
            try
            {
                bool ran = await dashboard.RefreshFromSchedulerAsync();
                completion.TrySetResult(
                    !ran ? RefreshOutcome.Skipped
                    : dataSource.LastRefreshHadTransientFailure ? RefreshOutcome.TransientFailure
                    : RefreshOutcome.Completed
                );
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Scheduled refresh threw an unexpected exception.");
                completion.TrySetResult(RefreshOutcome.TransientFailure);
            }
        });
        if (!enqueued)
        {
            completion.TrySetResult(RefreshOutcome.Skipped);
        }
        return await completion.Task;
    }

    private static bool IsEnergySaverOn()
    {
        try
        {
            return Windows.System.Power.PowerManager.EnergySaverStatus == Windows.System.Power.EnergySaverStatus.On;
        }
        catch
        {
            return false;
        }
    }

    // One-time move of the pre-rebrand "AI Limits" data folder (database,
    // theme, plan costs) to the QuotaBoard location. Must run before anything
    // opens the database. The target may already exist partially (e.g. a
    // tool seeded theme.preference first), so this fills in whatever is
    // missing instead of requiring an empty target.
    private static void MigrateLegacyDataDirectory()
    {
        string root = System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData);
        string legacy = Path.Combine(root, "AI Limits");
        string current = Path.Combine(root, "QuotaBoard");
        if (!Directory.Exists(legacy))
        {
            return;
        }
        try
        {
            if (!Directory.Exists(current))
            {
                Directory.Move(legacy, current);
                return;
            }
            DirectoryCopy.CopyMissing(legacy, current);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Legacy data directory migration failed; the app rebuilds aggregates from local histories."
            );
            // Fresh start: the app rebuilds aggregates from local histories.
        }
    }
}
