using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.Remote
{
    /// <summary>
    /// Serialises live keystrokes: they are queued and sent **one at a time, in order**.
    /// <para>
    /// Without this, fast typing garbles the result. Each keystroke becomes a run of tens of key presses on
    /// the TV, and the UI can dispatch the next keystroke while the previous run is still going, so two
    /// runs interleave — typing `hello` quickly on the set produced nonsense. A queue is also what makes a
    /// dropped press impossible: nothing is merged or skipped, it simply waits its turn.
    /// </para>
    /// </summary>
    public sealed class LiveTypingQueue : IAsyncDisposable
    {
        private readonly Func<string, CancellationToken, Task> _send;
        private readonly Queue<string> _pending = new();
        private readonly SemaphoreSlim _signal = new(0);
        private readonly CancellationTokenSource _stopping = new();
        private readonly Task _pump;
        private bool _completed;

        /// <summary>Queued **plus in flight** — what <see cref="DrainAsync"/> waits for.</summary>
        private int _outstanding;
        private int _disposeState;

        /// <summary>Guards the completed check and the enqueue+release, against Dispose's flag flip.</summary>
        private readonly object _enqueueGate = new();

        /// <param name="send">What to do with one keystroke. Must be awaited to completion before the next.</param>
        public LiveTypingQueue(Func<string, CancellationToken, Task> send)
        {
            _send = send;
            _pump = Task.Run(PumpAsync);
        }

        /// <summary>How many keystrokes are waiting — for a "typing behind" indicator and for tests.</summary>
        public int PendingCount
        {
            get
            {
                lock (_pending)
                    return _pending.Count;
            }
        }

        /// <summary>Queues one keystroke ("h", " ", "\b", "\r"). Never blocks the caller.</summary>
        public void Enqueue(string keystroke)
        {
            if (string.IsNullOrEmpty(keystroke) || _completed)
                return;

            // The completed check and the release are one critical section, and Dispose flips the flag under
            // the same lock *before* it disposes the semaphore. So the release can never meet a disposed
            // semaphore — the race is removed rather than caught, which also keeps this throw-free for the
            // key handler that calls it.
            lock (_enqueueGate)
            {
                if (_completed)
                    return;

                lock (_pending)
                    _pending.Enqueue(keystroke);

                Interlocked.Increment(ref _outstanding);
                _signal.Release();
            }
        }

        /// <summary>Waits until everything queued so far has been sent.</summary>
        public async Task DrainAsync(CancellationToken cancellationToken = default)
        {
            // Wait for the in-flight counter, not just the queue: an item leaves the queue *before* its
            // key run finishes, so checking the queue alone returns too early.
            while (Volatile.Read(ref _outstanding) > 0)
                await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }

        private async Task PumpAsync()
        {
            var token = _stopping.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    await _signal.WaitAsync(token).ConfigureAwait(false);

                    string keystroke;
                    lock (_pending)
                    {
                        if (_pending.Count == 0)
                        {
                            // A release can outlive its item; keep the counter honest so DrainAsync cannot spin.
                            Interlocked.Decrement(ref _outstanding);
                            continue;
                        }
                        keystroke = _pending.Dequeue();
                    }

                    // One keystroke at a time, in order: this is the whole point.
                    try
                    {
                        await _send(keystroke, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch
                    {
                        // A single failed keystroke (TV asleep, channel dropped mid-run) must not end the
                        // pump: without this catch the loop dies and every later keystroke queues forever.
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _outstanding);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Disposal.
            }
        }

        public async ValueTask DisposeAsync()
        {
            // Concurrent callers race here; only the winner may Cancel and Dispose.
            if (Interlocked.Exchange(ref _disposeState, 1) == 1)
                return;
            _completed = true;
            _stopping.Cancel();
            try
            {
                await _pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            // Safe now: the pump has ended and _completed is set, so nothing can release this.
            _signal.Dispose();
            _stopping.Dispose();
        }
    }
}
