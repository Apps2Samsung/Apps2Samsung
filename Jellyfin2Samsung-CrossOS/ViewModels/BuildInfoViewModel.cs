using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
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

        private static readonly HttpClient _http = new();
        private static readonly ProviderManifestService _manifestService = new(_http);
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

                ParseVersionsTable(jellyfinMd, JellyfinVersions);

                foreach (var provider in _manifest.Providers)
                {
                    if (provider.BuildInfo is null || string.IsNullOrWhiteSpace(provider.BuildInfo.Name))
                        continue;

                    JellyfinVersions.Add(new BuildVersion
                    {
                        FileName = provider.BuildInfo.Name,
                        Description = provider.BuildInfo.Description
                    });
                }

                ParseApplicationsTable(communityMd, CommunityApps);

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

        // -------- your existing parsing methods (unchanged) --------

        private static string CleanText(string input)
        {
            var text = RegexPatterns.BuildInfo.MarkdownBold.Replace(input, "$1");
            text = RegexPatterns.BuildInfo.EmojiRange.Replace(text, "");
            return text.Trim();
        }

        private void ParseVersionsTable(string md, ObservableCollection<BuildVersion> target)
        {
            var match = RegexPatterns.BuildInfo.VersionsTable.Match(md);
            if (!match.Success) return;

            var table = match.Groups["table"].Value;
            var rows = RegexPatterns.BuildInfo.TableRow2Columns.Matches(table);

            bool headerSkipped = false;

            foreach (System.Text.RegularExpressions.Match row in rows)
            {
                var col1 = row.Groups[1].Value.Trim();
                var col2 = row.Groups[2].Value.Trim();

                if (!headerSkipped && col1.Equals("File name", StringComparison.OrdinalIgnoreCase))
                {
                    headerSkipped = true;
                    continue;
                }

                if (col1.StartsWith("-"))
                    continue;

                target.Add(new BuildVersion
                {
                    FileName = CleanText(col1),
                    Description = CleanText(col2)
                });
            }
        }

        private void ParseApplicationsTable(string md, ObservableCollection<BuildVersion> target)
        {
            var match = RegexPatterns.BuildInfo.ApplicationsTable.Match(md);
            if (!match.Success) return;

            var table = match.Groups["table"].Value;
            var rows = RegexPatterns.BuildInfo.TableRow3Columns.Matches(table);

            bool headerSkipped = false;

            foreach (System.Text.RegularExpressions.Match row in rows)
            {
                var col1 = row.Groups[1].Value.Trim();
                var col2 = row.Groups[2].Value.Trim();
                var col3 = row.Groups[3].Value.Trim();

                if (!headerSkipped && col1.Contains("Application", StringComparison.OrdinalIgnoreCase))
                {
                    headerSkipped = true;
                    continue;
                }

                if (col1.StartsWith("-"))
                    continue;

                target.Add(new BuildVersion
                {
                    FileName = CleanText(col1),
                    Description = CleanText(col2)
                });
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
