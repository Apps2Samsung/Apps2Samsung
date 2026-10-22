using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// "App icons" settings section: choose an installable app and give it a custom launcher icon —
    /// a user-supplied PNG, or (where one ships) the bundled 16:9 "oblong" tile. The choice is
    /// remembered per app in <see cref="AppSettings.CustomAppIconsJson"/> and written into the
    /// package at install by <c>CustomIconPackagePatcher</c>.
    /// </summary>
    public partial class AppIconsViewModel : ViewModelBase, IDisposable
    {
        private readonly ILocalizationService _localizationService;
        private readonly FileHelper _fileHelper;
        private readonly ProviderManifestService _providerManifestService;
        private readonly Apps2Samsung.Catalog.AppCatalog _appCatalog;

        public ObservableCollection<AppIconEntry> AppIcons { get; } = new();

        [ObservableProperty]
        private AppIconEntry? selectedAppIcon;

        public bool HasSelectedAppIcon => SelectedAppIcon != null;

        public string LblAppIcons => _localizationService.GetString("lblAppIcons");
        public string LblAppIconsHint => _localizationService.GetString("lblAppIconsHint");
        public string LblIconOblong => _localizationService.GetString("lblIconOblong");
        public string LblIconCustom => _localizationService.GetString("lblIconCustom");
        public string LblIconReset => _localizationService.GetString("lblIconReset");
        public string LblIconDefault => _localizationService.GetString("lblIconDefault");
        public string LblAppTitle => _localizationService.GetString("lblAppTitle");
        public string LblAppTitleHint => _localizationService.GetString("lblAppTitleHint");

        public AppIconsViewModel(
            ILocalizationService localizationService,
            FileHelper fileHelper,
            HttpClient httpClient,
            Apps2Samsung.Catalog.AppCatalog appCatalog)
        {
            _localizationService = localizationService;
            _fileHelper = fileHelper;
            _providerManifestService = new ProviderManifestService(httpClient);
            _appCatalog = appCatalog;

            _localizationService.LanguageChanged += OnLanguageChanged;

            _ = LoadAppIconsAsync();
        }

        partial void OnSelectedAppIconChanged(AppIconEntry? value)
        {
            OnPropertyChanged(nameof(HasSelectedAppIcon));
            UseOblongIconCommand.NotifyCanExecuteChanged();
            BrowseCustomIconCommand.NotifyCanExecuteChanged();
            ResetIconCommand.NotifyCanExecuteChanged();
        }

        private async System.Threading.Tasks.Task LoadAppIconsAsync()
        {
            try
            {
                // The head fetches the manifest; the shared Core AppCatalog does the list shaping
                // (expand community assets, collapse Jellyfin variants, flag bundled oblong tiles),
                // so desktop and mobile present the same app list (#521).
                var manifest = await _providerManifestService.GetAsync();
                var catalog = await _appCatalog.BuildAsync(manifest);

                var iconMap = LoadIconMap();
                var titleMap = LoadTitleMap();

                var entries = catalog.Select(c =>
                {
                    var entry = new AppIconEntry
                    {
                        DisplayName = c.DisplayName,
                        Key = c.Key,
                        HasOblong = c.HasOblong,
                        Value = iconMap.TryGetValue(c.Key, out var v) ? v : string.Empty,
                        Title = titleMap.TryGetValue(c.Key, out var t) ? t : string.Empty,
                    };
                    RefreshSummary(entry);
                    return entry;
                }).ToList();

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Drop the previous rows' change subscriptions before rebuilding the list.
                    foreach (var old in AppIcons)
                        old.PropertyChanged -= OnEntryPropertyChanged;

                    AppIcons.Clear();
                    foreach (var entry in entries)
                    {
                        entry.PropertyChanged += OnEntryPropertyChanged;
                        AppIcons.Add(entry);
                    }
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AppIcons] Failed to build the app-icon list: {ex.Message}");
            }
        }

        // A custom title is edited inline (TextBox two-way bound to the entry), so persist it whenever
        // the entry's Title changes — mirroring how SetIcon auto-saves the icon choice.
        private void OnEntryPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppIconEntry.Title) && sender is AppIconEntry entry)
                SaveTitle(entry);
        }

        private void SaveTitle(AppIconEntry entry)
        {
            var map = LoadTitleMap();
            var title = entry.Title?.Trim() ?? string.Empty;
            if (string.IsNullOrEmpty(title))
                map.Remove(entry.Key);
            else
                map[entry.Key] = title;

            AppSettings.Default.CustomAppTitlesJson = JsonSerializer.Serialize(map);
            AppSettings.Default.Save();
        }

        [RelayCommand(CanExecute = nameof(HasSelectedAppIcon))]
        private void UseOblongIcon()
        {
            if (SelectedAppIcon != null)
                SetIcon(SelectedAppIcon, AppIconEntry.OblongValue);
        }

        [RelayCommand(CanExecute = nameof(HasSelectedAppIcon))]
        private async System.Threading.Tasks.Task BrowseCustomIconAsync()
        {
            var entry = SelectedAppIcon;
            if (entry == null)
                return;

            var storageProvider = GetActiveStorageProvider();
            if (storageProvider == null)
                return;

            var path = await _fileHelper.BrowseImageFileAsync(storageProvider);
            if (!string.IsNullOrEmpty(path))
                SetIcon(entry, path);
        }

        [RelayCommand(CanExecute = nameof(HasSelectedAppIcon))]
        private void ResetIcon()
        {
            if (SelectedAppIcon != null)
                SetIcon(SelectedAppIcon, string.Empty);
        }

        private void SetIcon(AppIconEntry entry, string value)
        {
            entry.Value = value;
            RefreshSummary(entry);

            var map = LoadIconMap();
            if (string.IsNullOrEmpty(value))
                map.Remove(entry.Key);
            else
                map[entry.Key] = value;

            AppSettings.Default.CustomAppIconsJson = JsonSerializer.Serialize(map);
            AppSettings.Default.Save();
        }

        private void RefreshSummary(AppIconEntry entry)
        {
            entry.Summary = entry.IsDefault
                ? LblIconDefault
                : entry.IsOblong
                    ? LblIconOblong
                    : Path.GetFileName(entry.Value);
        }

        private static Dictionary<string, string> LoadIconMap() => LoadMap(AppSettings.Default.CustomAppIconsJson);

        private static Dictionary<string, string> LoadTitleMap() => LoadMap(AppSettings.Default.CustomAppTitlesJson);

        private static Dictionary<string, string> LoadMap(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(json) is { } parsed
                    ? new Dictionary<string, string>(parsed, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
            catch
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static IStorageProvider? GetActiveStorageProvider()
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                return (desktop.Windows.FirstOrDefault(w => w.IsActive) ?? desktop.MainWindow)?.StorageProvider;

            return null;
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            OnPropertyChanged(nameof(LblAppIcons));
            OnPropertyChanged(nameof(LblAppIconsHint));
            OnPropertyChanged(nameof(LblIconOblong));
            OnPropertyChanged(nameof(LblIconCustom));
            OnPropertyChanged(nameof(LblIconReset));
            OnPropertyChanged(nameof(LblIconDefault));
            OnPropertyChanged(nameof(LblAppTitle));
            OnPropertyChanged(nameof(LblAppTitleHint));

            foreach (var entry in AppIcons)
                RefreshSummary(entry);
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
            foreach (var entry in AppIcons)
                entry.PropertyChanged -= OnEntryPropertyChanged;
        }
    }
}
