// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Application.Refresh;
using Microsoft.Extensions.Logging;

namespace AiLimits.Tests;

public sealed class RefreshSchedulerTransientRetryTests
{
    [Fact]
    public async Task Throwing_tick_advances_transient_retry_attempt()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var scheduler = new RefreshScheduler(
            _ => throw new InvalidOperationException("boom"),
            () => false,
            clock,
            logger: new CapturingLogger<RefreshScheduler>()
        );

        // Pretend a recent interaction and an already-completed external refresh
        // happened 3 minutes ago, so the adaptive delay (2 min) has already
        // elapsed and the first tick fires immediately.
        scheduler.NoteUserInteraction();
        scheduler.NoteExternalRefresh(false);
        clock.Advance(TimeSpan.FromMinutes(3));

        scheduler.Start();
        try
        {
            // Wait for the loop to observe the overdue tick and run the callback.
            await WaitUntilAsync(() => scheduler.TransientRetryAttempt > 0, TimeSpan.FromSeconds(5));
            Assert.True(scheduler.TransientRetryAttempt >= 1);
        }
        finally
        {
            await scheduler.StopAsync();
            scheduler.Dispose();
        }
    }

    [Fact]
    public async Task Throwing_tick_logs_warning()
    {
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var logger = new CapturingLogger<RefreshScheduler>();
        var scheduler = new RefreshScheduler(
            _ => throw new InvalidOperationException("boom"),
            () => false,
            clock,
            logger: logger
        );

        scheduler.NoteUserInteraction();
        scheduler.NoteExternalRefresh(false);
        clock.Advance(TimeSpan.FromMinutes(3));

        scheduler.Start();
        try
        {
            await WaitUntilAsync(() => logger.Warnings.Count > 0, TimeSpan.FromSeconds(5));
            Assert.NotEmpty(logger.Warnings);
        }
        finally
        {
            await scheduler.StopAsync();
            scheduler.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
                return;
            await Task.Delay(50);
        }
    }

    private sealed class MutableClock(DateTimeOffset initial) : IClock
    {
        private DateTimeOffset _utcNow = initial;
        public DateTimeOffset UtcNow => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow += delta;
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel >= LogLevel.Warning)
                Warnings.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
