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
        private readonly AddLatestRelease _addLatestRelease;

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

        public AppIconsViewModel(
            ILocalizationService localizationService,
            FileHelper fileHelper,
            HttpClient httpClient)
        {
            _localizationService = localizationService;
            _fileHelper = fileHelper;
            _providerManifestService = new ProviderManifestService(httpClient);
            _addLatestRelease = new AddLatestRelease(httpClient);

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
                var manifest = await _providerManifestService.GetAsync();

                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var entries = new List<AppIconEntry>();

                void Add(string display, string key)
                {
                    key = key?.Trim() ?? string.Empty;
                    if (string.IsNullOrEmpty(key) || !seen.Add(key))
                        return;

                    entries.Add(new AppIconEntry
                    {
                        DisplayName = string.IsNullOrWhiteSpace(display) ? key : display.Trim(),
                        Key = key,
                        HasOblong = HasBundledOblong(key),
                    });
                }

                foreach (var provider in manifest.Providers)
                {
                    if (provider.ExpandAssets)
                    {
                        // Community bundle: derive one icon entry per real .wgt asset in the latest
                        // release, so EVERY community app is offered and the list stays current with
                        // the repo — instead of a hardcoded subset (see #462). Custom launcher icons
                        // only apply to web apps, so skip native .tpk assets. Best-effort: a fetch
                        // failure just yields no community rows rather than breaking the editor.
                        if (string.IsNullOrWhiteSpace(provider.Url))
                            continue;
                        try
                        {
                            var releases = await _addLatestRelease.GetReleasesAsync(
                                provider.Url, provider.Prefix, provider.DisplayName, provider.Take);
                            foreach (var r in releases)
                                foreach (var asset in r.Assets)
                                {
                                    if (!asset.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
                                        continue;
                                    var name = Path.GetFileNameWithoutExtension(asset.FileName);
                                    Add(name, name);
                                }
                        }
                        catch (Exception ex)
                        {
                            Trace.WriteLine($"[AppIcons] Could not expand community assets from {provider.Url}: {ex.Message}");
                        }
                        continue;
                    }

                    // All Jellyfin builds share the "Jellyfin" file-name root, so collapse the
                    // variants (AVPlay, Legacy, …) into a single entry that matches any of them.
                    if (string.IsNullOrWhiteSpace(provider.DisplayName) ||
                        provider.DisplayName.StartsWith("Jellyfin", StringComparison.OrdinalIgnoreCase))
                        Add("Jellyfin", "Jellyfin");
                    else
                        Add(provider.DisplayName, provider.DisplayName);
                }

                var map = LoadIconMap();
                foreach (var entry in entries)
                {
                    entry.Value = map.TryGetValue(entry.Key, out var v) ? v : string.Empty;
                    RefreshSummary(entry);
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    AppIcons.Clear();
                    foreach (var entry in entries)
                        AppIcons.Add(entry);
                });
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"[AppIcons] Failed to build the app-icon list: {ex.Message}");
            }
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

        private static bool HasBundledOblong(string key)
            => key.IndexOf("tvapp", StringComparison.OrdinalIgnoreCase) >= 0
            || key.IndexOf("litefin", StringComparison.OrdinalIgnoreCase) >= 0;

        private static Dictionary<string, string> LoadIconMap()
        {
            var json = AppSettings.Default.CustomAppIconsJson;
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

            foreach (var entry in AppIcons)
                RefreshSummary(entry);
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }
}
