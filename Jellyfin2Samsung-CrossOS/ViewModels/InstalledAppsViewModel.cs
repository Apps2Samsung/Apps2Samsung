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
    public partial class InstalledAppsViewModel : ViewModelBase, IDisposable
    {
        private readonly ITizenInstallerService _installer;
        private readonly IDialogService _dialogService;
        private readonly string _tvIp;
        private static readonly System.Net.Http.HttpClient _http = new();
        private static readonly System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);

        public string TvLabel { get; }

        public ObservableCollection<InstalledAppViewModel> Apps { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
        private bool isBusy;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsProgressVisible))]
        private bool isDebugging;

        public bool IsProgressVisible => IsBusy || IsDebugging;

        private IAsyncDisposable? _activeForwardSession;
        private InstalledApp? _debuggedApp;

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
                var iconMap = await Apps2Samsung.Catalog.AppIconResolver.GetIconMapAsync();
                var apps = (await _installer.GetInstalledAppsAsync(_tvIp)).ToList();
                var viewModels = new System.Collections.Generic.List<InstalledAppViewModel>();

                for (int i = 0; i < apps.Count; i++)
                {
                    var a = apps[i];
                    if ((!string.IsNullOrEmpty(a.AppId) && iconMap.TryGetValue(a.AppId, out var iconUrl)) ||
                        iconMap.TryGetValue(a.TizenId, out iconUrl) ||
                        iconMap.TryGetValue(a.DisplayName, out iconUrl) ||
                        iconMap.TryGetValue(a.DisplayName.ToLowerInvariant(), out iconUrl))
                    {
                        apps[i] = a with { IconUrl = iconUrl };
                    }
                    viewModels.Add(new InstalledAppViewModel(apps[i]));
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Apps.Clear();
                    foreach (var a in viewModels)
                        Apps.Add(a);
                });
                
                // Start loading bitmaps in the background
                _ = Task.Run(async () =>
                {
                    foreach (var vm in viewModels)
                    {
                        if (!string.IsNullOrEmpty(vm.App.IconUrl))
                            await vm.LoadIconAsync(_http, _bitmapCache);
                    }
                });

                var removable = apps.Count(a => a.IsRemovable);
                var totalUsed = InstalledApp.FormatSize(apps.Sum(a => a.SizeBytes));
                StatusText = apps.Count == 0
                    ? "Couldn't read the app list from this TV."
                    : $"{apps.Count} apps · {totalUsed} used · {removable} removable";
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
        private async Task Launch(InstalledApp? app)
        {
            if (app is null || IsBusy) return;
            IsBusy = true;
            StatusText = $"Launching {app.DisplayName}…";
            try
            {
                await _installer.LaunchAppAsync(_tvIp, app.TizenId);
                StatusText = $"Launched {app.DisplayName}.";
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task Stop(InstalledApp? app)
        {
            if (app is null || IsBusy) return;
            IsBusy = true;
            StatusText = $"Stopping {app.DisplayName}…";
            try
            {
                await _installer.StopAppAsync(_tvIp, app.TizenId);
                StatusText = $"Stopped {app.DisplayName}.";
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task Debug(InstalledApp? app)
        {
            if (app is null || IsBusy) return;
            IsBusy = true;
            
            try 
            { 
                await _installer.StopAppAsync(_tvIp, app.TizenId); 
            } 
            catch { /* Ignore if it's not running */ }
            
            StatusText = $"Starting debug for {app.DisplayName}…";
            try
            {
                var (port, session) = await _installer.DebugAppAsync(_tvIp, app.TizenId);
                _activeForwardSession = session;
                _debuggedApp = app;
                IsDebugging = true;

                StatusText = $"Debugging {app.DisplayName} on local port {port}…";

                bool opened = false;
                Exception? lastError = null;

                try
                {
                    if (OperatingSystem.IsWindows())
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", "/c start chrome \"chrome://inspect\"") { CreateNoWindow = true });
                    }
                    else if (OperatingSystem.IsMacOS())
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", "-a \"Google Chrome\" \"chrome://inspect\"") { UseShellExecute = false });
                    }
                    else
                    {
                        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("google-chrome", "\"chrome://inspect\"") { UseShellExecute = false });
                    }
                    opened = true;
                }
                catch (Exception ex1)
                {
                    lastError = ex1;
                    try
                    {
                        if (OperatingSystem.IsWindows())
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd", "/c start msedge \"edge://inspect\"") { CreateNoWindow = true });
                        }
                        else if (OperatingSystem.IsMacOS())
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("open", "-a \"Microsoft Edge\" \"edge://inspect\"") { UseShellExecute = false });
                        }
                        else
                        {
                            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("microsoft-edge", "\"edge://inspect\"") { UseShellExecute = false });
                        }
                        opened = true;
                    }
                    catch (Exception ex2)
                    {
                        lastError = ex2;
                    }
                }

                if (!opened)
                {
                    await _dialogService.ShowMessageAsync("Debug Started",
                        $"The app is now running in debug mode on the TV. The debugger is forwarded to localhost:{port}.\n\n" +
                        "However, we could not automatically open the inspector in your browser. " +
                        "Please manually open 'chrome://inspect' or 'edge://inspect' in your browser to attach to the TV.");
                }
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // A partial/failed install can leave a package dir that vd_applist never lists, so it can't be
        // removed from the list above. vd_appuninstall <packageId> still reclaims it — offer a manual
        // escape hatch: prompt for the package id and force-remove it.
        [RelayCommand]
        private async Task RemoveLeftover()
        {
            if (IsBusy)
                return;

            var id = await _dialogService.PromptForTextAsync(
                "Remove leftover",
                "Enter the package id of a leftover/partial install to remove:",
                "e.g. HarborTV");
            if (string.IsNullOrWhiteSpace(id))
                return;
            id = id.Trim();

            var confirm = await _dialogService.ShowConfirmationAsync(
                "Remove leftover",
                $"Force-remove package \"{id}\" from {TvLabel}?",
                "Remove", "Cancel");
            if (!confirm)
                return;

            IsBusy = true;
            StatusText = $"Removing {id}…";
            try
            {
                var result = await _installer.UninstallAppAsync(_tvIp, id);
                // "failed[132]" = not installed — already gone, treat as success.
                var ok = result.ExitCode == 0 ||
                         (result.Output?.Contains("failed[132]", StringComparison.OrdinalIgnoreCase) ?? false);
                if (!ok)
                {
                    IsBusy = false;
                    await _dialogService.ShowErrorAsync(
                        string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() ?? "Remove failed." : result.Error);
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

        [RelayCommand]
        private async Task StopDebug()
        {
            if (!IsDebugging || _debuggedApp == null) return;
            
            if (_activeForwardSession != null)
            {
                await _activeForwardSession.DisposeAsync();
                _activeForwardSession = null;
            }
            
            IsDebugging = false;
            _debuggedApp = null;
            StatusText = string.Empty;
        }

        public void Dispose()
        {
            if (_activeForwardSession != null)
            {
                try { _activeForwardSession.DisposeAsync().AsTask().Wait(); } catch { }
                _activeForwardSession = null;
            }
        }

    }

    public partial class InstalledAppViewModel : ObservableObject
    {
        public InstalledApp App { get; }
        
        [ObservableProperty]
        private Avalonia.Media.Imaging.Bitmap? iconBitmap;
        
        public InstalledAppViewModel(InstalledApp app)
        {
            App = app;
        }
        
        public async Task LoadIconAsync(System.Net.Http.HttpClient http, System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap?> cache)
        {
            if (string.IsNullOrEmpty(App.IconUrl)) return;
            
            if (cache.TryGetValue(App.IconUrl, out var cached))
            {
                IconBitmap = cached;
                return;
            }
            
            try
            {
                var bytes = await http.GetByteArrayAsync(App.IconUrl);
                using var ms = new System.IO.MemoryStream(bytes);
                var bmp = new Avalonia.Media.Imaging.Bitmap(ms);
                cache[App.IconUrl] = bmp;
                IconBitmap = bmp;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Failed to load app icon '{App.IconUrl}': {ex.Message}");
                cache[App.IconUrl] = null;
            }
        }
    }
}

