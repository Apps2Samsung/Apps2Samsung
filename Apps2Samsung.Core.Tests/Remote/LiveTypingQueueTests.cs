using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Apps2Samsung.Remote;

namespace Apps2Samsung.Tests.Remote;

/// <summary>
/// Fast typing used to garble the query because each keystroke started its own key run and the runs
/// interleaved. These pin the queue that fixes it: order is kept, and one keystroke finishes before the
/// next begins.
/// </summary>
public class LiveTypingQueueTests
{
    [Fact]
    public async Task KeystrokesAreSentInOrderAndNeverOverlap()
    {
        var sent = new List<string>();
        var concurrent = 0;
        var maxConcurrent = 0;

        await using var queue = new LiveTypingQueue(async (key, ct) =>
        {
            var now = Interlocked.Increment(ref concurrent);
            maxConcurrent = System.Math.Max(maxConcurrent, now);
            await Task.Delay(5, ct);          // stand-in for a run of key presses
            lock (sent) sent.Add(key);
            Interlocked.Decrement(ref concurrent);
        });

        foreach (var character in "hello")
            queue.Enqueue(character.ToString());

        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "h", "e", "l", "l", "o" }, sent);
        Assert.Equal(1, maxConcurrent);       // never two runs at once
    }

    [Fact]
    public async Task NothingIsDroppedOrMerged()
    {
        var sent = new List<string>();
        await using var queue = new LiveTypingQueue((key, _) =>
        {
            lock (sent) sent.Add(key);
            return Task.CompletedTask;
        });

        // A fast burst including control keys, as the keyboard handler produces them.
        foreach (var key in new[] { "a", "b", " ", "c", "\b", "\r" })
            queue.Enqueue(key);

        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "a", "b", " ", "c", "\b", "\r" }, sent);
    }

    /// <summary>
    /// A keystroke whose send throws (TV asleep, channel dropped mid-run) must not end the pump: while it
    /// only caught cancellation, one failure left every later keystroke queued forever.
    /// </summary>
    [Fact]
    public async Task AFailedKeystrokeDoesNotStopThePump()
    {
        var sent = new List<string>();
        var failedOnce = false;

        await using var queue = new LiveTypingQueue((key, _) =>
        {
            if (!failedOnce)
            {
                failedOnce = true;
                throw new InvalidOperationException("the TV went away");
            }

            lock (sent) sent.Add(key);
            return Task.CompletedTask;
        });

        queue.Enqueue("a");
        queue.Enqueue("b");
        queue.Enqueue("c");

        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "b", "c" }, sent);
    }

    /// <summary>
    /// Smoke test for enqueue-during-dispose. The guarantee itself is structural — Enqueue's completed check
    /// and its release happen under the same lock Dispose flips the flag in, so a release can never meet a
    /// disposed semaphore. The window is too narrow to hit reliably from the public API (Dispose sets the
    /// flag before it awaits anything), so this documents intent rather than proving it.
    /// </summary>
    [Fact]
    public async Task EnqueueWhileDisposingDoesNotThrow()
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var queue = new LiveTypingQueue((_, _) => Task.CompletedTask);
            var go = new TaskCompletionSource();

            var enqueuer = Task.Run(async () =>
            {
                await go.Task;
                for (var i = 0; i < 20; i++)
                    queue.Enqueue("x");
            });

            go.SetResult();
            var disposed = queue.DisposeAsync().AsTask();
            var threw = await Record.ExceptionAsync(() => Task.WhenAll(enqueuer, disposed));

            Assert.Null(threw);
            await queue.DisposeAsync();   // idempotent
        }
    }

    [Fact]
    public async Task EmptyKeystrokesAreIgnored()
    {
        var sent = new List<string>();
        await using var queue = new LiveTypingQueue((key, _) =>
        {
            lock (sent) sent.Add(key);
            return Task.CompletedTask;
        });

        queue.Enqueue(string.Empty);
        queue.Enqueue("x");
        await queue.DrainAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(new[] { "x" }, sent);
    }
}
