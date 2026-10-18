using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using System;
using System.Runtime.InteropServices;

namespace Apps2Samsung.ViewModels
{
    public partial class UpdateDialogViewModel : ViewModelBase
    {
        private readonly ILocalizationService _localizationService;

        [ObservableProperty]
        private string currentVersion = string.Empty;

        [ObservableProperty]
        private string latestVersion = string.Empty;

        [ObservableProperty]
        private string releaseTitle = string.Empty;

        [ObservableProperty]
        private string releaseNotes = string.Empty;

        [ObservableProperty]
        private DateTime? publishedAt;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowRateLimitNote))]
        [NotifyCanExecuteChangedFor(nameof(SelectAutomaticCommand))]
        private bool hasDownloadUrl;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ShowInstallerManagedNote))]
        [NotifyPropertyChangedFor(nameof(ShowRateLimitNote))]
        [NotifyCanExecuteChangedFor(nameof(SelectAutomaticCommand))]
        private bool automaticUpdateSupported = true;

        [ObservableProperty]
        private bool isDownloading;

        [ObservableProperty]
        private int downloadProgress;

        [ObservableProperty]
        private string downloadStatus = string.Empty;

        /// <summary>
        /// The user's choice after interacting with the dialog.
        /// </summary>
        public UpdateDialogChoice? DialogResult { get; private set; }

        /// <summary>
        /// Event raised when the dialog should be closed.
        /// </summary>
        public event EventHandler? RequestClose;

        // Localized strings
        public string L(string key) => _localizationService.GetString(key);
        public string DialogTitle => L("UpdateAvailable");
        public string CurrentVersionLabel => L("UpdateCurrentVersion");
        public string LatestVersionLabel => L("UpdateLatestVersion");
        public string ReleaseNotesLabel => L("UpdateReleaseNotes");
        public string ManualButtonText => L("UpdateManual");
        public string AutomaticButtonText => L("UpdateAutomatic");
        public string SkipButtonText => L("UpdateSkip");
        public string DownloadingText => L("UpdateDownloading");

        // Note shown when the install is managed by an OS installer/package manager,
        // so the automatic in-place update is hidden and the user is pointed at the right channel.
        public bool ShowInstallerManagedNote => !AutomaticUpdateSupported;
        public string InstallerManagedNote => L(InstallerManagedNoteKey);

        // Note shown when automatic update is supported in principle but no download URL
        // was resolved (e.g. GitHub API rate limited).
        public bool ShowRateLimitNote => AutomaticUpdateSupported && !HasDownloadUrl;

        private static string InstallerManagedNoteKey
        {
            get
            {
                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                    return "UpdateInstallerManagedWindows";
                if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
                    return "UpdateInstallerManagedMac";
                return "UpdateInstallerManagedLinux";
            }
        }

        public UpdateDialogViewModel(ILocalizationService localizationService)
        {
            _localizationService = localizationService;
        }

        public void Initialize(UpdateCheckResult updateInfo)
        {
            CurrentVersion = updateInfo.CurrentVersion;
            LatestVersion = updateInfo.LatestVersion;
            ReleaseTitle = updateInfo.ReleaseTitle;
            ReleaseNotes = updateInfo.ReleaseNotes;
            PublishedAt = updateInfo.PublishedAt;
            HasDownloadUrl = !string.IsNullOrEmpty(updateInfo.DownloadUrl);
            AutomaticUpdateSupported = updateInfo.SupportsAutomaticUpdate;
        }

        [RelayCommand]
        private void SelectManual()
        {
            DialogResult = UpdateDialogChoice.Manual;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand(CanExecute = nameof(CanSelectAutomatic))]
        private void SelectAutomatic()
        {
            DialogResult = UpdateDialogChoice.Automatic;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void SelectSkip()
        {
            DialogResult = UpdateDialogChoice.Skip;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        [RelayCommand]
        private void Cancel()
        {
            DialogResult = UpdateDialogChoice.Cancel;
            RequestClose?.Invoke(this, EventArgs.Empty);
        }

        private bool CanSelectAutomatic() => HasDownloadUrl && AutomaticUpdateSupported && !IsDownloading;

        public void UpdateDownloadProgress(int progress, string status)
        {
            DownloadProgress = progress;
            DownloadStatus = status;
        }
    }
}
