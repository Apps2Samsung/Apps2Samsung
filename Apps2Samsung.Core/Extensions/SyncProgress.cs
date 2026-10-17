using System;

namespace Apps2Samsung.Extensions
{
    /// <summary>
    /// An <see cref="IProgress{T}"/> that runs its handler on the reporting thread, as the
    /// <see cref="ProgressCallback"/> strings already do. <see cref="Progress{T}"/> posts each report
    /// to the synchronization context captured at construction — or, with none, to the thread pool,
    /// where reports can land out of order, which for a percentage means a bar that jumps backwards.
    /// The handler is responsible for its own marshalling (the desktop head posts to the dispatcher).
    /// </summary>
    public sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler)
        {
            _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        }

        public void Report(T value) => _handler(value);
    }
}
