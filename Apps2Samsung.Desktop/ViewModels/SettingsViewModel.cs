using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Apps2Samsung.Extensions;
using Apps2Samsung.Helpers;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Styles;
using Avalonia.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;

namespace Apps2Samsung.ViewModels
{
    /// <summary>
    /// The Settings section in the main window (#755): the update card and five rows on the left,
    /// the chosen page on the right. Installing, Language, Backup and diagnostics and Advanced are
    /// views over the one <see cref="AppSettingsViewModel"/>; App customization lists the per-app
    /// sections (<see cref="IAppSettingsProvider"/>) and App icons, and opening one of those gives
    /// it the full width with a way back.
    /// </summary>
    public partial class SettingsViewModel : ViewModelBase, IDisposable
    {
        public enum Page { Installing, AppCustomization, Language, Backup, Advanced }

        private readonly ILocalizationService _localizationService;
        private readonly IDialogService _dialogService;

        public AppSettingsViewModel Application { get; }
        public AppIconsViewModel AppIcons { get; }

        /// <summary>The rows on the left, in two groups.</summary>
        public ObservableCollection<SettingsRow> MainRows { get; } = new();
        public ObservableCollection<SettingsRow> MoreRows { get; } = new();

        /// <summary>App customization: one row per app with settings, and App icons for all of them.</summary>
        public ObservableCollection<SettingsAppRow> AppRows { get; } = new();
        public SettingsAppRow AppIconsRow { get; }

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsInstallingPage), nameof(IsAppCustomizationPage), nameof(IsLanguagePage),
            nameof(IsBackupPage), nameof(IsAdvancedPage), nameof(PageTitle))]
        private Page selectedPage = Page.Installing;

        /// <summary>A per-app page or App icons, opened from App customization; null = none.</summary>
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HasPushedPage))]
        private SettingsAppRow? pushedPage;

        public bool HasPushedPage => PushedPage is not null;
        public bool IsInstallingPage => SelectedPage == Page.Installing;
        public bool IsAppCustomizationPage => SelectedPage == Page.AppCustomization;
        public bool IsLanguagePage => SelectedPage == Page.Language;
        public bool IsBackupPage => SelectedPage == Page.Backup;
        public bool IsAdvancedPage => SelectedPage == Page.Advanced;
        public string PageTitle => MainRows.Concat(MoreRows).FirstOrDefault(r => r.Page == SelectedPage)?.Label ?? string.Empty;

        // The update card: the version, what the last manual check found, Check and the Beta toggle.
        // The check itself (and its dialogs) lives with the main window, which sets the delegate.
        // The version gets its own wrapping line: a test build's is long (3.0.0-beta.787+b2a6ca8).
        public string VersionText => $"{L("lblVersion")} {AppSettings.Default.AppVersion}";
        public string LicenseText => $"© {DateTime.Now.Year} · {L("lblLicenseFooter")}";

        [ObservableProperty]
        private string updateStatus = string.Empty;

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(CheckForUpdatesCommand))]
        private bool isCheckingForUpdates;

        public Func<Task<string>>? CheckForUpdatesHandler { get; set; }

        // Installing > Prepare the certificate now: the selected TV lives with the main window,
        // which sets the delegate (the same way as the update check).
        public Func<Task>? PrepareCertificateHandler { get; set; }

        [ObservableProperty]
        [NotifyCanExecuteChangedFor(nameof(PrepareCertificateCommand))]
        [NotifyPropertyChangedFor(nameof(PrepareCertificateText))]
        private bool isPreparingCertificate;

        /// <summary>The button: what it does, or what it is doing while the TV is read.</summary>
        public string PrepareCertificateText => L(IsPreparingCertificate ? "statusReadingTvDuid" : "lblPrepareCertificateNow");

        public string LblSigning => L("lblSigning");
        public string LblEveryInstall => L("lblEveryInstall");
        public string LblPerApp => L("lblPerApp");
        public string LblAllApps => L("lblAllApps");
        public string LblAppCustomizationHint => L("hintAppCustomization");
        public string LblBetaUpdates => L("lblBetaUpdates");
        public string LblCheckForUpdates => L("UpdateCheckNow");

        public SettingsViewModel(
            AppSettingsViewModel application,
            AppIconsViewModel appIcons,
            IEnumerable<IAppSettingsProvider> providers,
            ILocalizationService localizationService,
            IDialogService dialogService)
        {
            Application = application;
            AppIcons = appIcons;
            _localizationService = localizationService;
            _dialogService = dialogService;

            MainRows.Add(new SettingsRow(Page.Installing, "lblSettingsInstalling", "summarySettingsInstalling", "Accent",
                "M12 3v12M7 10l5 5 5-5M4 19h16"));
            MainRows.Add(new SettingsRow(Page.AppCustomization, "lblAppCustomization", "summaryAppCustomization", "TileMedia",
                "M4 4h6v6H4zM14 4h6v6h-6zM4 14h6v6H4zM14 14h6v6h-6z"));
            MainRows.Add(new SettingsRow(Page.Language, "lblLanguage", null, "TileGames",
                "M12 3a9 9 0 1 0 0 18a9 9 0 1 0 0-18M3 12h18M12 3c3.5 3.5 3.5 14.5 0 18M12 3c-3.5 3.5-3.5 14.5 0 18"));
            MoreRows.Add(new SettingsRow(Page.Backup, "lblBackupAndDiagnostics", "summaryBackupAndDiagnostics", "TileTools",
                "M4 8h16v12H4zM3 4h18v4H3zM10 12h4"));
            MoreRows.Add(new SettingsRow(Page.Advanced, "lblAdvanced", "summaryAdvanced", "TileOther",
                "M8 7l-5 5 5 5M16 7l5 5-5 5"));

            foreach (var provider in providers.OrderBy(p => p.SortOrder))
                AppRows.Add(new SettingsAppRow(() => provider.DisplayName, provider.CreateSettingsViewModel()));
            AppIconsRow = new SettingsAppRow(() => AppIcons.LblAppIcons, appIcons);

            RefreshRows();
            Application.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName is nameof(AppSettingsViewModel.SelectedLanguage))
                    RefreshRows();
            };
            _localizationService.LanguageChanged += OnLanguageChanged;
        }

        private string L(string key) => _localizationService.GetString(key);

        [RelayCommand]
        private void SelectPage(SettingsRow? row)
        {
            if (row is null)
                return;
            PushedPage = null;
            SelectedPage = row.Page;
        }

        [RelayCommand]
        private void OpenApp(SettingsAppRow? row) => PushedPage = row;

        [RelayCommand]
        private void CloseApp() => PushedPage = null;

        [RelayCommand(CanExecute = nameof(CanCheckForUpdates))]
        private async Task CheckForUpdates()
        {
            if (CheckForUpdatesHandler is null)
                return;
            IsCheckingForUpdates = true;
            UpdateStatus = "UpdateChecking".Localized();
            try
            {
                UpdateStatus = await CheckForUpdatesHandler();
            }
            finally
            {
                IsCheckingForUpdates = false;
            }
        }

        private bool CanCheckForUpdates() => !IsCheckingForUpdates;

        [RelayCommand(CanExecute = nameof(CanPrepareCertificate))]
        private async Task PrepareCertificate()
        {
            if (PrepareCertificateHandler is null)
                return;
            IsPreparingCertificate = true;
            try
            {
                await PrepareCertificateHandler();
            }
            finally
            {
                IsPreparingCertificate = false;
            }
        }

        private bool CanPrepareCertificate() => !IsPreparingCertificate;

        /// <summary>Backup and diagnostics > View debug log.</summary>
        [RelayCommand]
        private Task ViewLog() => _dialogService.ShowLogViewerAsync();

        partial void OnSelectedPageChanged(Page value)
        {
            foreach (var row in MainRows.Concat(MoreRows))
                row.IsSelected = row.Page == value;
        }

        private void RefreshRows()
        {
            foreach (var row in MainRows.Concat(MoreRows))
            {
                row.Label = L(row.LabelKey);
                row.Summary = row.Page == Page.Language
                    ? Application.SelectedLanguage?.Name ?? string.Empty
                    : row.SummaryKey is null ? string.Empty : L(row.SummaryKey);
                row.IsSelected = row.Page == SelectedPage;
            }
            foreach (var row in AppRows)
                row.RefreshDisplayName();
            AppIconsRow.RefreshDisplayName();
            OnPropertyChanged(nameof(PageTitle));
        }

        private void OnLanguageChanged(object? sender, EventArgs e)
        {
            RefreshRows();
            OnPropertyChanged(nameof(LblSigning));
            OnPropertyChanged(nameof(LblEveryInstall));
            OnPropertyChanged(nameof(LblPerApp));
            OnPropertyChanged(nameof(LblAllApps));
            OnPropertyChanged(nameof(LblAppCustomizationHint));
            OnPropertyChanged(nameof(LblBetaUpdates));
            OnPropertyChanged(nameof(LblCheckForUpdates));
            OnPropertyChanged(nameof(PrepareCertificateText));
            OnPropertyChanged(nameof(LicenseText));
        }

        public void Dispose()
        {
            _localizationService.LanguageChanged -= OnLanguageChanged;
        }
    }

    /// <summary>A row in the Settings list: a coloured icon tile, the page name and a one-line summary.</summary>
    public partial class SettingsRow : ObservableObject
    {
        public SettingsViewModel.Page Page { get; }
        public string LabelKey { get; }
        public string? SummaryKey { get; }
        /// <summary>The tile's colour token.</summary>
        public string TileToken { get; }
        /// <summary>The icon as path data on a 24 × 24 grid, stroked.</summary>
        public string IconData { get; }
        public IBrush TileBrush => DesignTheme.Brush(TileToken);

        [ObservableProperty]
        private string label = string.Empty;

        [ObservableProperty]
        private string summary = string.Empty;

        [ObservableProperty]
        private bool isSelected;

        public SettingsRow(SettingsViewModel.Page page, string labelKey, string? summaryKey, string tileToken, string iconData)
        {
            Page = page;
            LabelKey = labelKey;
            SummaryKey = summaryKey;
            TileToken = tileToken;
            IconData = iconData;
        }
    }

    /// <summary>A per-app settings page (or App icons) under App customization; the view model is
    /// shown through <c>ViewLocator</c>. The name is re-read when the UI language changes.</summary>
    public sealed class SettingsAppRow : ObservableObject
    {
        private readonly Func<string> _displayNameFactory;

        public ViewModelBase Content { get; }
        public string DisplayName => _displayNameFactory();
        public string Letter => string.IsNullOrEmpty(DisplayName) ? "?" : DisplayName[..1].ToUpperInvariant();

        public SettingsAppRow(Func<string> displayNameFactory, ViewModelBase content)
        {
            _displayNameFactory = displayNameFactory;
            Content = content;
        }

        public void RefreshDisplayName()
        {
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Letter));
        }
    }
}
