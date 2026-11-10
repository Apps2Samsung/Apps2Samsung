using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using System.Threading.Tasks;
using Apps2Samsung.Catalog;
using Apps2Samsung.Catalog.Repositories;

namespace Apps2Samsung.Views
{
    /// <summary>
    /// Add a source of your own: a GitHub repository or release, or a direct .wgt/.tpk link,
    /// checked with <see cref="UserSources.Validate"/> against the sources already there. Closes
    /// with the new <see cref="UserSource"/>, or null when cancelled.
    /// </summary>
    public partial class UserSourceWindow : Window
    {
        public sealed record Choice(string Id, string Name);

        private readonly Func<string, string> _localize = key => key;
        private readonly IReadOnlyList<UserSource> _existing = Array.Empty<UserSource>();
        // A signed repository (#1018): read it, then the key step; null on a head path without one.
        private readonly Func<string, Task<RepositoryProbe>>? _probe;
        private RepositoryProbe? _probed;
        private string _probedUrl = string.Empty;
        // Reviewing a changed key: the stored source, replaced by the one approved with the new key.
        private readonly UserSource? _reviewing;

        public string TitleText { get; } = string.Empty;
        public string HintText { get; } = string.Empty;
        public string UrlLabel { get; } = string.Empty;
        public string UrlHint { get; } = string.Empty;
        public string NameLabel { get; } = string.Empty;
        public string CategoryLabel { get; } = string.Empty;
        public string PartnerLabel { get; } = string.Empty;
        public string PartnerHint { get; } = string.Empty;
        public string AddText { get; } = string.Empty;
        public string CancelText { get; } = string.Empty;

        public UserSourceWindow()
        {
            InitializeComponent();
        }

        public UserSourceWindow(Func<string, string> localize, IReadOnlyList<UserSource> existing,
            Func<string, Task<RepositoryProbe>>? probe = null, UserSource? reviewing = null) : this()
        {
            _probe = probe;
            _reviewing = reviewing;
            _localize = localize;
            _existing = existing;
            TitleText = localize("lblAddSourceTitle");
            HintText = localize("hintAddSourceForm");
            UrlLabel = localize("lblUserSourceUrl");
            UrlHint = localize("hintUserSourceUrl");
            NameLabel = localize("lblUserSourceName") + $" ({localize("lblOptional")})";
            CategoryLabel = localize("lblUserSourceCategory");
            PartnerLabel = localize("lblUserSourcePartner");
            PartnerHint = localize("hintUserSourcePartner");
            AddText = localize("lblAdd");
            CancelText = localize("lblCancel");
            DataContext = this;

            // The catalog's categories (#1054), "Other" preselected wherever the catalog puts it.
            var categories = AppCategories.Ordered;
            CategoryBox.ItemsSource = categories.Select(id => new Choice(id, AppCategories.DisplayName(id, localize))).ToList();
            CategoryBox.SelectedIndex = Math.Max(0, categories.ToList().IndexOf(AppCategories.Other));
            UrlBox.Watermark = "github.com/yourname/your-app";

            Opened += async (_, _) =>
            {
                if (_reviewing is null)
                {
                    UrlBox.Focus();
                    return;
                }
                // The key changed: read the repository again and go straight to the key step.
                UrlBox.Text = _reviewing.Url;
                NameBox.Text = _reviewing.Name;
                await ProbeAndShowKeyAsync(_reviewing.Url.Trim());
            };
        }

        /// <summary>A signed repository was typed: read it and show the key, or say why it can't be added.</summary>
        private async Task ProbeAndShowKeyAsync(string url)
        {
            BusyText.Text = _localize("lblRepoReading");
            BusyText.IsVisible = true;
            UrlError.IsVisible = false;
            RepositoryProbe probe;
            try { probe = await _probe!(url); }
            finally { BusyText.IsVisible = false; }
            if (!probe.Ok)
            {
                UrlError.Text = string.Format(_localize("errRepoNotAdded"), $"[{probe.Reason}] {probe.Message}");
                UrlError.IsVisible = true;
                return;
            }
            _probed = probe;
            _probedUrl = url;
            var m = probe.Manifest!;
            var reviewing = _reviewing is not null;
            KeyTitle.Text = _localize(reviewing ? "lblRepoKeyChangedTitle" : "lblRepoKeyTitle");
            KeyHint.Text = reviewing ? string.Format(_localize("hintRepoKeyChanged"), m.Repository.Name) : _localize("hintRepoKey");
            WasRow.IsVisible = NowLabel.IsVisible = reviewing;
            WasLabel.Text = _localize("lblRepoKeyWas");
            NowLabel.Text = _localize("lblRepoKeyNow");
            WasText.Text = reviewing ? RepositoryVerifier.FormatFingerprint(_reviewing!.Fingerprint ?? string.Empty) : string.Empty;
            FingerprintGroups.ItemsSource = RepositoryVerifier.FormatFingerprint(probe.Fingerprint!).Split(' ');
            CopyButton.Content = _localize("lblRepoCopyFingerprint");
            RepoLetter.Text = Apps2Samsung.Design.AppTiles.Letter(m.Repository.Name);
            RepoName.Text = m.Repository.Name;
            RepoBy.Text = string.Format(_localize("lblRepoBy"), m.Repository.Maintainer, m.Providers.Count);
            FromLabel.Text = _localize("lblRepoFrom");
            FromText.Text = probe.Domain;
            SignedMeans.Text = _localize("hintRepoSignedMeans");
            TrustButton.Content = _localize(reviewing ? "lblRepoTrustNewKey" : "lblRepoKeyMatches");
            RejectButton.Content = _localize("lblRepoDontAdd");
            FormPanel.IsVisible = false;
            KeyPanel.IsVisible = true;
        }

        private void OnTrustClick(object? sender, RoutedEventArgs e)
        {
            if (_probed is null)
                return;
            var approved = _probed.Approve(_probedUrl, NameBox.Text);
            approved.Category = (CategoryBox.SelectedItem as Choice)?.Id ?? AppCategories.Other;
            Close(approved);
        }

        private async void OnCopyClick(object? sender, RoutedEventArgs e)
        {
            if (_probed?.Fingerprint is { } fp && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(fp);
        }

        private UserSource Read() => new()
        {
            Url = UrlBox.Text?.Trim() ?? string.Empty,
            Name = NameBox.Text?.Trim() ?? string.Empty,
            Category = (CategoryBox.SelectedItem as Choice)?.Id ?? AppCategories.Other,
            Partner = PartnerBox.IsChecked == true,
        };

        private async void OnAddClick(object? sender, RoutedEventArgs e)
        {
            var source = Read();
            var errors = UserSources.Validate(source, _existing);
            if (errors.Count == 0 && _probe is not null && UserSources.IsSignedRepository(source.Url))
            {
                await ProbeAndShowKeyAsync(source.Url);
                return;
            }
            Show(UrlError, UserSources.FieldUrl);
            Show(NameError, UserSources.FieldName);
            if (errors.Count > 0)
            {
                (errors.ContainsKey(UserSources.FieldUrl) ? UrlBox : NameBox).Focus();
                return;
            }
            Close(source);

            void Show(TextBlock error, string field)
            {
                var bad = errors.TryGetValue(field, out var key);
                error.Text = bad ? _localize(key!) : string.Empty;
                error.IsVisible = bad;
            }
        }

        private void OnCancelClick(object? sender, RoutedEventArgs e) => Close(null);
    }
}
