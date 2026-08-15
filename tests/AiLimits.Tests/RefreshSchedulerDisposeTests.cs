// SPDX-License-Identifier: Apache-2.0
using AiLimits.Application.Abstractions;
using AiLimits.Application.Refresh;

namespace AiLimits.Tests;

public sealed class RefreshSchedulerDisposeTests
{
    [Fact]
    public async Task DisposeThenNoteUserInteractionDoesNotThrow()
    {
        var scheduler = new RefreshScheduler(
            _ => Task.FromResult(RefreshOutcome.Completed),
            () => false,
            new FixedClock()
        );

        scheduler.Start();
        // Allow the loop to start.
        await Task.Delay(100);
        scheduler.Dispose();

        // Wake() is called inside NoteUserInteraction; after Dispose the
        // semaphore is disposed and must not throw.
        var ex = Record.Exception(() => scheduler.NoteUserInteraction());
        Assert.Null(ex);
    }

    private sealed class FixedClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
}
