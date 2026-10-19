using System;
using System.Diagnostics;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Apps2Samsung.Extensions;
using Apps2Samsung.Interfaces;
using Apps2Samsung.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Apps2Samsung.Views
{
    /// <summary>The debug log viewer window (Settings › Backup and diagnostics).</summary>
    public partial class LogViewerWindow : Window
    {
        private readonly DispatcherTimer _follow = new() { Interval = TimeSpan.FromSeconds(2) };

        public LogViewerWindow()
        {
            InitializeComponent();
        }

        public LogViewerWindow(LogViewerViewModel model) : this()
        {
            DataContext = model;
            _follow.Tick += (_, _) => model.Follow();
            Opened += (_, _) => _follow.Start();
            Closed += (_, _) => _follow.Stop();
        }

        private LogViewerViewModel? Model => DataContext as LogViewerViewModel;

        private void OnOpenFolderClick(object? sender, RoutedEventArgs e) =>
            App.Services.GetRequiredService<AppSettingsViewModel>().OpenLogsFolderCommand.Execute(null);

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (Model is not { } model || Clipboard is null)
                return;
            await Clipboard.SetTextAsync(model.ShownText);
            model.MarkCopied();
        }

        // The whole file, whatever the filter: whoever reads it wants the full transcript.
        private async void OnSaveAsClick(object? sender, RoutedEventArgs e)
        {
            if (Model?.SelectedPath is not { } source || !File.Exists(source))
                return;

            var target = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = Path.GetFileName(source),
                DefaultExtension = "log",
            });
            if (target is null)
                return;

            try
            {
                await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                await using var output = await target.OpenWriteAsync();
                await input.CopyToAsync(output);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[Log] Couldn't save the log: {ex}");
                await App.Services.GetRequiredService<IDialogService>()
                    .ShowErrorAsync(string.Format("statusLogSaveFailed".Localized(), ex.Message));
            }
        }
    }
}
