using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Apps2Samsung.Certificate;

namespace Apps2Samsung.Views
{
    /// <summary>What the user chose when the certificates that come with the app had ended (#1153).</summary>
    public enum AuthorityChoice
    {
        /// <summary>Stop the install; nothing was changed.</summary>
        Stop,
        /// <summary>Go on: new certificates were downloaded, or the user wants to try with the old ones.</summary>
        Continue,
        /// <summary>Use a Samsung certificate for this install instead.</summary>
        SamsungInstead,
    }

    /// <summary>
    /// Says which shipped certificates have ended and when, and asks before anything is downloaded.
    /// The download runs with the window open; when the server has nothing newer, or the download
    /// fails, the same window offers the ways on. UI thread only.
    /// </summary>
    public partial class AuthorityEndedWindow : Window
    {
        private readonly Func<string, string> _localize = key => key;
        private readonly Func<Task<(AuthorityRefresh Result, string? Detail)>>? _download;
        private readonly bool _offerSamsung;
        private readonly string _endedOn = string.Empty;
        private bool _busy;

        public AuthorityEndedWindow()
        {
            InitializeComponent();
            Apps2Samsung.Helpers.DialogKeys.CloseOnEscape(this);
        }

        /// <param name="download">Fetches the newer set; null when there is nothing to fetch (a
        /// certificate that cannot be renewed), which opens the window on the ways on.</param>
        /// <param name="offerSamsung">Whether a Samsung certificate is a way on from here.</param>
        /// <param name="nothingToFetchBody">The text for the case without a download.</param>
        public AuthorityEndedWindow(AuthoritySet set, DateTimeOffset endedAt, string? tvName, bool offerSamsung,
            Func<Task<(AuthorityRefresh Result, string? Detail)>>? download, Func<string, string> localize,
            string? nothingToFetchBody = null) : this()
        {
            _localize = localize;
            _download = download;
            _offerSamsung = offerSamsung;
            _endedOn = endedAt.ToLocalTime().ToString("d MMMM yyyy");

            bool samsung = set == AuthoritySet.Samsung;
            Title = localize(samsung ? "authEndedTitleSamsung" : "authEndedTitleTizen");
            TitleBlock.Text = Title;
            SubBlock.Text = tvName ?? string.Empty;
            SubBlock.IsVisible = !string.IsNullOrEmpty(tvName);
            BodyBlock.Text = string.Format(localize(samsung ? "authEndedBodySamsung" : "authEndedBodyTizen"), _endedOn);
            HintBlock.Text = string.Format(localize("authEndedHint"), samsung ? AuthorityStore.SamsungDownloadMb : AuthorityStore.TizenDownloadMb);
            DownloadButton.Content = localize("authDownload");
            SamsungButton.Content = localize("lblSignInSamsungInstead");
            SamsungHint.Text = localize("authSamsungInsteadHint");
            TryAnywayButton.Content = localize("authTryAnyway");
            StopButton.Content = localize("authStop");

            if (download is null)
            {
                BodyBlock.IsVisible = false;
                ShowWaysOn(string.Format(localize("authNothingNewerTitle"), _endedOn), nothingToFetchBody ?? localize("authNothingNewerBody"));
            }
        }

        private async void OnDownloadClick(object? sender, RoutedEventArgs e)
        {
            if (_busy || _download is null)
                return;

            _busy = true;
            DownloadButton.IsEnabled = false;
            TryAnywayButton.IsEnabled = false;
            DownloadButton.Content = _localize("authDownloading");
            (AuthorityRefresh Result, string? Detail) outcome;
            try
            {
                outcome = await _download();
            }
            catch (Exception ex)
            {
                outcome = (AuthorityRefresh.Failed, ex.Message);
            }
            _busy = false;

            switch (outcome.Result)
            {
                case AuthorityRefresh.Replaced:
                    Close(AuthorityChoice.Continue);
                    break;
                case AuthorityRefresh.NothingNewer:
                    ShowWaysOn(string.Format(_localize("authNothingNewerTitle"), _endedOn), _localize("authNothingNewerBody"));
                    break;
                default:
                    ShowWaysOn(_localize("authDownloadFailedTitle"),
                        string.IsNullOrWhiteSpace(outcome.Detail) ? _localize("authDownloadFailedBody") : outcome.Detail!, retry: true);
                    break;
            }
        }

        // Nothing to download, or it did not work: the certificate may still be accepted by the TV,
        // so trying is offered next to the Samsung way and stopping.
        private void ShowWaysOn(string title, string body, bool retry = false)
        {
            NoticeTitle.Text = title;
            NoticeBody.Text = body;
            NoticePanel.IsVisible = true;
            HintBlock.IsVisible = false;

            DownloadButton.IsVisible = retry;
            DownloadButton.IsEnabled = true;
            DownloadButton.Content = _localize("authTryAgain");
            SamsungButton.IsVisible = _offerSamsung && !retry;
            SamsungHint.IsVisible = SamsungButton.IsVisible;
            TryAnywayButton.IsVisible = true;
            TryAnywayButton.IsEnabled = true;
        }

        private void OnSamsungClick(object? sender, RoutedEventArgs e) => Close(AuthorityChoice.SamsungInstead);

        private void OnTryAnywayClick(object? sender, RoutedEventArgs e) => Close(AuthorityChoice.Continue);

        private void OnStopClick(object? sender, RoutedEventArgs e)
        {
            // Not while the download runs: it finishes first, then the window answers.
            if (!_busy)
                Close(AuthorityChoice.Stop);
        }

        /// <summary>True when the user went on without new certificates (try anyway), as opposed to after a download.</summary>
        public bool WentOnWithEnded => NoticePanel.IsVisible;
    }
}
