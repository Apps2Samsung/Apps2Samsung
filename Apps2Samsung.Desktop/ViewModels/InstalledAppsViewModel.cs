using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Diagnostics;
using System.Threading.Tasks;
using Apps2Samsung.Extensions;

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

        // The catalog as the Install section lists it, the install the Update button runs, and the
        // catalog reload behind "Check for updates" (#820). Null on a head that wires none of it.
        private readonly Func<System.Collections.Generic.IReadOnlyList<GitHubRelease>>? _catalog;
        private readonly Func<GitHubRelease, Asset, Task>? _update;
        private readonly Func<Task>? _refreshCatalog;
        private readonly System.Collections.Generic.Dictionary<string, Apps2Samsung.Catalog.CatalogMatch> _matches =
            new(StringComparer.OrdinalIgnoreCase);
        private static readonly System.Net.Http.HttpClient _http = new();
        private static readonly System.Collections.Generic.Dictionary<string, Avalonia.Media.Imaging.Bitmap?> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);

        public string TvLabel { get; }

        public ObservableCollection<InstalledAppViewModel> Apps { get; } = new();

        // Every app the TV listed, as read; Apps and Groups are this in the chosen order.
        private System.Collections.Generic.List<InstalledAppViewModel> _all = new();

        /// <summary>How the list is ordered (#888); apps with an update stay on top whichever it is.</summary>
        public Apps2Samsung.Sdb.InstalledAppOrder Order =>
            Enum.TryParse<Apps2Samsung.Sdb.InstalledAppOrder>(Apps2Samsung.Helpers.AppSettings.Default.InstalledAppsOrder, out var order)
                ? order
                : Apps2Samsung.Sdb.InstalledAppOrder.Size;

        public string SortHeader => string.Format("lblSortBy".Localized(), (Order switch
        {
            Apps2Samsung.Sdb.InstalledAppOrder.Name => "lblSortName",
            Apps2Samsung.Sdb.InstalledAppOrder.Newest => "lblSortNewest",
            _ => "lblSortSize",
        }).Localized());

        public bool IsSortSize => Order == Apps2Samsung.Sdb.InstalledAppOrder.Size;
        public bool IsSortName => Order == Apps2Samsung.Sdb.InstalledAppOrder.Name;
        public bool IsSortNewest => Order == Apps2Samsung.Sdb.InstalledAppOrder.Newest;

        /// <summary>The ⋯ menu's Sort entries: remembers the order and re-sorts what is on screen.</summary>
        [RelayCommand]
        private void Sort(string order)
        {
            if (!Enum.TryParse<Apps2Samsung.Sdb.InstalledAppOrder>(order, out var picked) || picked == Order)
                return;
            Apps2Samsung.Helpers.AppSettings.Default.InstalledAppsOrder = picked.ToString();
            Apps2Samsung.Helpers.AppSettings.Default.Save();
            OnPropertyChanged(nameof(Order));
            OnPropertyChanged(nameof(SortHeader));
            OnPropertyChanged(nameof(IsSortSize));
            OnPropertyChanged(nameof(IsSortName));
            OnPropertyChanged(nameof(IsSortNewest));
            ShowInOrder();
        }

        // Fills Apps and Groups from _all in the chosen order: the apps that can be removed, then the TV's own.
        private void ShowInOrder()
        {
            var byApp = _all.ToDictionary(vm => vm.App, System.Collections.Generic.ReferenceEqualityComparer.Instance);
            var sorted = Apps2Samsung.Sdb.InstalledAppSort.Sort(_all.Select(vm => vm.App), Order).Select(a => byApp[a]).ToList();
            Apps.Clear();
            foreach (var a in sorted)
                Apps.Add(a);
            Groups.Clear();
            var removableApps = sorted.Where(a => a.App.IsRemovable).ToList();
            var builtInApps = sorted.Where(a => !a.App.IsRemovable).ToList();
            if (removableApps.Count > 0)
                Groups.Add(new InstalledAppGroup("lblAppsGroupRemovable".Localized(), removableApps));
            if (builtInApps.Count > 0)
                Groups.Add(new InstalledAppGroup("lblAppsGroupBuiltIn".Localized(), builtInApps));
        }

        /// <summary>The list as the section shows it: the apps that can be removed, then the TV's own.</summary>
        public ObservableCollection<InstalledAppGroup> Groups { get; } = new();

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

        public event Action? OnRequestClose;

        /// <summary>The window hosts the console (it needs an owner window); the view model just asks for it.</summary>
        public event Action<DebugConsoleViewModel>? OnRequestDebugConsole;

        // The catalog's own icons (#1000), for the apps that came from the catalog.
        private readonly Apps2Samsung.Catalog.CatalogIcons? _icons;

        public InstalledAppsViewModel(ITizenInstallerService installer, IDialogService dialogService, string tvIp, string tvLabel,
            Func<System.Collections.Generic.IReadOnlyList<GitHubRelease>>? catalog = null,
            Func<GitHubRelease, Asset, Task>? update = null,
            Func<Task>? refreshCatalog = null,
            Func<System.Collections.Generic.IReadOnlyList<CommunityCatalogRemoved>>? removed = null,
            Apps2Samsung.Catalog.CatalogIcons? icons = null)
        {
            _icons = icons;
            _installer = installer;
            _dialogService = dialogService;
            _tvIp = tvIp;
            TvLabel = tvLabel;
            _catalog = catalog;
            _update = update;
            _refreshCatalog = refreshCatalog;
            _removed = removed;
        }

        // Apps the official catalog stopped distributing (#1033), to flag an installed copy.
        private readonly Func<System.Collections.Generic.IReadOnlyList<CommunityCatalogRemoved>>? _removed;

        // ---- apps that moved to their author's repository (#1018, moved_to) ----

        /// <summary>One repository apps on this TV moved to, with the sentence and the Add button.</summary>
        public sealed record MovedRow(Apps2Samsung.Catalog.Repositories.MovedHint Hint, string Text);
        public System.Collections.ObjectModel.ObservableCollection<MovedRow> Moved { get; } = new();
        [ObservableProperty]
        private string movedStatus = string.Empty;

        /// <summary>Adds the repository with the key the official catalog named, then reloads the catalog and this list.</summary>
        [RelayCommand]
        private async Task AddMovedRepository(MovedRow? row)
        {
            if (row is null)
                return;
            var repositories = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions
                .GetRequiredService<Apps2Samsung.Catalog.Repositories.SignedRepositories>(App.Services);
            var (source, problem) = await Apps2Samsung.Catalog.Repositories.MovedApps.AdoptAsync(repositories, row.Hint.To);
            if (source is null)
            {
                MovedStatus = string.Format("errMovedNotAdded".Localized(), problem);
                return;
            }
            var sources = Apps2Samsung.Catalog.UserSources.Parse(Apps2Samsung.Helpers.AppSettings.Default.UserSourcesJson);
            sources.Add(source);
            Apps2Samsung.Helpers.AppSettings.Default.UserSourcesJson = Apps2Samsung.Catalog.UserSources.Serialize(sources);
            Apps2Samsung.Helpers.AppSettings.Default.Save();
            MovedStatus = string.Empty;
            if (_refreshCatalog is not null)
                await _refreshCatalog();
            await Load();
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
                var catalog = _catalog?.Invoke() ?? Array.Empty<GitHubRelease>();
                var removed = _removed?.Invoke() ?? Array.Empty<CommunityCatalogRemoved>();
                _matches.Clear();

                for (int i = 0; i < apps.Count; i++)
                {
                    // An app the catalog removed (#1033): the quiet line, the reason behind it, and no
                    // catalog comparison, so no Update.
                    if (Apps2Samsung.Catalog.RemovedApps.Find(apps[i], removed, catalog, Apps2Samsung.Helpers.AppSettings.Default.PackageIdRenamesJson) is { } gone)
                        apps[i] = apps[i] with { CatalogLine = Apps2Samsung.Catalog.RemovedApps.RowClickKey.Localized(), Removed = gone };
                    else

                    // The catalog entry this app came from, by the id its manifest declares (#820).
                    // With the variant this TV got last time (#975), so an app with several files is
                    // compared with the right one and Update installs that one again.
                    if (Apps2Samsung.Catalog.InstalledAppUpdates.Find(apps[i], catalog, Apps2Samsung.Helpers.AppSettings.Default.PackageIdRenamesJson,
                            Apps2Samsung.Helpers.AppSettings.Default.InstalledVariantsJson, _tvIp) is { } match)
                    {
                        _matches[apps[i].TizenId] = match;
                        apps[i] = apps[i] with { CatalogLine = CatalogLine(match), HasUpdate = match.HasUpdate };
                    }

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

                var hints = Apps2Samsung.Catalog.Repositories.MovedApps.Hints(_matches.Values,
                    Apps2Samsung.Catalog.UserSources.Parse(Apps2Samsung.Helpers.AppSettings.Default.UserSourcesJson));
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _all = viewModels;
                    ShowInOrder();
                    Moved.Clear();
                    foreach (var hint in hints)
                        Moved.Add(new MovedRow(hint, string.Format("hintMoved".Localized(), string.Join(", ", hint.AppNames), hint.Domain)));
                });
                
                // Start loading bitmaps in the background. An app that came from the catalog takes the
                // catalog's icon (#1000): the store-icon list only knows store apps, so Jellyfin,
                // TizenTube and the community apps drew their letter.
                var matches = new System.Collections.Generic.Dictionary<string, Apps2Samsung.Catalog.CatalogMatch>(_matches, StringComparer.OrdinalIgnoreCase);
                _ = Task.Run(async () =>
                {
                    foreach (var vm in viewModels)
                    {
                        if (_icons is not null && matches.TryGetValue(vm.App.TizenId, out var m))
                        {
                            try
                            {
                                if (await _icons.PathForAsync(m.Release) is { } path && Apps2Samsung.Helpers.CatalogIconBitmaps.For(path) is { } bitmap)
                                {
                                    vm.IconBitmap = bitmap;
                                    continue;
                                }
                            }
                            catch (Exception ex)
                            {
                                System.Diagnostics.Trace.WriteLine($"[installed] catalog icon for {m.Release.Name}: {ex.Message}");
                            }
                        }
                        if (!string.IsNullOrEmpty(vm.App.IconUrl))
                            await vm.LoadIconAsync(_http, _bitmapCache);
                    }
                });

                var removable = apps.Count(a => a.IsRemovable);
                var totalUsed = InstalledApp.FormatSize(apps.Sum(a => a.SizeBytes));
                // An empty list is the TV's answer (a fresh emulator, a reset set); a failed read
                // throws and lands in the catch below.
                StatusText = apps.Count == 0
                    ? "statusNoInstalledApps".Localized()
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

        private static string? CatalogLine(Apps2Samsung.Catalog.CatalogMatch match) => match.State switch
        {
            Apps2Samsung.Catalog.CatalogVersionState.UpdateAvailable => string.Format("lblCatalogUpdateAvailable".Localized(), match.CatalogVersion),
            Apps2Samsung.Catalog.CatalogVersionState.Different => string.Format("lblCatalogDifferentVersion".Localized(), match.CatalogVersion),
            Apps2Samsung.Catalog.CatalogVersionState.UpToDate => "lblCatalogUpToDate".Localized(),
            _ => null,
        };

        /// <summary>"Update" on a row (#820): the catalog entry this app came from, installed the normal way.</summary>
        [RelayCommand]
        private async Task Update(InstalledApp? app)
        {
            if (app is null || _update is null || !_matches.TryGetValue(app.TizenId, out var match))
                return;
            await _update(match.Release, match.Asset);
        }

        /// <summary>Reloads the catalog and the TV's list, then says how many apps have a newer version (#820).</summary>
        [RelayCommand]
        private async Task CheckForUpdates()
        {
            if (_refreshCatalog is not null)
            {
                IsBusy = true;
                try { await _refreshCatalog(); }
                catch (Exception ex) { Trace.WriteLine($"[InstalledApps] Catalog refresh failed: {ex.Message}"); }
            }
            await Load();
            if (Apps.Count == 0)
                return;
            var updates = Apps.Count(a => a.App.HasUpdate);
            StatusText = updates > 0
                ? string.Format("statusUpdatesAvailable".Localized(), updates)
                : "statusNoUpdates".Localized();
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
                // "failed[132]" = not installed - already gone, treat as success.
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
                // No window to host it (shouldn't happen outside the designer) - don't leave the
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
        // removed from the list above. vd_appuninstall <packageId> still reclaims it - offer a manual
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
                // "failed[132]" = not installed - already gone, treat as success.
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
        }

    }

    /// <summary>A titled group of rows in the On the TV list.</summary>
    public sealed record InstalledAppGroup(string Title, System.Collections.Generic.IReadOnlyList<InstalledAppViewModel> Apps);

    public partial class InstalledAppViewModel : ObservableObject
    {
        public InstalledApp App { get; }
        
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasIconBitmap))]
        private Avalonia.Media.Imaging.Bitmap? iconBitmap;

        /// <summary>An icon is loaded (the catalog's or the store's); until then, or without one, the letter tile.</summary>
        public bool HasIconBitmap => IconBitmap is not null;
        
        public InstalledAppViewModel(InstalledApp app)
        {
            App = app;
        }

        // ---- a removed app (#1033): what the line under its name opens ----
        public bool IsRemoved => App.IsRemoved;
        public bool ShowCatalogLine => App.HasCatalogLine && !App.IsRemoved;
        public string RemovedBadge => Apps2Samsung.Catalog.RemovedApps.BadgeKey.Localized();
        public string RemovedReason => App.Removed is { } r ? Apps2Samsung.Catalog.RemovedApps.ReasonKey(r.Reason).Localized() : string.Empty;
        public string RemovedNote => App.Removed?.Note?.Trim() ?? string.Empty;
        public bool HasRemovedNote => RemovedNote.Length > 0;
        public string RemovedOn => Apps2Samsung.Catalog.RemovedApps.SinceText(App.Removed?.Since, System.Globalization.CultureInfo.CurrentUICulture) is { } date
            ? string.Format(Apps2Samsung.Catalog.RemovedApps.OnDateKey.Localized(), date)
            : string.Empty;
        public string RemovedFooter => Apps2Samsung.Catalog.RemovedApps.FooterKey.Localized();
        
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

