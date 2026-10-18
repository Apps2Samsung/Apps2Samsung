using Apps2Samsung.ViewModels;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using System.Collections.Specialized;

namespace Apps2Samsung.Views
{
    public partial class DebugConsoleWindow : Window
    {
        private readonly DebugConsoleViewModel? _vm;
        private bool _scrollQueued;
        private bool _closingForReal;

        public DebugConsoleWindow(DebugConsoleViewModel vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            vm.OnRequestClose += Close;
            vm.Rows.CollectionChanged += OnRowsChanged;

            // Attach once the window is up so the user sees the "Attaching…" state while the TV
            // restarts the app, rather than a frozen launcher.
            Opened += async (_, _) => await vm.AttachAsync();

            // Closing has to wait for the tunnel to come down, and Avalonia's Closing is synchronous:
            // cancel the first attempt, detach, then close for real.
            Closing += async (_, e) =>
            {
                if (_closingForReal || vm.IsDetached)
                    return;

                e.Cancel = true;
                await vm.DetachAsync();
                _closingForReal = true;
                Close();
            };
        }

        // Parameterless ctor for the XAML designer.
        public DebugConsoleWindow() => InitializeComponent();

        private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.Action != NotifyCollectionChangedAction.Add)
                return;

            // An app can log in bursts; one scroll per dispatcher turn keeps up with the tail without
            // paying for every intermediate line.
            if (_scrollQueued)
                return;
            _scrollQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _scrollQueued = false;
                var count = _vm?.Rows.Count ?? 0;
                if (count > 0)
                    LogList.ScrollIntoView(count - 1);
            }, DispatcherPriority.Background);
        }

        private void OnEvalKeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter || _vm is null)
                return;

            // The TextBox binding only pushes on focus loss by default; take the text as typed.
            if (sender is TextBox box)
                _vm.Expression = box.Text ?? string.Empty;

            if (_vm.EvaluateCommand.CanExecute(null))
                _vm.EvaluateCommand.Execute(null);
            e.Handled = true;
        }
    }
}
