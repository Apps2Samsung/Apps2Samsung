using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Catalog;
using Apps2Samsung.Models;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// The app sheet (#755): preview, description (#835), category line, the version or variant
    /// to install, and the Install button for the selected TV. Built by the main view model with everything it needs,
    /// so the window only shows and hands back the chosen asset.
    /// </summary>
    public partial class AppDetailViewModel : ObservableObject
    {
        private readonly HttpClient _http;
        private readonly string? _previewUrl;

        public GitHubRelease Release { get; }
        public string Name => Release.Name;
        /// <summary>What the app is, from the catalog (#835); the paragraph is hidden when the catalog has none.</summary>
        public string Description => Release.Description;
        public bool HasDescription => !string.IsNullOrWhiteSpace(Release.Description);
        /// <summary>The list row's info line: the category, and the variant count for a grouped app.</summary>
        public string Subline { get; }
        /// <summary>"by owner" and the repository link under the description (#820); hidden without a repository.</summary>
        public string AuthorText { get; }
        public Uri? SourceUri { get; }
        public bool HasSource => SourceUri is not null;
        public string SourceCodeText { get; }
        public bool RequiresPartner => Release.RequiresPartner;
        public string VersionsTitle { get; }
        public string Published { get; }
        public ObservableCollection<Asset> Assets { get; }
        public string InstallText { get; }
        public bool CanInstall { get; }
        public string PartnerNeededTitle { get; }
        public string PartnerNeededBody { get; }

        [ObservableProperty]
        private Asset? selectedAsset;

        private readonly Action<string, bool> _setFavorite;

        [ObservableProperty]
        private bool isFavorite;

        public string FavoriteText => IsFavorite ? _localize("lblRemoveFavorite") : _localize("lblAddFavorite");
        public string FavoriteGlyph => IsFavorite ? "★" : "☆";
        private readonly Func<string, string> _localize;

        partial void OnIsFavoriteChanged(bool value)
        {
            OnPropertyChanged(nameof(FavoriteText));
            OnPropertyChanged(nameof(FavoriteGlyph));
        }

        [RelayCommand]
        private void ToggleFavorite()
        {
            IsFavorite = !IsFavorite;
            _setFavorite(Release.Name, IsFavorite);
        }

        [ObservableProperty]
        private Bitmap? preview;

        /// <summary>
        /// "Show all versions" beside the Version title, for an app with a version history (Jellyfin):
        /// the stored preference (ShowAllJellyfinVersions), applied to the catalog when the sheet closes.
        /// </summary>
        public bool CanShowAllVersions { get; }
        public string ShowAllVersionsText { get; }

        [ObservableProperty]
        private bool showAllVersions;

        partial void OnShowAllVersionsChanged(bool value)
        {
            if (Helpers.AppSettings.Default.ShowAllJellyfinVersions == value)
                return;
            Helpers.AppSettings.Default.ShowAllJellyfinVersions = value;
            Helpers.AppSettings.Default.Save();
        }

        public bool HasPreview => Preview is not null;

        public AppDetailViewModel(GitHubRelease release, string? previewUrl, string subline,
            string? tvName, bool tvReady, HttpClient http, Func<string, string> localize,
            bool isFavorite = false, Action<string, bool>? setFavorite = null, bool canShowAllVersions = false)
        {
            CanShowAllVersions = canShowAllVersions;
            ShowAllVersions = Helpers.AppSettings.Default.ShowAllJellyfinVersions;
            ShowAllVersionsText = localize("lblShowAllJellyfinVersions");
            Release = release;
            _localize = localize;
            _setFavorite = setFavorite ?? ((_, _) => { });
            IsFavorite = isFavorite;
            _previewUrl = previewUrl;
            _http = http;
            Subline = subline;
            AuthorText = string.IsNullOrEmpty(release.Author) ? string.Empty : string.Format(localize("lblByAuthor"), release.Author);
            SourceUri = Uri.TryCreate(release.SourceUrl, UriKind.Absolute, out var source) ? source : null;
            SourceCodeText = localize("lblSourceCode");
            VersionsTitle = localize(release.HasVariants ? "lblVariant" : "lblVersion");
            Published = DateTime.TryParse(release.PublishedAt, out var date) ? date.ToShortDateString() : string.Empty;
            Assets = new ObservableCollection<Asset>(release.Assets);
            SelectedAsset = Assets.FirstOrDefault(a => a.IsDefault) ?? Assets.FirstOrDefault();
            CanInstall = tvReady && !string.IsNullOrEmpty(tvName);
            InstallText = string.IsNullOrEmpty(tvName) ? localize("lblChooseTvFirst") : string.Format(localize("lblInstallOn"), tvName);
            PartnerNeededTitle = localize("lblPartnerNeededTitle");
            PartnerNeededBody = localize("lblPartnerNeededBody");
        }

        partial void OnPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPreview));

        /// <summary>Loads the preview after the window is up, so the sheet opens at once.</summary>
        public async Task LoadPreviewAsync()
        {
            if (string.IsNullOrWhiteSpace(_previewUrl))
                return;
            try
            {
                var bytes = await _http.GetByteArrayAsync(_previewUrl);
                await using var stream = new MemoryStream(bytes);
                Preview = new Bitmap(stream);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"Preview '{_previewUrl}' failed: {ex.Message}");
            }
        }
    }
}
