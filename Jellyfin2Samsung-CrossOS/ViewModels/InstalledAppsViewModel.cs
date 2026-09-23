using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Agent;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Apps2Samsung.Extensions;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// Lists the apps installed on a TV (via <see cref="ITizenInstallerService.GetInstalledAppsAsync"/>,
    /// which shares the Core parser with the mobile head) and offers a per-app uninstall for
    /// user-removable apps. Also the home of the install-leftovers card: sdbd's staging folder, where
    /// every package ever pushed still sits, read and cleared by the debug agent — the same
    /// storage housekeeping as the uninstalls above, and like them SDB-only.
    /// </summary>
    public partial class InstalledAppsViewModel : ViewModelBase, IDisposable
    {
        private readonly ITizenInstallerService _installer;
        private readonly IDialogService _dialogService;
        private readonly string _tvIp;

        // The developer channel and this head's installer, for the debug agent behind the leftovers
        // card. Null where the caller has neither; the card then stays hidden.
        private readonly ISdbEngine? _sdb;
        private readonly Func<string, Action<string>, Task<bool>>? _installWgt;
        private DebugAgentClient? _agent;
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

        private DebugConsoleViewModel? _debugConsole;

        [ObservableProperty]
        private string statusText = string.Empty;

        /// <summary>Whether the leftovers card can work at all: it needs the developer channel.</summary>
        public bool HasSdb => _sdb is not null;

        /// <summary>The leftovers card's own status line.</summary>
        [ObservableProperty]
        private string stagingStatus = string.Empty;

        /// <summary>The staging folder's contents as the agent last listed them.</summary>
        public ObservableCollection<StagedFileRow> StagedFiles { get; } = new();

        /// <summary>Whether there is anything a clear would delete — what enables the Clear button.</summary>
        [ObservableProperty]
        private bool hasStagedPackages;

        /// <summary>Set once the folder has been read, so the (possibly empty) list is shown.</summary>
        [ObservableProperty]
        private bool isStagingListed;

        public event Action? OnRequestClose;

        /// <summary>The window hosts the console (it needs an owner window); the view model just asks for it.</summary>
        public event Action<DebugConsoleViewModel>? OnRequestDebugConsole;

        public InstalledAppsViewModel(
            ITizenInstallerService installer, IDialogService dialogService, string tvIp, string tvLabel,
            ISdbEngine? sdb = null, Func<string, Action<string>, Task<bool>>? installWgt = null)
        {
            _installer = installer;
            _dialogService = dialogService;
            _tvIp = tvIp;
            TvLabel = tvLabel;
            _sdb = sdb;
            _installWgt = installWgt;
        }

        [RelayCommand]
        private async Task Load()
        {
            IsBusy = true;
            StatusText = "statusReadingInstalledApps".Localized();
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
                StatusText = string.Format("statusReadAppListFailed".Localized(), ex.Message);
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
                "lblUninstallApp".Localized(),
                string.Format("statusConfirmUninstall".Localized(), app.DisplayName, TvLabel, app.TizenId),
                "lblUninstall".Localized(), "lblCancel".Localized());
            if (!confirm)
                return;

            IsBusy = true;
            StatusText = string.Format("statusUninstalling".Localized(), app.DisplayName);
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
                        string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() ?? "lblUninstallFailed".Localized() : result.Error);
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
            StatusText = string.Format("statusLaunching".Localized(), app.DisplayName);
            try
            {
                await _installer.LaunchAppAsync(_tvIp, app.TizenId);
                StatusText = string.Format("statusLaunched".Localized(), app.DisplayName);
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
            StatusText = string.Format("statusStopping".Localized(), app.DisplayName);
            try
            {
                await _installer.StopAppAsync(_tvIp, app.TizenId);
                StatusText = string.Format("statusStopped".Localized(), app.DisplayName);
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

        // Opens the debug console for an app: the same log-streaming console the Android head has,
        // instead of the old "forward the port and hope Chrome's chrome://inspect finds it" handoff.
        // The console window owns the stop → relaunch-in-debug → tunnel → attach lifecycle; this
        // view model only tracks that one is open so the list stays parked meanwhile.
        [RelayCommand]
        private async Task Debug(InstalledApp? app)
        {
            if (app is null || IsBusy || IsDebugging) return;

            var console = new DebugConsoleViewModel(_installer, _dialogService, _tvIp, app);
            console.Attached += port =>
                StatusText = string.Format("statusDebugging".Localized(), app.DisplayName, port);
            console.Detached += () =>
            {
                if (!ReferenceEquals(_debugConsole, console)) return;
                _debugConsole = null;
                IsDebugging = false;
                StatusText = string.Empty;
            };

            _debugConsole = console;
            IsDebugging = true;
            StatusText = string.Format("statusStartingDebug".Localized(), app.DisplayName);

            if (OnRequestDebugConsole is null)
            {
                // No window to host it (shouldn't happen outside the designer) — don't leave the
                // list parked on a console that will never open.
                _debugConsole = null;
                IsDebugging = false;
                StatusText = string.Empty;
                await _dialogService.ShowErrorAsync("statusDebugAttachFailed".Localized());
                return;
            }

            OnRequestDebugConsole.Invoke(console);
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
                "lblRemoveLeftoverTitle".Localized(),
                "statusRemoveLeftoverPrompt".Localized(),
                "e.g. HarborTV");
            if (string.IsNullOrWhiteSpace(id))
                return;
            id = id.Trim();

            var confirm = await _dialogService.ShowConfirmationAsync(
                "lblRemoveLeftoverTitle".Localized(),
                string.Format("statusConfirmForceRemove".Localized(), id, TvLabel),
                "lblRemove".Localized(), "lblCancel".Localized());
            if (!confirm)
                return;

            IsBusy = true;
            StatusText = string.Format("statusRemoving".Localized(), id);
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
                        string.IsNullOrWhiteSpace(result.Error) ? result.Output?.Trim() ?? "lblRemoveFailed".Localized() : result.Error);
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

        // ---------------------------------------------------------------------------------------
        // Install leftovers. sdbd pushes every package to /home/owner/share/tmp/sdk_tools and leaves
        // it there; its own "0 rmfile" verb deletes nothing on retail firmware. The debug agent runs
        // on the TV as that folder's owner, so it lists and clears the folder itself. The agent is
        // attached on the first click (installed first if the TV lacks it) and kept until this window
        // closes.
        // ---------------------------------------------------------------------------------------

        /// <summary>Reads the staging folder: attaches the agent if needed, then lists. Read-only.</summary>
        [RelayCommand]
        private async Task CheckStaging()
        {
            if (IsBusy || IsDebugging)
                return;

            IsBusy = true;
            try
            {
                var agent = await AttachAgentAsync();
                if (agent is null)
                    return;

                StagingStatus = "lblToolboxStagingListing".Localized();
                var listing = await LoadStagedFilesAsync(agent);
                var packages = listing.Packages.ToList();
                StagingStatus = listing.Errors.Count > 0 && listing.Files.Count == 0
                    ? string.Format("lblToolboxStagingFailed".Localized(), string.Join("; ", listing.Errors))
                    : packages.Count == 0
                        ? "lblToolboxStagingEmpty".Localized()
                        : string.Format("lblToolboxStagingSummary".Localized(), packages.Count, InstalledApp.FormatSize(listing.PackageBytes));
            }
            catch (Exception ex)
            {
                StagingStatus = string.Format("lblToolboxStagingFailed".Localized(), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        /// <summary>
        /// Deletes the package files in the staging folder — nothing else in there, and no installed
        /// app. On request only: nothing in the install flow calls this.
        /// </summary>
        [RelayCommand]
        private async Task ClearStaging()
        {
            if (IsBusy || IsDebugging)
                return;

            IsBusy = true;
            try
            {
                var agent = await AttachAgentAsync();
                if (agent is null)
                    return;

                StagingStatus = "lblToolboxStagingClearing".Localized();
                var result = await agent.ClearStagingAsync();
                var status = result.Failed.Count == 0
                    ? string.Format("lblToolboxStagingCleared".Localized(), result.Deleted.Count, InstalledApp.FormatSize(result.FreedBytes))
                    : string.Format("lblToolboxStagingPartial".Localized(), result.Deleted.Count, result.Failed.Count, string.Join("; ", result.Failed));

                // Show what is left, but keep the verdict: the clear is the news here, not the listing.
                try { await LoadStagedFilesAsync(agent); }
                catch (Exception ex) { System.Diagnostics.Trace.WriteLine($"[installed-apps] staging re-list after clear: {ex.Message}"); }
                StagingStatus = status;
            }
            catch (Exception ex)
            {
                StagingStatus = string.Format("lblToolboxStagingFailed".Localized(), ex.Message);
            }
            finally
            {
                IsBusy = false;
            }
        }

        // The attached agent, attaching (and installing) on the first call. Null, with the reason in
        // the status line, when there is no developer channel or the agent could not be put on the TV.
        private async Task<DebugAgentClient?> AttachAgentAsync()
        {
            if (_agent is not null)
                return _agent;

            if (_sdb is null)
            {
                StagingStatus = "lblToolboxAgentNeedsSdb".Localized();
                return null;
            }

            try
            {
                var progress = new Progress<string>(key => StagingStatus = key.Localized());
                var agent = await DebugAgentClient.AttachCurrentAsync(
                    _sdb, _tvIp, _installWgt, message => StagingStatus = message, progress);
                agent.Disconnected += OnAgentDisconnected;
                _agent = agent;

                if (!agent.SupportsStaging)
                {
                    StagingStatus = string.Format("lblToolboxStagingAgentTooOld".Localized(), agent.AgentVersion, DebugAgentClient.StagingSince);
                    await DetachAgentAsync();
                    return null;
                }

                return agent;
            }
            catch (DebugAgentInstallException ex)
            {
                System.Diagnostics.Trace.WriteLine($"[installed-apps] agent install: {ex.Message}");
                StagingStatus = ex.Key.Localized();
                return null;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[installed-apps] agent attach failed: {ex}");
                StagingStatus = string.Format("lblToolboxAgentFailed".Localized(), ex.Message);
                return null;
            }
        }

        private async Task<DebugAgentStaging> LoadStagedFilesAsync(DebugAgentClient agent)
        {
            var listing = await agent.ListStagingAsync();
            var kept = "lblToolboxStagingKept".Localized();
            StagedFiles.Clear();
            foreach (var file in listing.Files)
                StagedFiles.Add(new StagedFileRow(file, kept));
            HasStagedPackages = listing.Packages.Any();
            IsStagingListed = true;
            return listing;
        }

        private async Task DetachAgentAsync()
        {
            var agent = _agent;
            _agent = null;
            if (agent is not null)
            {
                agent.Disconnected -= OnAgentDisconnected;
                await agent.DisposeAsync();
            }
        }

        // Raised off the UI thread by the inspector's receive loop. The list stays; the next click
        // simply attaches again.
        private void OnAgentDisconnected(string? reason) =>
            Dispatcher.UIThread.Post(async () =>
            {
                if (_agent is null)
                    return;
                await DetachAgentAsync();
                StagingStatus = string.Format("lblToolboxAgentDisconnected".Localized(), reason ?? string.Empty);
            });

        [RelayCommand]
        private void Close() => OnRequestClose?.Invoke();

        // Closing the console window detaches and raises Detached, which clears IsDebugging above.
        [RelayCommand]
        private Task StopDebug()
        {
            _debugConsole?.CloseCommand.Execute(null);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _debugConsole?.CloseCommand.Execute(null);
            _ = DetachAgentAsync();
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

    /// <summary>One file in the TV's install staging folder, as the agent listed it.</summary>
    public sealed class StagedFileRow
    {
        public StagedFileRow(DebugAgentStagedFile file, string keptLabel)
        {
            File = file;
            var detail = InstalledApp.FormatSize(file.Size);
            if (file.Modified is { } modified)
                detail += $" · {modified.LocalDateTime:g}";
            // A file a clear leaves alone (not a package) says so, so the list and the count agree.
            if (!file.IsPackage)
                detail += $" · {keptLabel}";
            Detail = detail;
        }

        public DebugAgentStagedFile File { get; }
        public string Name => File.Name;
        public bool IsPackage => File.IsPackage;
        public string Detail { get; }
    }
}

