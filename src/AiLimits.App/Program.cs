// SPDX-License-Identifier: Apache-2.0
using System.Collections.Concurrent;
using System.Threading;
using AiLimits.Presentation.WinUI;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;
using Velopack;
using Windows.ApplicationModel.Activation;

namespace AiLimits.App;

internal static class Program
{
    private const string InstanceKey = "QuotaBoard.Primary";
    private static readonly object Gate = new();
    private static readonly ConcurrentQueue<ActivationRequest> PendingActivations = new();
    private static DispatcherQueue? _dispatcher;
    private static App? _app;
    private static AppInstance? _primaryInstance;

    internal static ActivationRequest InitialActivation { get; private set; }
    internal static bool IsolatedEmptyState { get; private set; }

    [STAThread]
    private static void Main()
    {
        string? isolatedRoot = ReadIsolatedRoot(Environment.GetCommandLineArgs().Skip(1).ToArray());
        if (isolatedRoot is not null)
        {
            AppDataDirectory.UseIsolatedRoot(isolatedRoot);
            IsolatedEmptyState = true;
        }
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        RunAsync().GetAwaiter().GetResult();
    }

    private static async Task RunAsync()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        InitialActivation = ActivationRequest.From(activation, includeProcessArguments: true);
        AppInstance primary = AppInstance.FindOrRegisterForKey(
            IsolatedEmptyState ? InstanceKey + ".IsolatedE2E" : InstanceKey
        );
        if (!primary.IsCurrent)
        {
            await primary.RedirectActivationToAsync(activation);
            return;
        }

        _primaryInstance = primary;
        primary.Activated += OnActivated;
        Microsoft.UI.Xaml.Application.Start(_ =>
        {
            DispatcherQueue dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(dispatcher));
            var app = new App();
            Attach(app, dispatcher);
        });
        GC.KeepAlive(_primaryInstance);
    }

    private static void OnActivated(object? sender, AppActivationArguments activation)
    {
        ActivationRequest request = ActivationRequest.From(activation, includeProcessArguments: false);
        DispatcherQueue? dispatcher;
        App? app;
        lock (Gate)
        {
            dispatcher = _dispatcher;
            app = _app;
            if (dispatcher is null || app is null)
            {
                PendingActivations.Enqueue(request);
                return;
            }
        }

        dispatcher.TryEnqueue(() => app.HandleRedirectedActivation(request));
    }

    private static void Attach(App app, DispatcherQueue dispatcher)
    {
        lock (Gate)
        {
            _app = app;
            _dispatcher = dispatcher;
        }

        while (PendingActivations.TryDequeue(out ActivationRequest request))
        {
            dispatcher.TryEnqueue(() => app.HandleRedirectedActivation(request));
        }
    }

    private static string? ReadIsolatedRoot(IReadOnlyList<string> arguments)
    {
        const string option = "--e2e-clean-state";
        int index = -1;
        for (int candidate = 0; candidate < arguments.Count; candidate++)
        {
            if (!string.Equals(arguments[candidate], option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            if (index >= 0)
            {
                throw new ArgumentException($"{option} can be specified only once.");
            }
            index = candidate;
        }
        if (index < 0)
        {
            return null;
        }
        if (index + 1 >= arguments.Count || string.IsNullOrWhiteSpace(arguments[index + 1]))
        {
            throw new ArgumentException($"{option} requires a temporary data directory.");
        }
        return arguments[index + 1].Trim('"');
    }

    internal readonly record struct ActivationRequest(bool KeepHidden)
    {
        internal static ActivationRequest From(AppActivationArguments activation, bool includeProcessArguments)
        {
            bool keepHidden = activation.Kind == ExtendedActivationKind.StartupTask;
            if (activation.Data is ILaunchActivatedEventArgs launch)
            {
                keepHidden |= ContainsMinimized(launch.Arguments);
            }
            if (includeProcessArguments)
            {
                keepHidden |= Environment.GetCommandLineArgs().Skip(1).Any(IsMinimizedArgument);
            }
            return new ActivationRequest(keepHidden);
        }

        private static bool ContainsMinimized(string arguments) =>
            arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(IsMinimizedArgument);

        private static bool IsMinimizedArgument(string argument) =>
            string.Equals(argument.Trim('"'), "--minimized", StringComparison.OrdinalIgnoreCase);
    }
}
