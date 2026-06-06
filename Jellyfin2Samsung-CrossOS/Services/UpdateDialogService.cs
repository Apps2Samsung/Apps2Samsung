using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Views;
using System;
using System.Threading.Tasks;

namespace Apps2Samsung.Services
{
    /// <summary>
    /// Service for showing update-related dialogs.
    /// </summary>
    public class UpdateDialogService : IUpdateDialogService
    {
        private readonly IDialogService _dialogService;
        private readonly ILocalizationService _localizationService;
        private UpdateDialog? _currentDialog;

        public UpdateDialogService(IDialogService dialogService, ILocalizationService localizationService)
        {
            _dialogService = dialogService;
            _localizationService = localizationService;
        }

        private Window? GetMainWindow()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return desktop.MainWindow;
            return null;
        }

        private string L(string key) => _localizationService.GetString(key);

        /// <inheritdoc />
        public async Task<UpdateDialogChoice> ShowUpdateAvailableDialogAsync(UpdateCheckResult updateInfo)
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null)
                return UpdateDialogChoice.Cancel;

            // Ensure we're on the UI thread
            if (!Dispatcher.UIThread.CheckAccess())
            {
                return await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ShowUpdateAvailableDialogAsync(updateInfo));
            }

            _currentDialog = new UpdateDialog();
            _currentDialog.Initialize(updateInfo);

            var result = await _currentDialog.ShowDialogAsync(mainWindow);
            _currentDialog = null;

            return result;
        }

        /// <inheritdoc />
        public async Task<bool> ShowDownloadProgressAsync(IProgress<int> progress)
        {
            // This is handled within the UpdateDialog itself via the IsDownloading property
            // The progress is reported through the dialog's UpdateProgress method
            await Task.CompletedTask;
            return true;
        }

        /// <inheritdoc />
        public async Task ShowApplyingUpdateMessageAsync()
        {
            var mainWindow = GetMainWindow();
            if (mainWindow == null)
                return;

            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ShowApplyingUpdateMessageAsync());
                return;
            }

            await _dialogService.ShowMessageAsync(
                L("UpdateApplying"),
                L("UpdateApplyingMessage"));
        }

        /// <inheritdoc />
        public async Task ShowUpdateErrorAsync(string errorMessage)
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                await Dispatcher.UIThread.InvokeAsync(async () =>
                    await ShowUpdateErrorAsync(errorMessage));
                return;
            }

            await _dialogService.ShowErrorAsync($"{L("UpdateError")}: {errorMessage}");
        }
    }
}
