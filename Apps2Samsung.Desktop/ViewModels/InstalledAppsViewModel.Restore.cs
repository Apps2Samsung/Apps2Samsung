using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;

namespace Apps2Samsung.ViewModels
{
    /// <summary>An app that was on the TV when its list was last complete and is gone now.</summary>
    public sealed record MissingAppRow(string Name, string Initials, string Color, string Detail);

    /// <summary>One app of a running restore.</summary>
    public partial class RestoreRunRow : ObservableObject
    {
        public string Name { get; }
        public string Initials { get; }
        public string Color { get; }

        [ObservableProperty] private string detail;
        [ObservableProperty] private string state = "lblRestoreStateWaiting".Localized();
        [ObservableProperty] private bool isWaiting = true;
        [ObservableProperty] private bool isRunning;
        [ObservableProperty] private bool isDone;
        [ObservableProperty] private bool isFailed;

        public RestoreRunRow(RestoreTvRow row)
        {
            Name = row.Name;
            Initials = row.Initials;
            Color = row.Color;
            detail = row.Detail;
        }

        public void Set(string stateKey, bool running = false, bool done = false, bool failed = false, string? detail = null)
        {
            State = stateKey.Localized();
            IsWaiting = false;
            IsRunning = running;
            IsDone = done;
            IsFailed = failed;
            if (!string.IsNullOrWhiteSpace(detail))
                Detail = detail;
        }
    }

    // Restore a TV (#1069, mockups A + the sheet). Every read of the TV's list is written down per TV
    // (Core SavedTvs); when apps of that list are gone the section says so and offers to install them
    // again. The restore itself is the Update all queue: one app at a time, a failure does not stop
    // the rest, a summary at the end.
    public partial class InstalledAppsViewModel
    {
        /// <summary>Update all's runner, with each app's outcome reported as it lands.</summary>
        public delegate Task<IReadOnlyList<UpdateAllOutcome>> BatchInstall(
            IReadOnlyList<(GitHubRelease Release, Asset Asset)> items, Action<int, string> onItem, Func<bool> stopRequested,
            Action<int, UpdateAllOutcome>? onOutcome);

        private BatchInstall? _restore;
        private Func<GitHubRelease, Asset, Task<bool>>? _collectSettings;
        private List<InstalledApp> _onTv = new();
        private SavedTv? _saved;
        private bool _stopRestore;
        private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Set by the main window, which runs the installs and owns the values form.</summary>
        public void UseRestore(BatchInstall restore, Func<GitHubRelease, Asset, Task<bool>> collectSettings)
        {
            _restore = restore;
            _collectSettings = collectSettings;
        }

        /// <summary>The window hosts the sheet; true when the user chose Restore.</summary>
        public event Func<RestoreTvViewModel, Task<bool>>? OnRequestRestoreSheet;

        public ObservableCollection<MissingAppRow> MissingApps { get; } = new();
        public ObservableCollection<RestoreRunRow> RestoreRows { get; } = new();
        public bool HasRestoreRows => RestoreRows.Count > 0;

        [ObservableProperty] private bool showRestoreCard;
        [ObservableProperty] private string restoreCardTitle = string.Empty;
        [ObservableProperty] private string restoreCardText = string.Empty;
        [ObservableProperty] private string restoreCardButton = string.Empty;
        [ObservableProperty] private bool restoreCardCanRestore;
        [ObservableProperty] private string missingHeader = string.Empty;

        /// <summary>The ⋯ menu's "Restore apps…": there is a saved list with apps this TV does not have.</summary>
        [ObservableProperty] private bool canRestoreSaved;

        [ObservableProperty] private bool isRestoring;
        [ObservableProperty] private string restoreProgressTitle = string.Empty;
        [ObservableProperty] private string restoreProgressText = string.Empty;
        [ObservableProperty] private double restoreProgress;
        [ObservableProperty] private bool stopRestoreAfterCurrent;

        // Called by Load with what the TV listed. Nothing is written down without a catalog: every app
        // would look like one the catalog does not have.
        private void SnapshotTv(List<InstalledApp> apps, IReadOnlyList<GitHubRelease> catalog, IReadOnlyList<CommunityCatalogRemoved> removed)
        {
            _onTv = apps;
            _saved = null;
            if (catalog.Count > 0)
            {
                try
                {
                    var settings = AppSettings.Default;
                    var fresh = TvProfiles.Save(apps, catalog, removed, settings.PackageIdRenamesJson, settings.InstalledVariantsJson,
                        _tvIp, null, null, includeSettings: false, DateTimeOffset.UtcNow, tvName: TvLabel);
                    _saved = SavedTvs.Remember(_tvIp, fresh.Profile, DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    Trace.WriteLine($"[restore] Could not save the app list of {_tvIp}: {ex.Message}");
                }
            }
            Dispatcher.UIThread.Post(RefreshRestoreOffer);
        }

        private void RefreshRestoreOffer()
        {
            MissingApps.Clear();
            var saved = _saved;
            var missing = saved is null
                ? new List<TvProfileApp>()
                : saved.Profile.Apps.Where(a => saved.Missing.Contains(SavedTvs.AppKey(a))).ToList();
            CanRestoreSaved = missing.Count > 0 && _restore is not null;
            var offer = saved is { OfferRestore: true } && missing.Count > 0 && _restore is not null && !IsRestoring && !IsUpdatingAll && !HasUpdateAllSummary;
            ShowRestoreCard = offer;
            if (!offer)
                return;

            var date = saved!.Profile.SavedAtUtc.ToLocalTime().ToString("d MMMM", CultureInfo.CurrentUICulture);
            foreach (var app in missing)
            {
                var tile = new InstalledApp(app.Name, null, app.AppId, string.Empty, string.Empty, true);
                var detail = string.Join(" · ", new[] { app.Version, app.Variant }.Where(s => !string.IsNullOrWhiteSpace(s)));
                MissingApps.Add(new MissingAppRow(app.Name, tile.Initials, tile.FallbackColor, detail));
            }

            // What can actually be installed again today; the rest is explained in the sheet.
            var catalog = _catalog?.Invoke() ?? Array.Empty<GitHubRelease>();
            var wanted = new HashSet<string>(saved.Missing, StringComparer.Ordinal);
            var ready = TvProfiles.Plan(saved.Profile, catalog, _onTv, AppSettings.Default.PackageIdRenamesJson, AppSettings.Default.InstalledVariantsJson, _tvIp)
                .Count(i => i.State == TvRestoreState.Ready && wanted.Contains(SavedTvs.AppKey(i.App)) && i.Release is { IsRoot: false });

            RestoreCardTitle = missing.Count == 1 ? "lblRestoreMissingOne".Localized() : string.Format("lblRestoreMissingMany".Localized(), missing.Count);
            RestoreCardText = string.Format("lblRestoreMissingText".Localized(), date, saved.Profile.Apps.Count);
            RestoreCardButton = ready == 1 ? "lblRestoreAppsOne".Localized() : string.Format("lblRestoreAppsMany".Localized(), ready);
            RestoreCardCanRestore = ready > 0;
            MissingHeader = string.Format("lblRestoreWasHere".Localized(), date, missing.Count);
        }

        /// <summary>The card's Restore and the ⋯ menu's "Restore apps…": this TV's own saved list.</summary>
        [RelayCommand]
        private async Task RestoreApps()
        {
            if (_saved is { } saved)
                await RestoreProfileAsync(saved.Profile);
        }

        [RelayCommand]
        private void DismissRestore()
        {
            SavedTvs.Dismiss(_tvIp);
            _saved = SavedTvs.Get(_tvIp);
            RefreshRestoreOffer();
        }

        /// <summary>Settings › Backup opens a saved list here: waits for this TV's own list first.</summary>
        public async Task RestoreProfileWhenLoadedAsync(TvProfile profile)
        {
            await Task.WhenAny(_firstLoad.Task, Task.Delay(TimeSpan.FromSeconds(90)));
            await RestoreProfileAsync(profile);
        }

        /// <summary>Opens the sheet for <paramref name="profile"/> against this TV and runs what was ticked.</summary>
        public async Task RestoreProfileAsync(TvProfile profile)
        {
            if (_restore is null || IsRestoring || IsUpdatingAll || IsBusy || OnRequestRestoreSheet is null)
                return;
            var catalog = _catalog?.Invoke() ?? Array.Empty<GitHubRelease>();
            if (catalog.Count == 0)
            {
                await _dialogService.ShowErrorAsync("errRestoreNoCatalog".Localized());
                return;
            }

            var plan = TvProfiles.Plan(profile, catalog, _onTv, AppSettings.Default.PackageIdRenamesJson, AppSettings.Default.InstalledVariantsJson, _tvIp);
            var sheet = new RestoreTvViewModel(profile, plan, catalog, TvLabel, _tvIp, _collectSettings);
            if (!await OnRequestRestoreSheet(sheet))
                return;
            var picked = sheet.Picked();
            if (picked.Count > 0)
                await RunRestore(picked);
        }

        private async Task RunRestore(IReadOnlyList<RestoreTvRow> picked)
        {
            // Values a profile file carries go where an install reads them, unless this computer has its own.
            var settings = AppSettings.Default;
            var wrote = false;
            foreach (var row in picked)
            {
                if (row.App.Settings is { Count: > 0 } carried && row.Asset.UserSettings is { } spec &&
                    UserSettingsStore.Get(settings.AppUserSettingsJson, row.Asset.FileName) is null)
                {
                    settings.AppUserSettingsJson = UserSettingsStore.Set(settings.AppUserSettingsJson, row.Asset.FileName, spec, carried);
                    wrote = true;
                }
            }
            if (wrote)
                settings.Save();

            RestoreRows.Clear();
            foreach (var row in picked)
                RestoreRows.Add(new RestoreRunRow(row));
            OnPropertyChanged(nameof(HasRestoreRows));

            _stopRestore = false;
            StopRestoreAfterCurrent = false;
            RestoreProgress = 0;
            RestoreProgressTitle = string.Format("lblRestoreProgress".Localized(), 1, picked.Count);
            RestoreProgressText = string.Empty;
            IsRestoring = true;
            HasUpdateAllSummary = false;
            ShowUpdatesBanner = false;
            ShowRestoreCard = false;

            IReadOnlyList<UpdateAllOutcome> outcomes;
            try
            {
                outcomes = await _restore!(
                    picked.Select(r => (r.Release, r.Asset)).ToList(),
                    (index, name) => Dispatcher.UIThread.Post(() =>
                    {
                        RestoreProgressTitle = string.Format("lblRestoreProgress".Localized(), index + 1, picked.Count);
                        RestoreProgressText = string.Format("lblRestoreInstalling".Localized(), name);
                        RestoreProgress = 100.0 * index / picked.Count;
                        if (index < RestoreRows.Count)
                            RestoreRows[index].Set("lblRestoreStateInstalling", running: true);
                    }),
                    () => _stopRestore,
                    (index, outcome) => Dispatcher.UIThread.Post(() =>
                    {
                        if (index >= RestoreRows.Count)
                            return;
                        switch (outcome.State)
                        {
                            case UpdateAllState.Updated:
                                RestoreRows[index].Set("lblRestoreStateInstalled", done: true);
                                break;
                            case UpdateAllState.Failed:
                                RestoreRows[index].Set("lblRestoreStateFailed", failed: true, detail: outcome.Detail);
                                break;
                            case UpdateAllState.Skipped:
                                RestoreRows[index].Set("lblRestoreStateSkipped", detail: outcome.Detail);
                                break;
                            default:
                                RestoreRows[index].Set("lblRestoreStateNotStarted");
                                break;
                        }
                    }));
            }
            catch (Exception ex)
            {
                outcomes = new[] { new UpdateAllOutcome(RestoreProgressText, UpdateAllState.Failed, ex.Message) };
            }
            finally
            {
                IsRestoring = false;
            }

            ShowSummary(outcomes, "lblRestoreDone", "lblRestoreDoneFailed", "lblRestoreNotStarted");
            await Load();
        }

        [RelayCommand]
        private void StopRestore()
        {
            _stopRestore = true;
            StopRestoreAfterCurrent = true;
        }

        // ---- the list as a file: for another computer, the CLI, or safekeeping ----

        private static readonly FilePickerFileType ProfileFile = new("Apps2Samsung TV app list")
        {
            Patterns = new[] { "*.a2stv", "*.json" },
            MimeTypes = new[] { "application/json" },
        };

        private static IStorageProvider? Storage() =>
            Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
                ? (desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow)?.StorageProvider
                : null;

        /// <summary>"Save this app list to a file…": the TV as it is now. The typed values only go in when asked.</summary>
        [RelayCommand]
        private async Task SaveAppList()
        {
            if (IsBusy || Storage() is not { } storage)
                return;
            var catalog = _catalog?.Invoke() ?? Array.Empty<GitHubRelease>();
            var removed = _removed?.Invoke() ?? Array.Empty<CommunityCatalogRemoved>();
            if (catalog.Count == 0)
            {
                await _dialogService.ShowErrorAsync("errRestoreNoCatalog".Localized());
                return;
            }

            TvProfileSaved Build(bool withValues) => TvProfiles.Save(_onTv, catalog, removed, AppSettings.Default.PackageIdRenamesJson,
                AppSettings.Default.InstalledVariantsJson, _tvIp, null, AppSettings.Default.AppUserSettingsJson, withValues, DateTimeOffset.UtcNow, tvName: TvLabel);

            var saved = Build(withValues: true);
            if (saved.Profile.Apps.Count == 0)
            {
                await _dialogService.ShowMessageAsync("lblRestoreSaveList".Localized(), "lblRestoreSaveNothing".Localized());
                return;
            }
            if (saved.Profile.ContainsSettings && !await _dialogService.ShowConfirmationAsync(
                    "lblRestoreSaveList".Localized(), "lblRestoreSaveValuesAsk".Localized(),
                    "lblRestoreSaveValuesYes".Localized(), "lblRestoreSaveValuesNo".Localized()))
                saved = Build(withValues: false);

            try
            {
                var safe = string.Concat(TvLabel.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c));
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "lblRestoreSaveList".Localized(),
                    SuggestedFileName = $"{safe}-{DateTime.Now:yyyy-MM-dd}.a2stv",
                    DefaultExtension = "a2stv",
                    FileTypeChoices = new[] { ProfileFile },
                });
                if (file is null)
                    return;
                await using (var stream = await file.OpenWriteAsync())
                await using (var writer = new StreamWriter(stream))
                    await writer.WriteAsync(TvProfiles.Serialize(saved.Profile));
                StatusText = saved.LeftOut.Count == 0
                    ? string.Format("statusRestoreSaved".Localized(), saved.Profile.Apps.Count, file.Name)
                    : string.Format("statusRestoreSavedLeftOut".Localized(), saved.Profile.Apps.Count, file.Name, saved.LeftOut.Count);
            }
            catch (Exception ex)
            {
                await _dialogService.ShowErrorAsync(ex.Message);
            }
        }

        /// <summary>"Restore from a file…": a list saved here, on another computer or by the CLI.</summary>
        [RelayCommand]
        private async Task RestoreFromFile()
        {
            if (await PickProfileAsync(_dialogService) is { } profile)
                await RestoreProfileAsync(profile);
        }

        /// <summary>The file picker and the parse, shared with Settings › Backup. Null when cancelled or not a list.</summary>
        public static async Task<TvProfile?> PickProfileAsync(Apps2Samsung.Interfaces.IDialogService dialogs)
        {
            if (Storage() is not { } storage)
                return null;
            try
            {
                var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "lblRestoreFromFile".Localized(),
                    AllowMultiple = false,
                    FileTypeFilter = new[] { ProfileFile },
                });
                if (files.Count == 0)
                    return null;
                await using var stream = await files[0].OpenReadAsync();
                using var reader = new StreamReader(stream);
                return TvProfiles.Parse(await reader.ReadToEndAsync());
            }
            catch (Exception ex)
            {
                await dialogs.ShowErrorAsync(ex.Message);
                return null;
            }
        }
    }
}
