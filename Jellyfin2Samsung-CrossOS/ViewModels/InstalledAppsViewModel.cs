using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Lists the apps installed on a TV (via <see cref="ITizenInstallerService.GetInstalledAppsAsync"/>,
    /// which shares the Core parser with the mobile head) and offers a per-app uninstall for
    /// user-removable apps.
    /// </summary>
    public partial class InstalledAppsViewModel : ViewModelBase
    {
        private readonly ITizenInstallerService _installer;
        private readonly IDialogService _dialogService;
        private readonly string _tvIp;

        public string TvLabel { get; }

        public ObservableCollection<InstalledApp> Apps { get; } = new();

        [ObservableProperty]
        private bool isBusy;

        [ObservableProperty]
        private string statusText = string.Empty;

        public event Action? OnRequestClose;

        public InstalledAppsViewModel(ITizenInstallerService installer, IDialogService dialogService, string tvIp, string tvLabel)
        {
            _installer = installer;
            _dialogService = dialogService;
            _tvIp = tvIp;
            TvLabel = tvLabel;
        }

        [RelayCommand]
        private async Task Load()
        {
            IsBusy = true;
            StatusText = "Reading installed apps…";
            try
            {
                var apps = await _installer.GetInstalledAppsAsync(_tvIp);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Apps.Clear();
                    foreach (var a in apps)
                        Apps.Add(a);
                });
                var removable = apps.Count(a => a.IsRemovable);
                StatusText = apps.Count == 0
                    ? "Couldn't read the app list from this TV."
                    : $"{apps.Count} apps · {removable} removable";
            }
            catch (Exception ex)
            {
                StatusText = $"Couldn't read the app list: {ex.Message}";
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task Uninstall(InstalledApp? app)
        {
            if (app is null || IsBusy)
                return;

            var confirm = await _dialogService.ShowConfirmationAsync(
                "Uninstall app",
                $"Remove \"{app.DisplayName}\" from {TvLabel}?\n\n({app.TizenId})",
                "Uninstall", "Cancel");
            if (!confirm)
                return;

            IsBusy = true;
            StatusText = $"Uninstalling {app.DisplayName}…";
            try
            {
                var result = await _installer.UninstallAppAsync(_tvIp, app.TizenId);
                // "failed[132]" = not installed — already gone, treat as success.
                var ok = result.ExitCode == 0 ||
                         (result.Output?.Contains("failed[132]", StringComparison.OrdinalIgnoreCase) ?? false);
                if (!ok)
                {
                    IsBusy = false;
                    await _dialogService.ShowErrorAsync(
                        string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() ?? "Uninstall failed." : result.Error);
                    return;
                }
            }
            catch (Exception ex)
            {
                IsBusy = false;
                await _dialogService.ShowErrorAsync(ex.Message);
                return;
            }

            await Load();
        }

        [RelayCommand]
        private void Close() => OnRequestClose?.Invoke();
    }
}
