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
        /// <summary>"All apps by owner": the tooltip on the name; a click closes the sheet with <see cref="SearchAuthorRequested"/> set (#888).</summary>
        public string AuthorTip { get; }
        public bool HasAuthor => !string.IsNullOrEmpty(Release.Author);
        /// <summary>The user clicked the developer's name: the main window searches the catalog for them.</summary>
        public bool SearchAuthorRequested { get; set; }
        public Uri? SourceUri { get; }
        public bool HasSource => SourceUri is not null;
        public string SourceCodeText { get; }
        public bool RequiresPartner => Release.RequiresPartner;
        /// <summary>The 18+ badge by the title (#850); the sheet only opens for such an app once the user opted in.</summary>
        public bool IsAdult => Release.IsAdult;
        /// <summary>The root badge by the title: the package gains root on the TV, and the install asks first.</summary>
        public bool IsRoot => Release.IsRoot;
        public string VersionsTitle { get; }
        public string Published { get; }
        public ObservableCollection<Asset> Assets { get; }
        public string InstallText { get; }
        /// <summary>The TV is ready and the selected build runs on it (TV-Store#47).</summary>
        public bool CanInstall => _tvCanInstall && !SelectedNeedsNewer;
        private readonly bool _tvCanInstall;
        private readonly Version? _tvVersion;
        private readonly string _tvLabel;
        private bool SelectedNeedsNewer => SelectedAsset is { } a && TizenRequirement.NeedsNewer(a, _tvVersion);
        /// <summary>"Tizen 6.0 or newer" by the title for the selected build, "Needs Tizen 6.0" when this TV is too old; empty when the catalog does not say.</summary>
        public string TizenBadgeText => SelectedAsset is { } a && a.HasRequiredVersion
            ? string.Format(_localize(SelectedNeedsNewer ? "lblNeedsTizenBadge" : "lblTizenOrNewer"), a.RequiredVersion)
            : string.Empty;
        public bool HasTizenBadgeWarn => SelectedNeedsNewer;
        public bool HasTizenBadgeMute => !SelectedNeedsNewer && TizenBadgeText.Length > 0;
        /// <summary>Why Install is off: the selected build asks for a newer Tizen than the selected TV runs.</summary>
        public string TizenHint => SelectedNeedsNewer && SelectedAsset is { } a
            ? string.Format(_localize("hintNeedsTizen"), Release.Name, a.RequiredVersion, _tvLabel, _tvVersion)
            : string.Empty;
        public bool HasTizenHint => TizenHint.Length > 0;
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

        /// <summary>"Install under a new package id" (#839): for a TV that has this package's own id blocked.</summary>
        [ObservableProperty]
        private bool newPackageId;
        public string NewPackageIdText { get; }
        public string NewPackageIdHint { get; }

        /// <summary>
        /// "Back to its own package id" (#839): forget the rename remembered for the TV and install
        /// under the package's own id. Offered only when this app was renamed on some TV; the sheet
        /// has no DUID to tell which.
        /// </summary>
        [ObservableProperty]
        private bool ownPackageId;
        public bool HasPackageIdRename { get; }
        public string OwnPackageIdText { get; }
        public string OwnPackageIdHint { get; }

        public AppDetailViewModel(GitHubRelease release, string? previewUrl, string subline,
            string? tvName, bool tvReady, HttpClient http, Func<string, string> localize,
            bool isFavorite = false, Action<string, bool>? setFavorite = null, bool canShowAllVersions = false,
            Version? tvVersion = null)
        {
            _tvVersion = tvVersion;
            _tvLabel = tvName ?? string.Empty;
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
            AuthorTip = string.IsNullOrEmpty(release.Author) ? string.Empty : string.Format(localize("lblAppsByAuthor"), release.Author);
            SourceUri = Uri.TryCreate(release.SourceUrl, UriKind.Absolute, out var source) ? source : null;
            SourceCodeText = localize("lblSourceCode");
            VersionsTitle = localize(release.HasVariants ? "lblVariant" : "lblVersion");
            Published = DateTime.TryParse(release.PublishedAt, out var date) ? date.ToShortDateString() : string.Empty;
            Assets = new ObservableCollection<Asset>(release.Assets);
            // Land on a build this TV runs when the default is too new for it (TV-Store#47).
            SelectedAsset = TizenRequirement.FirstFitting(Assets, Assets.FirstOrDefault(a => a.IsDefault) ?? Assets.FirstOrDefault(), tvVersion);
            _tvCanInstall = tvReady && !string.IsNullOrEmpty(tvName);
            InstallText = string.IsNullOrEmpty(tvName) ? localize("lblChooseTvFirst") : string.Format(localize("lblInstallOn"), tvName);
            PartnerNeededTitle = localize("lblPartnerNeededTitle");
            NewPackageIdText = localize("lblNewPackageId");
            NewPackageIdHint = localize("lblNewPackageIdHint");
            OwnPackageIdText = localize("lblOwnPackageId");
            OwnPackageIdHint = localize("lblOwnPackageIdHint");
            HasPackageIdRename = release.Assets.Any(a =>
                Apps2Samsung.Packaging.PackageIdRenameStore.IsRenamedAnywhere(Helpers.AppSettings.Default.PackageIdRenamesJson, a.PackageId));
            PartnerNeededBody = localize("lblPartnerNeededBody");
        }

        partial void OnPreviewChanged(Bitmap? value) => OnPropertyChanged(nameof(HasPreview));

        partial void OnSelectedAssetChanged(Asset? value)
        {
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(TizenBadgeText));
            OnPropertyChanged(nameof(HasTizenBadgeWarn));
            OnPropertyChanged(nameof(HasTizenBadgeMute));
            OnPropertyChanged(nameof(TizenHint));
            OnPropertyChanged(nameof(HasTizenHint));
        }

        // One or the other: a fresh id and the package's own id exclude each other.
        partial void OnNewPackageIdChanged(bool value) { if (value) OwnPackageId = false; }
        partial void OnOwnPackageIdChanged(bool value) { if (value) NewPackageId = false; }

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
