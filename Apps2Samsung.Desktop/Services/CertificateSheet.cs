using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using Apps2Samsung.Certificate;
using Apps2Samsung.Views;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// An open <see cref="CertificateWindow"/>, usable from the installer's threads: every call is
    /// marshalled to the UI thread.
    /// </summary>
    public sealed class CertificateSheet
    {
        private readonly CertificateWindow _window;

        public CertificateSheet(CertificateWindow window)
        {
            _window = window;
        }

        /// <summary>True once the user chose Sign in with Samsung; false on Cancel or close.</summary>
        public Task<bool> SignInChosen => _window.SignInChosen;

        /// <summary>Cancel (or close) while the browser sign-in runs.</summary>
        public event EventHandler? CancelRequested
        {
            add => _window.CancelRequested += value;
            remove => _window.CancelRequested -= value;
        }

        public void SetStage(CertificateStage stage) => Dispatcher.UIThread.Post(() => _window.SetStage(stage));

        public void Report(string message) => Dispatcher.UIThread.Post(() => _window.Report(message));

        public void Fail(string message) => Dispatcher.UIThread.Post(() => _window.Fail(message));

        public Task FinishAsync() => Dispatcher.UIThread.InvokeAsync(_window.FinishAsync);

        public Task CloseAsync() => Dispatcher.UIThread.InvokeAsync(_window.Close).GetTask();
    }
}
