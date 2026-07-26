// SPDX-License-Identifier: Apache-2.0
using AiLimits.Infrastructure.Providers.Common;

namespace AiLimits.Tests;

public sealed class ProcessRunnerTimeoutTests
{
    [Fact]
    public async Task TimeoutKillsProcessAndThrowsOperationCanceledWithoutUnobservedException()
    {
        Exception? unobserved = null;
        EventHandler<UnobservedTaskExceptionEventArgs> handler = (_, args) =>
        {
            unobserved = args.Exception;
            args.SetObserved();
        };
        TaskScheduler.UnobservedTaskException += handler;
        try
        {
            var runner = new ProcessRunner();

            // ping with a very long timeout on a nonexistent host would take
            // too long; instead use a simple timeout process. On Windows,
            // ping -n 60 waits ~60 seconds, far beyond our 200ms timeout.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                runner.RunAsync("ping", ["/n", "60", "127.0.0.1"], TimeSpan.FromMilliseconds(200), default));

            // Force GC + finalizers to surface any unobserved task exceptions
            // from the abandoned ReadToEndAsync tasks.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            Assert.Null(unobserved);
        }
        finally
        {
            TaskScheduler.UnobservedTaskException -= handler;
        }
    }
}
