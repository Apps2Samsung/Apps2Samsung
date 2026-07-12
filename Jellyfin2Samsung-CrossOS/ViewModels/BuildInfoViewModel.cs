using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Helpers;
using Apps2Samsung.Helpers.Core;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Services;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    public partial class BuildInfoViewModel : ViewModelBase
    {
        public ObservableCollection<BuildVersion> JellyfinVersions { get; } = new();
        public ObservableCollection<BuildVersion> CommunityApps { get; } = new();

        public ObservableCollection<ProviderOption> ProviderOptions { get; } = new();

        [ObservableProperty]
        private ProviderOption? selectedProviderOption;

        // Selected row in each table — selecting one drives the preview panel
        // (so the user no longer has to use the dropdown above the preview).
        [ObservableProperty]
        private BuildVersion? selectedJellyfinVersion;

        [ObservableProperty]
        private BuildVersion? selectedCommunityApp;

        partial void OnSelectedJellyfinVersionChanged(BuildVersion? value)
        {
            if (value is null) return;
            SelectedCommunityApp = null;   // one active selection across both tables
            SelectPreviewFor(value.FileName);
        }

        partial void OnSelectedCommunityAppChanged(BuildVersion? value)
        {
            if (value is null) return;
            SelectedJellyfinVersion = null;
            SelectPreviewFor(value.FileName);
        }

        // Point the preview at the ProviderOption matching the picked row.
        // ProviderOptions.DisplayName is built from the same names shown in the
        // tables, so an exact match works; fall back to a contains match.
        private void SelectPreviewFor(string name)
        {
            name = (name ?? string.Empty).Trim();
            if (name.Length == 0) return;

            var match = ProviderOptions.FirstOrDefault(o =>
                            string.Equals(o.DisplayName, name, StringComparison.OrdinalIgnoreCase))
                     ?? ProviderOptions.FirstOrDefault(o =>
                            name.Contains(o.DisplayName, StringComparison.OrdinalIgnoreCase)
                            || o.DisplayName.Contains(name, StringComparison.OrdinalIgnoreCase));

            if (match is not null)
                SelectedProviderOption = match;
        }

        private static readonly HttpClient _http = new();
        private static readonly ProviderManifestService _manifestService = new(_http);

        // GitHub's API requires a User-Agent (and benefits from the auth token);
        // the DI client has both. Fall back to the bare client if unavailable.
        private readonly HttpClient _apiHttp = App.Services.GetService<HttpClient>() ?? _http;
        // Shared parsing / release-tag helper (Apps2Samsung.Core) — single source with the mobile head.
        private readonly BuildInfoService _buildInfo = new(App.Services.GetService<HttpClient>() ?? _http);
        private readonly Dictionary<string, Bitmap?> _bitmapCache = new(StringComparer.OrdinalIgnoreCase);
        private ProviderManifest _manifest = new();

        private bool _isLoading;
        private int _rebuildVersion;

        private readonly ILocalizationService _localizationService =
            App.Services.GetRequiredService<ILocalizationService>();

        private string L(string key) => _localizationService.GetString(key);

        // Localized labels for the catalog window.
        public string LblCatalogTitle => L("catalogTitle");
        public string LblCatalogSubtitle => L("catalogSubtitle");
        public string LblJellyfinBuilds => L("catalogJellyfinBuilds");
        public string LblCommunityApps => L("catalogCommunityApps");
        public string LblAppPreview => L("catalogAppPreview");
        public string LblColFileName => L("catalogColFileName");
        public string LblColDescription => L("catalogColDescription");
        public string LblColApplication => L("catalogColApplication");
        public string LblColVersion => L("catalogColVersion");
        public string LblNoPreview => L("catalogNoPreview");
        public string LblNoThumbnail => L("catalogNoThumbnail");
        public string LblClose => L("btn_Close");

        private void RefreshLocalizedLabels()
        {
            OnPropertyChanged(nameof(LblCatalogTitle));
            OnPropertyChanged(nameof(LblCatalogSubtitle));
            OnPropertyChanged(nameof(LblJellyfinBuilds));
            OnPropertyChanged(nameof(LblCommunityApps));
            OnPropertyChanged(nameof(LblAppPreview));
            OnPropertyChanged(nameof(LblColFileName));
            OnPropertyChanged(nameof(LblColDescription));
            OnPropertyChanged(nameof(LblColApplication));
            OnPropertyChanged(nameof(LblColVersion));
            OnPropertyChanged(nameof(LblNoPreview));
            OnPropertyChanged(nameof(LblNoThumbnail));
            OnPropertyChanged(nameof(LblClose));
        }

        public BuildInfoViewModel()
        {
            _localizationService.LanguageChanged += (_, __) => RefreshLocalizedLabels();

            CommunityApps.CollectionChanged += (_, __) =>
            {
                if (_isLoading) return;
                QueueRebuild();
            };

            JellyfinVersions.CollectionChanged += (_, __) =>
            {
                if (_isLoading) return;
                QueueRebuild();
            };

            _ = LoadAsync();
        }

        private static void SortByName(ObservableCollection<BuildVersion> collection)
        {
            var sorted = collection
                .OrderBy(b => b.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
            collection.Clear();
            foreach (var item in sorted)
                collection.Add(item);
        }

        private void QueueRebuild()
        {
            // Start a rebuild, but only the latest one is allowed to apply results
            var version = Interlocked.Increment(ref _rebuildVersion);
            _ = RebuildProviderOptionsAsync(version);
        }

        public async Task LoadAsync()
        {
            try
            {
                _isLoading = true;

                _manifest = await _manifestService.GetAsync();

                var jellyfinMd = await _http.GetStringAsync(AppSettings.Default.ReleaseInfo);
                var communityMd = await _http.GetStringAsync(AppSettings.Default.CommunityInfo);

                JellyfinVersions.Clear();
                CommunityApps.Clear();

                // Core Jellyfin builds come from one upstream release; tag their rows
                // with that provider's URL so they all get its release version.
                var coreUrl = _manifest.Providers
                    .FirstOrDefault(p => p.Url?.Contains("jellyfin-tizen-builds", StringComparison.OrdinalIgnoreCase) == true)
                    ?.Url ?? string.Empty;

                foreach (var item in BuildInfoService.ParseVersionsTable(jellyfinMd))
                    JellyfinVersions.Add(new BuildVersion { FileName = item.Name, Description = item.Description });
                foreach (var v in JellyfinVersions)
                    v.RepoUrl = coreUrl;

                foreach (var provider in _manifest.Providers)
                {
                    if (provider.BuildInfo is null || string.IsNullOrWhiteSpace(provider.BuildInfo.Name))
                        continue;

                    JellyfinVersions.Add(new BuildVersion
                    {
                        FileName = provider.BuildInfo.Name,
                        Description = provider.BuildInfo.Description,
                        RepoUrl = provider.Url ?? string.Empty
                    });
                }

                foreach (var item in BuildInfoService.ParseApplicationsTable(communityMd))
                    CommunityApps.Add(new BuildVersion { FileName = item.Name, Description = item.Description, Version = item.Version });

                // Sort both tables A-Z / 0-9, matching the release list ordering.
                SortByName(JellyfinVersions);
                SortByName(CommunityApps);
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to load build info: {ex}");
            }
            finally
            {
                _isLoading = false;
            }

            // Community versions come from the README; Jellyfin + forks are fetched
            // from their GitHub release (the same source the installer uses).
            await ApplyProviderVersionsAsync();

            // Do a single authoritative rebuild after load finishes
            var version = Interlocked.Increment(ref _rebuildVersion);
            await RebuildProviderOptionsAsync(version);

            if (SelectedProviderOption is null && ProviderOptions.Count > 0)
                SelectedProviderOption = ProviderOptions[0];
        }

        private async Task RebuildProviderOptionsAsync(int version)
        {
            // If a newer rebuild was queued, abandon this one
            if (version != Volatile.Read(ref _rebuildVersion))
                return;

            // Built from the loaded manifest — single source of truth for preview URLs.
            var communityPreviewUrls = _manifest.CommunityApps
                .Where(c => !string.IsNullOrWhiteSpace(c.MatchName) && !string.IsNullOrWhiteSpace(c.PreviewImage))
                .ToDictionary(c => c.MatchName, c => c.PreviewImage, StringComparer.OrdinalIgnoreCase);

            // Provider-level preview overrides keyed by buildInfo.name (e.g. "Moonfin", "Litefin").
            var jellyfinOverrides = _manifest.Providers
                .Where(p => p.BuildInfo is { } bi
                            && !string.IsNullOrWhiteSpace(bi.Name)
                            && !string.IsNullOrWhiteSpace(bi.PreviewImage))
                .ToDictionary(p => p.BuildInfo!.Name, p => p.BuildInfo!.PreviewImage!, StringComparer.OrdinalIgnoreCase);

            var jellyfinBitmap = string.IsNullOrWhiteSpace(_manifest.PreviewImages.Jellyfin)
                ? null
                : await LoadBitmapAsync(_manifest.PreviewImages.Jellyfin);

            // Build locally (don’t mutate ObservableCollection from background thread)
            var built = new List<ProviderOption>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void AddIfNew(string name, Bitmap? bmp)
            {
                name = (name ?? string.Empty).Trim();
                if (name.Length == 0) return;
                if (!seen.Add(name)) return;

                built.Add(new ProviderOption
                {
                    DisplayName = name,
                    PreviewImage = bmp
                });
            }

            // 1) Jellyfin top entry
            AddIfNew("Jellyfin", jellyfinBitmap);

            // 2) Community apps
            foreach (var app in CommunityApps)
            {
                var name = (app.FileName ?? string.Empty).Trim();
                if (name.Length == 0) continue;

                var url = communityPreviewUrls.FirstOrDefault(kvp =>
                    name.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)).Value;

                var bmp = url is not null ? await LoadBitmapAsync(url) : null;

                AddIfNew(name, bmp);
            }

            // 3) Jellyfin builds (default Jellyfin image, override forks like Moonfin)
            foreach (var build in JellyfinVersions)
            {
                var name = (build.FileName ?? string.Empty).Trim();
                if (name.Length == 0) continue;

                Bitmap? bmp = jellyfinBitmap;

                var overrideUrl = jellyfinOverrides.FirstOrDefault(kvp =>
                    name.Contains(kvp.Key, StringComparison.OrdinalIgnoreCase)).Value;

                if (overrideUrl is not null)
                    bmp = await LoadBitmapAsync(overrideUrl);

                AddIfNew(name, bmp);
            }

            // If a newer rebuild was queued while we were downloading images, abandon this one
            if (version != Volatile.Read(ref _rebuildVersion))
                return;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ProviderOptions.Clear();
                foreach (var opt in built)
                    ProviderOptions.Add(opt);

                if (SelectedProviderOption is null && ProviderOptions.Count > 0)
                    SelectedProviderOption = ProviderOptions[0];
            });
        }

        // Fetch each distinct provider URL's latest release tag once and apply it
        // to every row from that provider (core builds share one release tag).
        private async Task ApplyProviderVersionsAsync()
        {
            var urls = JellyfinVersions
                .Select(v => v.RepoUrl)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var url in urls)
            {
                var tag = await _buildInfo.GetLatestReleaseTagAsync(url);
                if (string.IsNullOrWhiteSpace(tag))
                    continue;

                foreach (var v in JellyfinVersions)
                    if (string.Equals(v.RepoUrl, url, StringComparison.OrdinalIgnoreCase))
                        v.Version = tag;
            }
        }

        private async Task<Bitmap?> LoadBitmapAsync(string url)
        {
            if (_bitmapCache.TryGetValue(url, out var cached))
                return cached;

            try
            {
                var bytes = await _http.GetByteArrayAsync(url);
                await using var ms = new MemoryStream(bytes);
                var bmp = new Bitmap(ms);
                _bitmapCache[url] = bmp;
                return bmp;
            }
            catch (Exception ex)
            {
                Trace.WriteLine($"Failed to load image '{url}': {ex.Message}");
                _bitmapCache[url] = null;
                return null;
            }
        }

        [RelayCommand]
        private void Close()
        {
            OnRequestClose?.Invoke();
        }

        public event Action? OnRequestClose;
    }
}
