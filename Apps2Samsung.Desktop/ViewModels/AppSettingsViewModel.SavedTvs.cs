using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;

namespace Apps2Samsung.ViewModels
{
    /// <summary>One TV on Settings › Backup › Saved TVs (#1069).</summary>
    public sealed record SavedTvRow(string Key, string Name, string Detail, string Chip, bool HasMissing, string RestoreText, bool IsSelectedTv);

    // Settings › Backup: the app list of every TV this computer has read (#1069, mockup B). Core keeps
    // them by itself; from here a list is restored onto the selected TV, saved to a file or forgotten.
    public partial class AppSettingsViewModel
    {
        public ObservableCollection<SavedTvRow> SavedTvRows { get; } = new();

        [ObservableProperty] private bool hasSavedTvs;
        [ObservableProperty] private string savedTvsStatus = string.Empty;

        /// <summary>Set by the main window: opens the restore sheet for a list against the selected TV.</summary>
        public Func<TvProfile, Task>? RestoreProfile { get; set; }

        private void InitSavedTvs()
        {
            SavedTvs.Changed += () => Dispatcher.UIThread.Post(RefreshSavedTvs);
            RefreshSavedTvs();
        }

        /// <summary>Re-reads the saved lists. Also called when the Backup page is opened.</summary>
        public void RefreshSavedTvs()
        {
            SavedTvRows.Clear();
            var selected = AppSettings.Default.TvIp;
            foreach (var tv in SavedTvs.All().OrderByDescending(t => t.LastSeenUtc))
            {
                var p = tv.Profile;
                var name = string.IsNullOrWhiteSpace(p.TvName) ? tv.Key : p.TvName!;
                var date = p.SavedAtUtc.ToLocalTime().ToString("d MMMM yyyy", CultureInfo.CurrentUICulture);
                var parts = new[]
                {
                    string.Equals(name, tv.Key, StringComparison.OrdinalIgnoreCase) ? null : tv.Key,
                    p.Apps.Count == 1 ? "lblSavedTvAppsOne".Localized() : string.Format("lblSavedTvAppsMany".Localized(), p.Apps.Count),
                    string.Format("lblSavedTvSavedOn".Localized(), date),
                };
                var chip = tv.Missing.Count switch
                {
                    0 => string.Empty,
                    1 => "lblSavedTvMissingOne".Localized(),
                    _ => string.Format("lblSavedTvMissingMany".Localized(), tv.Missing.Count),
                };
                var isSelected = string.Equals(tv.Key, selected, StringComparison.OrdinalIgnoreCase);
                SavedTvRows.Add(new SavedTvRow(tv.Key, name, string.Join(" · ", parts.Where(s => !string.IsNullOrEmpty(s))), chip,
                    tv.Missing.Count > 0, (isSelected ? "lblSavedTvRestore" : "lblSavedTvRestoreOther").Localized(), isSelected));
            }
            HasSavedTvs = SavedTvRows.Count > 0;
        }

        [RelayCommand]
        private async Task RestoreSavedTv(SavedTvRow? row)
        {
            if (row is null || SavedTvs.Get(row.Key) is not { } tv)
                return;
            await OpenRestoreAsync(tv.Profile);
        }

        /// <summary>"Restore a TV from a file": a list saved here, on another computer or by the CLI.</summary>
        [RelayCommand]
        private async Task RestoreTvFromFile()
        {
            if (await InstalledAppsViewModel.PickProfileAsync(_dialogService) is { } profile)
                await OpenRestoreAsync(profile);
        }

        private async Task OpenRestoreAsync(TvProfile profile)
        {
            SavedTvsStatus = string.Empty;
            if (RestoreProfile is null || string.IsNullOrWhiteSpace(AppSettings.Default.TvIp))
            {
                SavedTvsStatus = "statusSavedTvNoTv".Localized();
                return;
            }
            await RestoreProfile(profile);
        }

        /// <summary>The saved list as a file. No typed values in it: those are added from On the TV, where the TV is read.</summary>
        [RelayCommand]
        private async Task SaveSavedTvToFile(SavedTvRow? row)
        {
            if (row is null || SavedTvs.Get(row.Key) is not { } tv || GetActiveStorageProvider() is not { } storage)
                return;
            try
            {
                var safe = string.Concat(row.Name.Select(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '-' : c));
                var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
                {
                    Title = "lblRestoreSaveList".Localized(),
                    SuggestedFileName = $"{safe}-{tv.Profile.SavedAtUtc.ToLocalTime():yyyy-MM-dd}.a2stv",
                    DefaultExtension = "a2stv",
                    FileTypeChoices = new[] { new FilePickerFileType("Apps2Samsung TV app list") { Patterns = new[] { "*.a2stv" } } },
                });
                if (file is null)
                    return;
                await using (var stream = await file.OpenWriteAsync())
                await using (var writer = new StreamWriter(stream))
                    await writer.WriteAsync(TvProfiles.Serialize(tv.Profile));
                SavedTvsStatus = string.Format("statusRestoreSaved".Localized(), tv.Profile.Apps.Count, file.Name);
            }
            catch (Exception ex)
            {
                SavedTvsStatus = ex.Message;
            }
        }

        [RelayCommand]
        private async Task ForgetSavedTv(SavedTvRow? row)
        {
            if (row is null)
                return;
            if (!await _dialogService.ShowConfirmationAsync("lblSavedTvForget".Localized(),
                    string.Format("lblSavedTvForgetAsk".Localized(), row.Name), "lblSavedTvForget".Localized(), "lblCancel".Localized()))
                return;
            SavedTvs.Forget(row.Key);
        }
    }
}
